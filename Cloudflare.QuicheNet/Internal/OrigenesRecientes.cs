using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// Leer y mandar datagramas sin reservar nada por cada uno: la lectura sobre una <see cref="SocketAddress"/> que se
    /// reescribe, y el <see cref="EndPoint"/> de quien acaba de hablar reutilizado en vez de crear uno por datagrama; y el
    /// envío con la dirección del destino ya serializada.
    /// <para>
    /// <c>ReceiveFromAsync</c> con un EndPoint crea el del origen en cada lectura, y <c>SendTo</c> serializa el del destino
    /// en cada envío: en el banco de UDP sobre QUIC, un IPEndPoint y una SocketAddress con su arreglo por datagrama en cada
    /// sentido, en la escucha QUIC, en el canal de cada conexión de salida y en el UDP de «auto». Con .NET 8, que trae las
    /// sobrecargas sobre SocketAddress; antes, como siempre.
    /// </para>
    /// <para>
    /// <b>Cuántos recuerda.</b> Empieza con <see cref="Pocos"/>, mirados uno a uno, que es lo que habla por el socket de una
    /// conexión. Un socket de muchos pares —el UDP de un puerto, la escucha de un servidor QUIC, el de un relay con sus
    /// clientes del camino directo— pasaba de ahí enseguida, y entonces cada datagrama miraba los dieciséis, no encontraba
    /// el suyo y creaba otro extremo. Cuando echa a demasiados, crece —a 256 y a 4096—, y ya no se miran uno a uno
    /// sino por conjuntos de cuatro elegidos por la dirección, como una caché de n vías.
    /// </para>
    /// </summary>
    internal sealed class OrigenesRecientes
    {
        /// <summary>Los que se recuerdan al empezar, mirados uno a uno: los que hablan por el socket de una conexión.</summary>
        public const int Pocos = 16;

        /// <summary>Lo más que crece, en conjuntos de <see cref="ViasAlCrecer"/>.</summary>
        public const int Muchos = 4096;

        /// <summary>Los de cada conjunto cuando crece.</summary>
        private const int ViasAlCrecer = 4;

        /// <summary>
        /// Cada cuántos datagramas se mira si crecer: si en ellos salió más de uno de cada dieciséis extremos recordados, no
        /// caben. Un socket de pocos pares no echa a nadie, y uno de muchos echa en casi cada datagrama.
        /// </summary>
        private const int Ventana = 512;

        private readonly EndPoint _plantilla;
#if NET8_0_OR_GREATER
        private readonly SocketAddress _leida;
        private SocketAddress?[] _direcciones = new SocketAddress?[Pocos];
        private EndPoint?[] _extremos = new EndPoint?[Pocos];

        /// <summary>El siguiente sitio de cada conjunto: el más antiguo, que es el que sale.</summary>
        private byte[] _siguiente = new byte[1];
        private int _vias = Pocos, _mascara;
        private int _vistos, _desalojos;
#endif

        public OrigenesRecientes(AddressFamily familia)
        {
            _plantilla = new IPEndPoint(familia == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
#if NET8_0_OR_GREATER
            _leida = new SocketAddress(familia);
#endif
        }

        /// <summary>Cuántos extremos puede recordar ahora: para las pruebas.</summary>
#if NET8_0_OR_GREATER
        internal int Capacidad => _direcciones.Length;
#else
        internal int Capacidad => 0;
#endif

        /// <summary>
        /// Lee un datagrama de <paramref name="socket"/>, con quien lo mandó. Un solo lector a la vez: la dirección leída es de
        /// esta pieza.
        /// </summary>
#if NET8_0_OR_GREATER
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<SocketReceiveFromResult> RecibirAsync(Socket socket, Memory<byte> destino, CancellationToken ct)
        {
            int n = await socket.ReceiveFromAsync(destino, SocketFlags.None, _leida, ct).ConfigureAwait(false);
            return new SocketReceiveFromResult { ReceivedBytes = n, RemoteEndPoint = Origen(_leida) };
        }

        /// <summary>El EndPoint de <paramref name="leida"/>: el mismo objeto si ya habló hace poco.</summary>
        internal EndPoint Origen(SocketAddress leida)
        {
            if (++_vistos >= Ventana)
            {
                if (_desalojos * 16 > _vistos && _direcciones.Length < Muchos) Crecer();
                _vistos = _desalojos = 0;
            }

            int conjunto = _mascara == 0 ? 0 : leida.GetHashCode() & _mascara;
            int primero = conjunto * _vias;
            for (int i = primero; i < primero + _vias; i++)
            {
                var d = _direcciones[i];
                if (d is null) break;   // se llenan en orden: detrás no hay nadie
                if (d.Equals(leida)) return _extremos[i]!;
            }

            // Uno nuevo: ocupa el sitio del más antiguo de su conjunto, y si ya estaba ocupado, uno sale.
            int via = _siguiente[conjunto];
            int sitio = primero + via;
            if (_direcciones[sitio] is not null) _desalojos++;
            _siguiente[conjunto] = (byte)((via + 1) % _vias);

            var extremo = _plantilla.Create(leida);
            var copia = _direcciones[sitio];
            if (copia is null || copia.Buffer.Length < leida.Size) _direcciones[sitio] = copia = new SocketAddress(leida.Family, leida.Buffer.Length);
            leida.Buffer.Span[..leida.Size].CopyTo(copia.Buffer.Span);
            copia.Size = leida.Size;
            _extremos[sitio] = extremo;
            return extremo;
        }

        /// <summary>
        /// Dieciséis veces más, en conjuntos de cuatro elegidos por la dirección. Lo recordado se olvida: cada uno vuelve a
        /// entrar con su datagrama siguiente.
        /// </summary>
        private void Crecer()
        {
            int capacidad = Math.Min(Muchos, _direcciones.Length * 16);
            _vias        = ViasAlCrecer;
            _mascara     = capacidad / ViasAlCrecer - 1;
            _direcciones = new SocketAddress?[capacidad];
            _extremos    = new EndPoint?[capacidad];
            _siguiente   = new byte[capacidad / ViasAlCrecer];
        }
#else
        public ValueTask<SocketReceiveFromResult> RecibirAsync(Socket socket, Memory<byte> destino, CancellationToken ct)
            => socket.ReceiveFromAsync(destino, SocketFlags.None, _plantilla, ct);
#endif

#if NET8_0_OR_GREATER
        /// <summary>
        /// La dirección de cada destino ya serializada, por el EndPoint mismo: una conexión manda siempre al suyo, y los
        /// orígenes que reparte esta pieza son objetos estables.
        /// </summary>
        private static readonly ConditionalWeakTable<EndPoint, SocketAddress> Serializadas = new();

        /// <summary>La dirección de <paramref name="destino"/> ya serializada: ver <see cref="Serializadas"/>.</summary>
        public static SocketAddress Serializada(EndPoint destino)
            => Serializadas.TryGetValue(destino, out var d) ? d : Serializadas.GetValue(destino, static e => e.Serialize());
#endif

        /// <summary>
        /// Manda <paramref name="datagrama"/> a <paramref name="destino"/>: con la dirección ya serializada si es de la familia
        /// del socket, que es lo normal; si no —un destino de otra familia—, como siempre.
        /// </summary>
        public static int EnviarA(Socket socket, ReadOnlySpan<byte> datagrama, EndPoint destino)
        {
#if NET8_0_OR_GREATER
            if (destino.AddressFamily == socket.AddressFamily)
                return socket.SendTo(datagrama, SocketFlags.None, Serializada(destino));
#endif
            return socket.SendTo(datagrama, destino);
        }
    }
}
