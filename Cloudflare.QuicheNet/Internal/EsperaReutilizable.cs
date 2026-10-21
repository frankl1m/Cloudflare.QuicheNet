using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// Una espera de un solo consumidor que se arma una vez por vuelta y no asigna: la fuente de un
    /// <see cref="ValueTask{TResult}"/> que se reutiliza. La completa quien despierta o la cancela el token, lo que llegue
    /// antes: el otro no hace nada.
    /// <para>
    /// Es lo que no da un <c>Channel</c> con un token cancelable —ahí cada espera es un objeto nuevo— y lo que necesitan
    /// las colas que esperan en el camino caliente: la entrada de cada stream h2, a la que un WebSocket pregunta con una
    /// lectura de 0 bytes por mensaje, y las colas de los lectores de un canal.
    /// </para>
    /// <para>
    /// <b>Quien espera sigue en la cola global del pool</b>, en un encargo propio, no en la continuación asíncrona de
    /// <see cref="ManualResetValueTaskSourceCore{TResult}"/>: esa la encola en la cola <i>local</i> del hilo que completa, y
    /// quien completa es casi siempre un bucle de lectura —el de una conexión h2—, que sigue con el hilo hasta su
    /// próxima espera. El lector quedaba detrás, en serie con quien le da los datos, y el túnel h2 perdía un tercio de
    /// su caudal. Por la cola global lo recoge otro hilo, en paralelo, como hacía el canal al que sustituye.
    /// </para>
    /// <para>
    /// Quien la usa guarda, bajo su propio candado, si hay alguien esperando: arma con el candado tomado y completa ya
    /// sin él. Un despertar que llegue tarde a una vuelta ya cancelada puede completar la siguiente: es un despertar de
    /// más, y quien espera vuelve a mirar. Una cancelación tardía, en cambio, no: al recoger cada vuelta se espera a que
    /// acabe la suya si estaba corriendo, así que nunca alcanza a la siguiente con el token de otra.
    /// </para>
    /// </summary>
    internal sealed class EsperaReutilizable : IValueTaskSource<bool>, IThreadPoolWorkItem
    {
        private ManualResetValueTaskSourceCore<bool> _core;   // sin continuaciones asíncronas: corren en Execute
        private CancellationTokenRegistration _registro;
        private int _armada;   // 1 mientras nadie la completó
        private bool _valor;
        private Exception? _error;

        public ValueTask<bool> Armar(CancellationToken ct)
        {
            _core.Reset();
            Volatile.Write(ref _armada, 1);
            if (ct.CanBeCanceled)
                _registro = ct.UnsafeRegister(static (s, t) => ((EsperaReutilizable)s!).Cancelar(t), this);
            return new ValueTask<bool>(this, _core.Version);
        }

        public void Completar(bool valor)
        {
            if (Interlocked.Exchange(ref _armada, 0) != 1) return;
            _valor = valor;
            _error = null;
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        public void Fallar(Exception error)
        {
            if (Interlocked.Exchange(ref _armada, 0) != 1) return;
            _error = error;
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        private void Cancelar(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _armada, 0) != 1) return;
            _error = new OperationCanceledException(ct);
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        /// <summary>Completa la vuelta en un hilo del pool: la continuación de quien espera corre aquí.</summary>
        void IThreadPoolWorkItem.Execute()
        {
            if (_error is { } error) _core.SetException(error);
            else _core.SetResult(_valor);
        }

        public bool GetResult(short token)
        {
            try { return _core.GetResult(token); }
            finally
            {
                // Dispose y no Unregister: si la cancelación de esta vuelta está corriendo —perdió contra Completar—, se
                // espera a que acabe. Son microsegundos, y sin esperarla podía poner _armada a cero después de que quien
                // espera armara la vuelta siguiente, y completarla con la cancelación de un token que ya no es el suyo.
                _registro.Dispose();
                _registro = default;
            }
        }

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token,
                                ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}
