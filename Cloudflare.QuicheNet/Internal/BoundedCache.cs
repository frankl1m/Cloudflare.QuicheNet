using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// Caché concurrente con tope real de tamaño y evicción LRU aproximada.
    /// <para>
    /// Sustituye al patrón «<c>ConcurrentDictionary</c> + comprobar <c>Count</c>» que
    /// tenían la caché DNS, la de certificados y el mapa sticky de remap. Ese patrón
    /// tenía dos defectos graves: <c>ConcurrentDictionary.Count</c> adquiere
    /// <b>todos</b> los locks (bloquea a todos los escritores), y al llenarse la caché
    /// simplemente dejaba de admitir entradas nuevas — es decir, se apagaba sola.
    /// </para>
    /// <para>
    /// Aquí el tamaño se lleva con <see cref="Interlocked"/> (sin locks) y al llegar al
    /// tope se desalojan primero las caducadas y, si aún sobra, las menos usadas
    /// recientemente. Nunca se rechaza una entrada nueva.
    /// </para>
    /// </summary>
    internal sealed class BoundedCache<TKey, TValue> where TKey : notnull
    {
        private sealed class Node
        {
            public readonly TValue Value;

            /// <summary>
            /// Número de secuencia del último uso. Es un contador propio y no un reloj:
            /// <see cref="Environment.TickCount64"/> tiene ~15,6 ms de resolución en
            /// Windows, así que cientos de inserciones seguidas comparten marca y el
            /// orden LRU se vuelve arbitrario — hasta el punto de desalojar la entrada
            /// recién insertada.
            /// </summary>
            public long Seq;

            public Node(TValue value, long seq) { Value = value; Seq = seq; }
        }

        private long _clock;
        private long NextSeq() => Interlocked.Increment(ref _clock);

        /// <summary>
        /// Cuánto puede envejecer la marca de un nodo antes de refrescarla. Se calcula del
        /// tope: con capacidad cero —sin límite— no hay desalojo, así que tampoco hace
        /// falta orden y el margen se pone alto.
        /// </summary>
        private readonly long _margenLru;

        private readonly ConcurrentDictionary<TKey, Node> _map;
        private readonly int                              _capacity;
        private readonly Func<TValue, bool>?              _isExpired;
        private readonly Action<TKey, TValue>?            _onEvicted;

        private int  _count;
        private int  _evicting;   // 0/1 — evita que N hilos barran a la vez
        private long _evictions;

        /// <param name="capacity">Tope de entradas. &lt;= 0 = sin límite.</param>
        /// <param name="isExpired">Opcional: identifica entradas caducadas, que se desalojan primero.</param>
        /// <param name="onEvicted">Opcional: se invoca al desalojar (p.ej. para <c>Dispose</c>).</param>
        public BoundedCache(int capacity,
                            IEqualityComparer<TKey>? comparer = null,
                            Func<TValue, bool>?      isExpired = null,
                            Action<TKey, TValue>?    onEvicted = null)
        {
            _capacity  = capacity;
            _margenLru = capacity > 0 ? Math.Max(16, capacity / 16) : long.MaxValue;
            _isExpired = isExpired;
            _onEvicted = onEvicted;
            _map = comparer is null
                ? new ConcurrentDictionary<TKey, Node>()
                : new ConcurrentDictionary<TKey, Node>(comparer);
        }

        /// <summary>Entradas actuales. O(1) real — no toma locks.</summary>
        public int Count => Volatile.Read(ref _count);

        /// <summary>Total de entradas desalojadas desde el arranque.</summary>
        public long Evictions => Interlocked.Read(ref _evictions);

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                // Marca de uso para el LRU. Escritura suelta a propósito: no importa
                // perder actualizaciones concurrentes, solo es una heurística.
                //
                // Y no se refresca en CADA acierto. El mapa es un ConcurrentDictionary, que
                // no toma ni un lock para leer, así que este Interlocked sobre un contador
                // único era lo ÚNICO que impedía que las lecturas escalaran: una línea de
                // caché rebotando entre todos los núcleos en cada acierto del resolver DNS,
                // de la caché de certificados del MITM y del mapa sticky de remap.
                //
                // Con el margen de abajo se refresca solo cuando la marca se ha quedado
                // vieja de verdad. El desalojo trabaja por percentiles sobre el 10% menos
                // usado, así que esta resolución le sobra de largo, y a cambio se quitan
                // más del 90% de las escrituras compartidas.
                if (Volatile.Read(ref _clock) - Volatile.Read(ref node.Seq) > _margenLru)
                    node.Seq = NextSeq();

                value = node.Value;
                return true;
            }
            value = default!;
            return false;
        }

        /// <summary>Inserta o reemplaza. Nunca rechaza: si hay que hacer sitio, se desaloja.</summary>
        public void Set(TKey key, TValue value)
        {
            var node = new Node(value, NextSeq());

            // Bucle de reintento en vez de «mirar y luego escribir»: entre el
            // TryGetValue y el indexador, otro hilo podía eliminar la clave, y
            // reinsertarla con el indexador no incrementaba el contador. El recuento
            // se quedaba por debajo del real y la caché crecía por encima del tope.
            while (true)
            {
                if (_map.TryGetValue(key, out var current))
                {
                    // Reemplazo condicional: solo cuenta como reemplazo si la entrada
                    // que vimos sigue ahí.
                    if (_map.TryUpdate(key, node, current))
                    {
                        // El valor que acaba de salir de la caché ya no lo alcanza
                        // nadie, así que hay que avisarlo igual que en un desalojo. Sin
                        // esto, un valor con recursos nativos —un X509Certificate2 con
                        // su clave CNG, que es lo que guarda la caché de certificados
                        // del MITM— se quedaba vivo hasta el finalizador cada vez que
                        // dos conexiones simultáneas al mismo host nuevo se pisaban.
                        _onEvicted?.Invoke(key, current.Value);
                        return;
                    }
                    continue;   // cambió bajo nuestros pies: reintentar
                }

                if (_map.TryAdd(key, node))
                {
                    int n = Interlocked.Increment(ref _count);
                    if (_capacity > 0 && n > _capacity) Evict();
                    return;
                }
                // Otro hilo la añadió justo ahora: volver a intentarlo como reemplazo.
            }
        }

        /// <summary>
        /// Devuelve el valor asociado a <paramref name="key"/>, o inserta el que
        /// fabrique <paramref name="factory"/> si no había ninguno. Gana <b>un solo</b>
        /// llamante: el resto recibe el suyo y el candidato perdedor no llega a entrar
        /// en la caché.
        /// <para>
        /// Existe porque <see cref="Set"/> no sirve para eso: pisa lo que hubiera, así
        /// que dos hilos que fabriquen a la vez para la misma clave acaban con dos
        /// valores vivos y solo uno alcanzable. Cuando el valor tiene recursos —la
        /// caché de certificados por hostname— eso es una fuga, y es justo el caso que
        /// más se da: las conexiones a un host nuevo llegan en ráfaga.
        /// </para>
        /// <para>
        /// La fábrica puede ejecutarse más de una vez si varios hilos entran a la vez;
        /// lo que se garantiza es que solo un resultado queda en la caché. Envolver el
        /// valor en un <see cref="Lazy{T}"/> —como hace la caché de certificados— hace
        /// que además la construcción cara ocurra una sola vez.
        /// </para>
        /// </summary>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
        {
            if (_map.TryGetValue(key, out var found))
            {
                found.Seq = NextSeq();
                return found.Value;
            }

            var node = new Node(factory(key), NextSeq());
            while (true)
            {
                if (_map.TryAdd(key, node))
                {
                    int n = Interlocked.Increment(ref _count);
                    if (_capacity > 0 && n > _capacity) Evict();
                    return node.Value;
                }

                if (_map.TryGetValue(key, out var winner))
                {
                    // Ha ganado otro: el nuestro no ha llegado a estar en la caché, así
                    // que se avisa aquí para que quien lo fabricó pueda soltarlo.
                    _onEvicted?.Invoke(key, node.Value);
                    winner.Seq = NextSeq();
                    return winner.Value;
                }
                // Se lo han quitado entre medias: volver a intentar la inserción.
            }
        }

        public bool TryRemove(TKey key, out TValue value)
        {
            if (_map.TryRemove(key, out var node))
            {
                Interlocked.Decrement(ref _count);
                value = node.Value;
                return true;
            }
            value = default!;
            return false;
        }

        /// <summary>
        /// Elimina la clave solo si sigue asociada al valor indicado.
        /// Evita el TOCTOU de «leo caducada → borro» cuando otro hilo ya la refrescó.
        /// </summary>
        public bool TryRemoveIfValueIs(TKey key, TValue expected)
        {
            if (!_map.TryGetValue(key, out var node)) return false;
            if (!EqualityComparer<TValue>.Default.Equals(node.Value, expected)) return false;

            if (_map.TryRemove(new KeyValuePair<TKey, Node>(key, node)))
            {
                Interlocked.Decrement(ref _count);
                return true;
            }
            return false;
        }

        public void Clear()
        {
            foreach (var kv in _map) Desalojar(kv);
        }

        /// <summary>
        /// Quita la entrada —solo si sigue siendo la misma— y hace lo que hay que hacer al
        /// desalojar: bajar la cuenta y avisar a <c>onEvicted</c>.
        /// <para>
        /// Eran las mismas cuatro líneas en los cuatro sitios que desalojan. Y el de
        /// <see cref="Clear"/> se había quedado sin el aviso, así que una caché cuyo
        /// <c>onEvicted</c> libera recursos —la de certificados del MITM lo hace— los
        /// habría perdido al vaciarla. Hoy nadie la vacía, así que no llega a pasar; con
        /// una sola pieza tampoco puede empezar a pasar.
        /// </para>
        /// </summary>
        private bool Desalojar(KeyValuePair<TKey, Node> kv)
        {
            if (!_map.TryRemove(new KeyValuePair<TKey, Node>(kv.Key, kv.Value))) return false;
            Interlocked.Decrement(ref _count);
            _onEvicted?.Invoke(kv.Key, kv.Value.Value);
            return true;
        }

        /// <summary>Enumeración sin locks (el enumerador de ConcurrentDictionary no los toma).</summary>
        public IEnumerable<KeyValuePair<TKey, TValue>> Snapshot()
        {
            foreach (var kv in _map)
                yield return new KeyValuePair<TKey, TValue>(kv.Key, kv.Value.Value);
        }

        /// <summary>Desaloja las entradas caducadas. Devuelve cuántas quitó.</summary>
        public int PurgeExpired()
        {
            if (_isExpired is null) return 0;
            int removed = 0;
            foreach (var kv in _map)
            {
                if (!_isExpired(kv.Value.Value)) continue;
                if (Desalojar(kv)) removed++;
            }
            if (removed > 0) Interlocked.Add(ref _evictions, removed);
            return removed;
        }

        /// <summary>
        /// Hace sitio: primero caducadas, y si no basta, el 10% menos usado recientemente.
        /// Un solo hilo barre a la vez; los demás siguen adelante sin bloquearse.
        /// </summary>
        private void Evict()
        {
            if (Interlocked.CompareExchange(ref _evicting, 1, 0) != 0) return;
            try
            {
                // Se barre HASTA bajar del tope, no una sola vez.
                //
                // Un solo hilo barre a la vez y los demás siguen adelante sin bloquearse,
                // que es lo que se quiere; pero eso significa que con varios escritores
                // solo uno hace sitio mientras el resto sigue insertando. Si esa única
                // pasada quita el 10% y las inserciones continúan, la caché crece por
                // encima del tope sin que nadie vuelva a intentarlo. Medido con el banco
                // de pruebas: 32 hilos, tope 100, 11 210 entradas — o sea que el tope no
                // existía en cuanto había concurrencia, que es justo cuando importa,
                // porque de esta caché cuelgan el DNS, los certificados por hostname y el
                // mapa sticky de remap, y todos los alimenta quien conecta.
                //
                // El límite de pasadas es una red por si las inserciones van más rápido
                // que el barrido: mejor quedarse algo por encima del tope que dejar a un
                // hilo dando vueltas aquí para siempre.
                for (int pasada = 0; pasada < 64; pasada++)
                {
                    if (Volatile.Read(ref _count) <= _capacity) return;
                    if (EvictOnce() == 0) return;
                }
            }
            finally
            {
                Volatile.Write(ref _evicting, 0);
            }
        }

        /// <summary>Una pasada de desalojo. Devuelve cuántas entradas quitó.</summary>
        private int EvictOnce()
        {
            {
                if (PurgeExpired() > 0 && Volatile.Read(ref _count) <= _capacity) return 1;

                // Desalojar en lote (10% del tope) para amortizar el barrido: así solo
                // se recorre una vez cada capacity/10 inserciones.
                //
                // En vez de ordenar la tabla entera —O(n log n) con n = capacidad— se
                // hace una sola pasada calculando el umbral de secuencia por debajo
                // del cual cae aproximadamente ese 10%, y una segunda pasada quitando
                // lo que esté por debajo. Es O(n) y sin asignar la lista completa.
                int target = Math.Max(1, _capacity / 10);

                long minSeq = long.MaxValue, maxSeq = long.MinValue;
                int  total  = 0;
                foreach (var kv in _map)
                {
                    long s = kv.Value.Seq;
                    if (s < minSeq) minSeq = s;
                    if (s > maxSeq) maxSeq = s;
                    total++;
                }
                if (total == 0) return 0;

                // Umbral proporcional: las secuencias son un contador monótono, así que
                // repartirlas linealmente aproxima bien el percentil buscado.
                double fraction  = Math.Min(1.0, (double)target / total);
                long   threshold = minSeq + (long)((maxSeq - minSeq + 1) * fraction);

                int removed = 0;
                foreach (var kv in _map)
                {
                    if (kv.Value.Seq >= threshold) continue;
                    if (Desalojar(kv)) removed++;
                }

                // Red de seguridad: si el reparto no quitó nada (todas las secuencias
                // iguales), desalojar sin más hasta bajar del tope. Sin esto la caché
                // podría crecer indefinidamente en el caso degenerado.
                if (removed == 0)
                {
                    foreach (var kv in _map)
                    {
                        if (Volatile.Read(ref _count) <= _capacity) break;
                        if (Desalojar(kv)) removed++;
                    }
                }

                if (removed > 0) Interlocked.Add(ref _evictions, removed);
                return removed;
            }
        }
    }
}
