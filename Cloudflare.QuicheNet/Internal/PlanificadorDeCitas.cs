using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// Algo que tiene que pasar a una hora: el ping de un WebSocket que calla, el plazo de un pong,
    /// el de un cierre. La hora va en la escala de quien la programa: los milisegundos de
    /// <see cref="Environment.TickCount64"/> en el <see cref="PlanificadorDeCitas"/>, y los nanosegundos
    /// del reloj de <c>DespertadorPreciso</c> en ese.
    /// </summary>
    internal abstract class Cita
    {
        /// <summary>
        /// La hora a la que vence, o <see cref="long.MaxValue"/> si no está programada. La guarda el
        /// planificador, bajo su candado y con escrituras volátiles —atómicas también en 32 bits—, porque el
        /// camino rápido de <see cref="PlanificadorDeCitas.Programar"/> la lee sin él.
        /// </summary>
        internal long ProgramadaPara = long.MaxValue;

        /// <summary>Su sitio en el montículo, o -1 si no está en él. Solo lo toca el planificador, bajo su candado.</summary>
        internal int Indice = -1;

        /// <summary>
        /// Ha llegado la hora. Devuelve la siguiente, o <see cref="long.MaxValue"/> para no volver.
        /// <para>
        /// Corre en el bucle del planificador, que es de todos: tiene que volver enseguida. Lo que
        /// tarde —mandar un ping— se lanza y no se espera.
        /// </para>
        /// <para>
        /// Es aquí donde se hace el plazo <b>perezoso</b>: quien tuvo actividad entre medias no toca el
        /// planificador —apunta la hora y ya—, y al vencer se mira si de verdad tocaba o se devuelve
        /// la hora buena.
        /// </para>
        /// </summary>
        protected internal abstract long Vencer(long ahora);
    }

    /// <summary>
    /// Un trabajo que se repite cada tanto —podar un pool, barrer conexiones ociosas—, como cita: el
    /// bucle del planificador solo decide que toca, y el trabajo va al pool de hilos, porque ese bucle
    /// es de todos y no puede esperar a nadie. Si el anterior aún no acabó, la vuelta se salta: no se
    /// apilan barridos sobre uno lento.
    /// </summary>
    internal sealed class CitaPeriodica : Cita, IThreadPoolWorkItem
    {
        private readonly Action _trabajo;
        private readonly long _periodoMs;
        private readonly Action<string>? _log;
        private readonly object _candado = new();

        /// <summary>La vuelta en marcha, o null. Se completa al acabar: es lo que espera quien detiene.</summary>
        private TaskCompletionSource? _vuelta;
        private bool _parada;

        public CitaPeriodica(Action trabajo, TimeSpan periodo, Action<string>? log = null)
        {
            _trabajo   = trabajo;
            _periodoMs = Math.Max(1, (long)periodo.TotalMilliseconds);
            _log       = log;
        }

        /// <summary>La programa en <paramref name="planificador"/>, que vence al cabo de un periodo.</summary>
        public CitaPeriodica Empezar(PlanificadorDeCitas planificador)
        {
            planificador.Programar(this, Environment.TickCount64 + _periodoMs);
            return this;
        }

        /// <summary>
        /// Que no vuelva, y la vuelta en marcha si la hay: quien detiene espera a que acabe antes de
        /// soltar lo que ella toca —los pools—, como esperaba antes al bucle.
        /// </summary>
        public Task DetenerAsync(PlanificadorDeCitas planificador)
        {
            planificador.Anular(this);
            lock (_candado)
            {
                _parada = true;
                return _vuelta?.Task ?? Task.CompletedTask;
            }
        }

        protected internal override long Vencer(long ahora)
        {
            lock (_candado)
            {
                if (_parada) return long.MaxValue;
                if (_vuelta is null)
                {
                    _vuelta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
                }
            }
            return ahora + _periodoMs;
        }

        void IThreadPoolWorkItem.Execute()
        {
            try { _trabajo(); }
            catch (Exception ex) { _log?.Invoke($"[planificador] un trabajo periódico falló: {ex.Message}"); }
            finally
            {
                TaskCompletionSource? vuelta;
                lock (_candado)
                {
                    vuelta  = _vuelta;
                    _vuelta = null;
                }
                vuelta?.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Una cita que no hace su trabajo en el bucle del planificador, que es de todos: se lo encarga al pool de hilos —la propia
    /// cita es el encargo, sin reservar nada—. Un ping escribe en la red y un corte despierta a quien espera, y ninguna de las
    /// dos cosas debe retrasar las citas de los demás. Lo comparten los vigilantes de vida del socket y del canal y el latido de
    /// STOMP, que lo tenían copiado.
    /// </summary>
    internal abstract class CitaConEncargo : Cita, IThreadPoolWorkItem
    {
        /// <summary>Ningún encargo pendiente.</summary>
        protected const int Ninguno = 0;

        private int _encargo;

        /// <summary>Encarga <paramref name="encargo"/>, distinto de <see cref="Ninguno"/>; uno aún pendiente se sustituye.</summary>
        protected void Encargar(int encargo)
        {
            Volatile.Write(ref _encargo, encargo);
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        /// <summary>Lo encargado, ya en un hilo del pool.</summary>
        protected abstract void Hacer(int encargo);

        void IThreadPoolWorkItem.Execute()
        {
            int encargo = Interlocked.Exchange(ref _encargo, Ninguno);
            if (encargo != Ninguno) Hacer(encargo);
        }
    }

    /// <summary>
    /// Un solo bucle para miles de citas: un montículo ordenado por la hora de vencer y una espera
    /// hasta la primera, sin temporizador por conexión ni tic fijo.
    /// <para>
    /// Sin esto, cada WebSocket vivo llevaría su temporizador para el ping y cada conexión h2 su
    /// bucle de medio segundo: diez mil sockets son diez mil despertares, y en un móvil cada
    /// despertar es batería. Con el montículo, el bucle duerme hasta la próxima cita de verdad y
    /// cada una cuesta un O(log n) al programarse.
    /// </para>
    /// <para>
    /// El montículo es propio y cada cita sabe su sitio en él: adelantarla la sube donde está, y anularla la
    /// saca de verdad. Con el de la biblioteca —sin quitar— una cita anulada seguía dentro hasta su hora, y con
    /// ella lo que retuviera: una conexión h2 cerrada se quedaba en memoria los 130 s de su plazo de ociosa.
    /// </para>
    /// </summary>
    internal sealed class PlanificadorDeCitas : IDisposable
    {
        private static readonly Lazy<PlanificadorDeCitas> _compartido =
            new(() => new PlanificadorDeCitas(log: null), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>El del proceso. Es el que usan los WebSocket y las conexiones h2.</summary>
        public static PlanificadorDeCitas Compartido => _compartido.Value;

        private MonticuloDeCitas _monticulo = new(64);
        private readonly object _candado = new();
        private readonly SemaphoreSlim _despertar = new(0, 1);
        private readonly List<Cita> _vencidas = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Action<string>? _log;
        private readonly Task _bucle;

        /// <summary>
        /// Hasta cuándo duerme el bucle: la hora de la primera cita cuando se echó a dormir, <see cref="long.MaxValue"/> sin
        /// ninguna, y <see cref="long.MinValue"/> mientras está despierto —va a mirar el montículo otra vez antes de
        /// dormir—. Con el candado. Solo lo despierta una cita que vence antes: despertarlo por cada cita que pasaba a ser
        /// la primera le costaba una vuelta de más por cada una que él mismo volvía a programar al vencer.
        /// </summary>
        private long _dormidoHasta = long.MinValue;

        public PlanificadorDeCitas(Action<string>? log)
        {
            _log   = log;
            _bucle = Task.Run(() => GirarAsync(_cts.Token));
        }

        /// <summary>Citas programadas.</summary>
        public int Pendientes { get { lock (_candado) return _monticulo.Cuantas; } }

        /// <summary>Cuántas han vencido desde que empezó: las pruebas cuentan así los despertares.</summary>
        internal long Vencidas => Interlocked.Read(ref _vencidasTotal);
        private long _vencidasTotal;

        /// <summary>
        /// Que <paramref name="cita"/> venza a las <paramref name="cuando"/>. Solo adelanta: si ya
        /// vencía antes, no cambia nada —el retraso lo decide la propia cita al vencer—.
        /// <para>
        /// Lo de no cambiar nada va sin el candado, que es de todos: una conexión h2 lo pide en cada
        /// petición que abre, y casi siempre ya vencía antes. Las dos barreras hacen que no se pierda
        /// nada: la de aquí ordena lo que quien llama cambió —lo que la cita mirará al vencer— antes
        /// de leer la hora, y la del bucle, entre sacar las vencidas y vencerlas, lo contrario. O esta
        /// lectura ve que la cita ya salió, y la programa, o la cita ve el cambio al vencer.
        /// </para>
        /// </summary>
        public void Programar(Cita cita, long cuando)
        {
            Interlocked.MemoryBarrier();
            if (cuando >= Volatile.Read(ref cita.ProgramadaPara)) return;

            bool adelanta;
            lock (_candado)
            {
                if (cuando >= cita.ProgramadaPara) return;
                _monticulo.Poner(cita, cuando);   // solo adelanta: sube, nunca baja
                // Solo si vence antes de cuando el bucle iba a despertar: la hora nueva es la de su sueño, y otra cita
                // que llegue antes de que despierte no lo vuelve a despertar.
                adelanta = cuando < _dormidoHasta;
                if (adelanta) _dormidoHasta = cuando;
            }
            if (adelanta) Despertar();
        }

        /// <summary>Que no venza, y fuera del montículo: ya no retiene nada.</summary>
        public void Anular(Cita cita)
        {
            lock (_candado) _monticulo.Quitar(cita);
        }

        /// <summary>Cuántas veces se avisó al bucle de que madrugue: para las pruebas.</summary>
        internal long Despertares => Interlocked.Read(ref _despertares);
        private long _despertares;

        private void Despertar()
        {
            Interlocked.Increment(ref _despertares);
            try { if (_despertar.CurrentCount == 0) _despertar.Release(); }
            catch (SemaphoreFullException)  { }
            catch (ObjectDisposedException) { }
        }

        private async Task GirarAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                long ahora = Environment.TickCount64;
                int espera = Timeout.Infinite;

                lock (_candado)
                {
                    _dormidoHasta = long.MinValue;   // despierto: lo que se programe ahora lo ve la vuelta siguiente
                    while (_monticulo.Cuantas > 0 && _monticulo.Primera.ProgramadaPara <= ahora)
                    {
                        var cita = _monticulo.Primera;
                        _monticulo.Quitar(cita);
                        _vencidas.Add(cita);
                    }
                    if (_vencidas.Count == 0)
                    {
                        if (_monticulo.Cuantas > 0) espera = (int)Math.Clamp(_monticulo.Primera.ProgramadaPara - ahora, 1, int.MaxValue);
                        _dormidoHasta = _monticulo.Cuantas > 0 ? _monticulo.Primera.ProgramadaPara : long.MaxValue;
                    }
                    _monticulo.Recortar();
                }

                if (_vencidas.Count > 0)
                {
                    // La otra mitad del camino rápido de Programar: ver allí.
                    Interlocked.MemoryBarrier();
                    Interlocked.Add(ref _vencidasTotal, _vencidas.Count);
                    foreach (var cita in _vencidas)
                    {
                        long proxima;
                        try { proxima = cita.Vencer(ahora); }
                        catch (Exception ex)
                        {
                            // Una cita que lanza no puede parar las de los demás.
                            _log?.Invoke($"[planificador] {cita.GetType().Name} lanzó al vencer: {ex.Message}");
                            proxima = long.MaxValue;
                        }
                        if (proxima != long.MaxValue) Programar(cita, proxima);
                    }
                    _vencidas.Clear();
                    continue;   // el tiempo pasó mientras vencían: se mira otra vez antes de dormir
                }

                try { await _despertar.WaitAsync(espera, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _bucle.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
