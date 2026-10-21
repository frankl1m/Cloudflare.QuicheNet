using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Cloudflare.QuicheNet.Internal;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// Una cola acotada de un solo productor y un solo consumidor, con lo que se hace al llenarse elegido por
    /// <see cref="QueuePolicy"/>:
    /// <list type="bullet">
    /// <item><see cref="QueuePolicy.Bloquear"/>: el productor espera sitio. Nada se pierde, y la espera llega hasta
    /// quien produce —el bucle de lectura de un WebSocket deja de leer, y el TCP frena al servidor—.</item>
    /// <item><see cref="QueuePolicy.DescartarViejos"/>: sale el más viejo para que entre el nuevo.</item>
    /// <item><see cref="QueuePolicy.UltimoPorClave"/>: lo que trae una clave que ya espera ocupa su sitio, en el
    /// orden en que llegó la primera vez —el último precio de cada símbolo, no todos—; lo que no trae clave no se
    /// funde nunca. Llena de claves distintas, sale el más viejo.</item>
    /// </list>
    /// <para>
    /// Lo que sale sin leerse se entrega a <c>descartar</c>, que para un mensaje con búfer alquilado es
    /// devolverlo al pool. Las esperas no asignan: una <see cref="EsperaReutilizable"/> para cada lado, que se puede
    /// cancelar.
    /// </para>
    /// <para>
    /// Es la cola de cada lector de un canal, la de los envíos que esperan conexión en una suscripción y la de los
    /// flujos de los protocolos.
    /// </para>
    /// <para>
    /// Y la de los datagramas —los de cada conexión QUIC, y los de cada sesión UDP sobre QUIC, en los dos lados—, con
    /// elementos por valor: el datagrama y su búfer prestado, sin un objeto por cada uno. Ahí escriben a veces varios a la
    /// vez, siempre con <see cref="TryEscribir"/>, que nunca espera: el candado basta. Con
    /// <see cref="QueuePolicy.Bloquear"/> lo que no cabe no entra, y sigue siendo de quien lo trae.
    /// </para>
    /// </summary>
    internal sealed class ColaAcotada<T>
    {
        private readonly object _candado = new();

        /// <summary>
        /// Lo que cabe como mucho. El almacén crece hasta aquí según se usa, y vacío tras una ráfaga vuelve a su tamaño de
        /// reposo: reservaba la capacidad entera al crearse, y con la de omisión —1024— eran 8 KB por cola, también por
        /// cada consulta de graphql-ws o cada flujo de SignalR que nunca pasan de un puñado.
        /// </summary>
        private readonly int _capacidad;
        private T?[] _items;
        private string?[]? _claves;
        private readonly Dictionary<string, long>? _dondeEsta;
        private readonly QueuePolicy _politica;
        private readonly Func<T, string?>? _claveDe;
        private readonly Action<T>? _descartar, _soltar;

        /// <summary>Posiciones absolutas: se lee en <see cref="_cabeza"/> y se escribe en <see cref="_cola"/>.</summary>
        private long _cabeza, _cola;
        private bool _completada;
        private Exception? _error;
        private long _descartados;

        private readonly EsperaReutilizable _lector = new(), _escritor = new();
        private bool _lectorEspera, _escritorEspera;

        /// <param name="descartar">Lo que se hace con lo que sale sin leerse por la política: se cuenta como descartado.</param>
        /// <param name="soltar">Lo que se hace con lo que queda al <see cref="Vaciar"/>; si no, <paramref name="descartar"/>.</param>
        public ColaAcotada(int capacidad, QueuePolicy politica = QueuePolicy.Bloquear,
                           Func<T, string?>? claveDe = null, Action<T>? descartar = null, Action<T>? soltar = null)
        {
            if (capacidad < 1) throw new ArgumentOutOfRangeException(nameof(capacidad));
            if (politica == QueuePolicy.UltimoPorClave && claveDe is null)
                throw new ArgumentException("UltimoPorClave necesita cómo sacar la clave", nameof(claveDe));
            _capacidad = capacidad;
            _items     = new T?[Math.Min(capacidad, EnReposo)];
            _politica  = politica;
            _claveDe   = claveDe;
            _descartar = descartar;
            _soltar    = soltar ?? descartar;
            if (politica == QueuePolicy.UltimoPorClave)
            {
                _claves    = new string?[_items.Length];
                _dondeEsta = new Dictionary<string, long>(StringComparer.Ordinal);
            }
        }

        public int Capacidad => _capacidad;

        /// <summary>El almacén de una cola que no espera nada: el tamaño con que empieza y al que vuelve.</summary>
        private const int EnReposo = 8;

        /// <summary>Lo que ocupa ahora el almacén, en huecos: para las pruebas.</summary>
        internal int Huecos { get { lock (_candado) return _items.Length; } }

        /// <summary>Cuántos esperan a ser leídos.</summary>
        public int Cuantos { get { lock (_candado) return (int)(_cola - _cabeza); } }

        /// <summary>Los que salieron sin leerse —por viejos o porque llegó otro con su clave—.</summary>
        public long Descartados => Interlocked.Read(ref _descartados);

        public bool Completada { get { lock (_candado) return _completada; } }

        private int Hueco(long posicion) => (int)(posicion % _items.Length);

        // ── Escribir ─────────────────────────────────────────────────────────

        /// <summary>
        /// Deja <paramref name="item"/> en la cola. Con <see cref="QueuePolicy.Bloquear"/> y llena, espera sitio.
        /// <c>false</c> si la cola ya se completó: entonces no se lo ha quedado, y es de quien llama.
        /// </summary>
        public ValueTask<bool> EscribirAsync(T item, CancellationToken ct = default)
            => EscribirAsync(item, ClaveDe(item), ct);

        /// <summary>
        /// Lo mismo con la clave ya sacada: quien reparte el mismo mensaje a varias colas la saca una vez, no una por
        /// cola. Sin <see cref="QueuePolicy.UltimoPorClave"/> no se mira.
        /// </summary>
        public ValueTask<bool> EscribirAsync(T item, string? clave, CancellationToken ct)
        {
            var r = IntentarEscribir(item, clave);
            if (r != Resultado.Llena) return new ValueTask<bool>(r == Resultado.Hecho);
            return EscribirEsperandoAsync(item, clave, ct);
        }

        /// <summary>
        /// Deja <paramref name="item"/> sin esperar nunca: con <see cref="QueuePolicy.Bloquear"/> y llena, no entra y
        /// devuelve <c>false</c>, igual que si la cola ya se completó.
        /// </summary>
        public bool TryEscribir(T item) => IntentarEscribir(item, ClaveDe(item)) == Resultado.Hecho;

        /// <summary>
        /// La clave de <paramref name="item"/> con <see cref="QueuePolicy.UltimoPorClave"/>; null sin ella. Fuera del
        /// candado: es código de quien usa la cola. Si lanza, el mensaje va sin clave —no se funde— y la cola sigue.
        /// </summary>
        public string? ClaveDe(T item)
        {
            if (_politica != QueuePolicy.UltimoPorClave) return null;
            try { return _claveDe!(item); }
            catch { return null; }
        }

        private enum Resultado { Hecho, Llena, Completada }

        private Resultado IntentarEscribir(T item, string? clave)
        {
            T? tirado = default;
            bool hayTirado = false, despertar;
            lock (_candado)
            {
                if (_completada) return Resultado.Completada;
                if (_politica != QueuePolicy.UltimoPorClave) clave = null;

                if (clave != null && _dondeEsta!.TryGetValue(clave, out long pos) && pos >= _cabeza)
                {
                    // Uno de la misma clave ya espera: este ocupa su sitio, que es el de la primera llegada.
                    int h = Hueco(pos);
                    tirado = _items[h];
                    hayTirado = true;
                    _items[h] = item;
                    despertar = false;   // no cambia cuántos hay: quien lee ya lo sabía
                }
                else
                {
                    if (_cola - _cabeza == _items.Length && _items.Length < _capacidad) Crecer();
                    if (_cola - _cabeza == _capacidad)
                    {
                        if (_politica == QueuePolicy.Bloquear) return Resultado.Llena;
                        tirado = SacarCabeza();
                        hayTirado = true;
                    }
                    int h = Hueco(_cola);
                    _items[h] = item;
                    if (_claves != null)
                    {
                        _claves[h] = clave;
                        if (clave != null) _dondeEsta![clave] = _cola;
                    }
                    _cola++;
                    despertar = _lectorEspera;
                    _lectorEspera = false;
                }
            }
            if (despertar) _lector.Completar(true);
            if (hayTirado) Descartar(tirado!);
            return Resultado.Hecho;
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<bool> EscribirEsperandoAsync(T item, string? clave, CancellationToken ct)
        {
            while (true)
            {
                ValueTask<bool> espera;
                lock (_candado)
                {
                    if (_completada) return false;
                    if (_cola - _cabeza < _capacidad) espera = new ValueTask<bool>(true);
                    else
                    {
                        _escritorEspera = true;
                        espera = _escritor.Armar(ct);
                    }
                }
                if (!await espera.ConfigureAwait(false)) return false;

                var r = IntentarEscribir(item, clave);
                if (r != Resultado.Llena) return r == Resultado.Hecho;
            }
        }

        /// <summary>
        /// El almacén al doble, sin pasar de la capacidad, con el candado tomado. Las posiciones son absolutas: cada una se
        /// recoloca en su hueco del almacén nuevo, y las de las claves no cambian.
        /// </summary>
        private void Crecer()
        {
            int nuevo = (int)Math.Min(_capacidad, (long)_items.Length * 2);
            var items = new T?[nuevo];
            var claves = _claves is null ? null : new string?[nuevo];
            for (long p = _cabeza; p < _cola; p++)
            {
                int viejo = (int)(p % _items.Length), sitio = (int)(p % nuevo);
                items[sitio] = _items[viejo];
                if (claves != null) claves[sitio] = _claves![viejo];
            }
            _items = items;
            _claves = claves;
        }

        /// <summary>Saca el de la cabeza, con el candado tomado.</summary>
        private T SacarCabeza()
        {
            int h = Hueco(_cabeza);
            var item = _items[h]!;
            _items[h] = default;
            if (_claves != null)
            {
                if (_claves[h] is { } clave && _dondeEsta!.TryGetValue(clave, out long pos) && pos == _cabeza)
                    _dondeEsta.Remove(clave);
                _claves[h] = null;
            }
            _cabeza++;
            // Vacía tras una ráfaga, vuelve a su tamaño de reposo: si no, retendría el almacén de su peor momento.
            if (_cola == _cabeza && _items.Length > 8 * EnReposo)
            {
                _items = new T?[EnReposo];
                if (_claves != null) _claves = new string?[EnReposo];
            }
            return item;
        }

        private void Descartar(T item)
        {
            Interlocked.Increment(ref _descartados);
            try { _descartar?.Invoke(item); } catch { /* descartar no puede parar la cola */ }
        }

        // ── Leer ─────────────────────────────────────────────────────────────

        public bool TryLeer([MaybeNullWhen(false)] out T item)
        {
            bool despertar;
            lock (_candado)
            {
                if (_cola == _cabeza) { item = default; return false; }
                item = SacarCabeza();
                despertar = _escritorEspera;
                _escritorEspera = false;
            }
            if (despertar) _escritor.Completar(true);
            return true;
        }

        /// <summary>
        /// Espera a que haya algo que leer: <c>true</c> si lo hay —o puede haberlo: se mira con <see cref="TryLeer"/>—,
        /// <c>false</c> cuando la cola se completó y ya no queda nada. Si se completó con error, lo lanza al vaciarse.
        /// </summary>
        public ValueTask<bool> EsperarAsync(CancellationToken ct = default)
        {
            lock (_candado)
            {
                if (_cola != _cabeza) return new ValueTask<bool>(true);
                if (_completada)
                    return _error is null ? new ValueTask<bool>(false) : ValueTask.FromException<bool>(_error);
                _lectorEspera = true;
                return _lector.Armar(ct);
            }
        }

        /// <summary>Todo lo que llegue, hasta que se complete y se vacíe.</summary>
        public async IAsyncEnumerable<T> LeerTodosAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            while (await EsperarAsync(ct).ConfigureAwait(false))
                while (TryLeer(out var item))
                    yield return item;
        }

        // ── Completar ────────────────────────────────────────────────────────

        /// <summary>
        /// Ya no entra nada más; lo que queda se sigue leyendo. Con <paramref name="error"/>, el lector lo recibe al
        /// vaciarla. Quien esperaba sitio vuelve con <c>false</c>. Dos veces no hace nada.
        /// </summary>
        public void Completar(Exception? error = null)
        {
            bool lector, escritor;
            lock (_candado)
            {
                if (_completada) return;
                _completada = true;
                _error = error;
                lector = _lectorEspera;     _lectorEspera = false;
                escritor = _escritorEspera; _escritorEspera = false;
            }
            if (lector)
            {
                if (error is null) _lector.Completar(false);
                else _lector.Fallar(error);
            }
            if (escritor) _escritor.Completar(false);
        }

        /// <summary>
        /// Completa y suelta lo que quedaba sin leer: para quien deja de leer y no va a volver. No cuenta como descartado:
        /// no se perdió por ir despacio.
        /// </summary>
        public void Vaciar()
        {
            Completar();
            while (TryLeer(out var item))
            {
                try { _soltar?.Invoke(item); } catch { }
            }
        }
    }
}
