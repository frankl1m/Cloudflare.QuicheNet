using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cloudflare.QuicheNet
{
    /// <summary>
    /// Por dónde entran y salen los datagramas de una conexión QUIC.
    /// <para>
    /// La conexión no sabe de sockets: pide mandar un datagrama a un destino y espera el
    /// siguiente que llegue, con su origen. Lo corriente es un socket UDP
    /// (<see cref="CanalSocket"/>), pero así cabe también un camino que no lo es —el UDP
    /// de un salto anterior de la cadena, o un socket que cambia de puerto cada poco— sin
    /// que la conexión se entere.
    /// </para>
    /// <para>
    /// El origen que devuelve <see cref="RecibirAsync"/> es el que la conexión compara con su
    /// destino para el rebinding: un canal que cambia de camino a propósito tiene que
    /// devolver siempre el mismo, o la conexión se repuntaría con cada paquete.
    /// </para>
    /// </summary>
    internal interface ICanalDatagramas
    {
        /// <summary>La dirección local, la que quiche ata al camino de la conexión.</summary>
        EndPoint Local { get; }

        /// <summary>
        /// Manda un datagrama sin esperar. Un fallo del camino sale como
        /// <see cref="SocketException"/>: quiche retransmite por PTO lo que no salió.
        /// </summary>
        void Enviar(ReadOnlySpan<byte> datagrama, EndPoint destino);

        /// <summary>
        /// Manda de una vez varios datagramas escritos seguidos en <paramref name="lote"/>: todos miden
        /// <paramref name="segmento"/> bytes salvo el último, que puede ser más corto. Por omisión salen uno a uno;
        /// <see cref="CanalSocket"/> los manda con una sola llamada cuando el sistema sabe partirlos.
        /// </summary>
        void EnviarLote(ReadOnlySpan<byte> lote, int segmento, EndPoint destino)
        {
            while (lote.Length > 0)
            {
                int n = Math.Min(segmento, lote.Length);
                Enviar(lote[..n], destino);
                lote = lote[n..];
            }
        }

        /// <summary>
        /// Lo más largo que cabe ahora por el camino, si lo mide alguien —una sesión turn por debajo, con sus sondas—; null si
        /// no: entonces lo mide quien manda.
        /// </summary>
        int? MaxDatagrama => null;

        /// <summary>
        /// ¿Llega entero o no llega lo que se manda por aquí, sin trocear por el camino? Un socket con DF, o un salto por un
        /// flujo TCP. Es lo que deja medir el MTU con sondas: una sonda troceada pasaría.
        /// </summary>
        bool CaminoSinTrozos => false;

        /// <summary>
        /// A quién decirle que lo grande se reenvía sin llegar, si alguien mide el camino —la sesión turn de un flujo—: lo
        /// verifica con sus sondas y, si el camino se estrechó, su medida baja (ver <c>Kcp.DetectorDeAgujero</c>). null si
        /// nadie lo mide.
        /// </summary>
        Action? SospechaDelCamino => null;

        /// <summary>
        /// Si el par del otro lado rehace los trozos de datagrama de KCP y mKCP (ver <c>Kcp.TrozosDeDatagrama</c>), según
        /// quien mide el camino: lo dice el acuerdo de la sesión turn. mKCP no tiene saludo que lo negocie.
        /// </summary>
        bool ParRehaceTrozos => false;

        /// <summary>
        /// Información del camino UDP hacia <paramref name="remoto"/> para que cada transporte decida su MTU.
        /// Por omisión, no se conoce si el camino trocea.
        /// </summary>
        Cloudflare.QuicheNet.Internal.PistaCaminoUdp PistaHacia(EndPoint remoto) => default;

        /// <summary>
        /// El siguiente datagrama que llegue, y de dónde vino.
        /// <para>
        /// Es el camino caliente de la recepción y no puede asignar por paquete. Por eso
        /// devuelve el resultado del socket tal cual —un <c>async</c> en medio asignaba su
        /// máquina de estados en cada paquete: +600 B por vuelta en SOSTENIDA—, y recibe
        /// <paramref name="esperado"/>: el socket devuelve esa misma instancia cuando el
        /// origen coincide, en vez de una dirección nueva por paquete.
        /// </para>
        /// </summary>
        /// <param name="esperado">El destino actual de la conexión, del que se espera el paquete.</param>
        ValueTask<SocketReceiveFromResult> RecibirAsync(Memory<byte> destino, EndPoint esperado, CancellationToken ct);
    }

    /// <summary>
    /// El canal de siempre: un socket UDP ya atado. No es su dueño —lo cierra quien lo abrió—,
    /// así que el servidor puede darle el de su escucha a todas sus conexiones.
    /// </summary>
    internal sealed class CanalSocket : ICanalDatagramas
    {
        private readonly Socket _socket;

        /// <summary>
        /// Quien lee <see cref="RecibirAsync"/>, que es de un solo lector —la bomba de la conexión de salida—: en Linux, varios
        /// datagramas por llamada (ver <see cref="Cloudflare.QuicheNet.Internal.LectorDeDatagramas"/>). Solo se crea si se lee: la escucha
        /// da su canal a todas sus conexiones, pero lee ella.
        /// </summary>
        private Cloudflare.QuicheNet.Internal.LectorDeDatagramas? _lector;

        /// <summary>
        /// El envío segmentado de un socket. Es del socket y no del canal: la escucha da el suyo a todas sus conexiones,
        /// y el tamaño de segmento que ponga una lo ven los envíos de las demás.
        /// </summary>
        private sealed class EstadoDeEnvio
        {
            /// <summary>El tamaño de segmento puesto en el socket; 0 si no hay.</summary>
            public int Segmento;

            /// <summary>El sistema no parte los envíos de este socket: los lotes salen uno a uno.</summary>
            public volatile bool SinSegmentar;

            /// <summary>
            /// Ya salió partido algún lote. Hasta entonces, cualquier fallo de un envío partido se toma por un sistema que
            /// no sabe partir —en Linux, un núcleo o un dispositivo sin GSO puede fallar al mandar, no al ponerlo—; desde
            /// entonces, solo los que lo dicen: lo demás es del camino.
            /// </summary>
            public bool Probado;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Socket, EstadoDeEnvio> Estados = new();

        /// <summary><c>UDP_SEND_MSG_SIZE</c> de ws2ipdef.h: el tamaño de segmento del envío segmentado (USO) de Windows.</summary>
        private const SocketOptionName UdpSendMsgSize = (SocketOptionName)2;

        /// <summary><c>SOL_UDP</c> y <c>UDP_SEGMENT</c> de Linux: el tamaño de segmento del envío segmentado (GSO), desde el 4.18.</summary>
        private const int SolUdp = 17, UdpSegment = 103;

        /// <summary>
        /// Cuántos segmentos parte Linux en un envío como mucho (<c>UDP_MAX_SEGMENTS</c>, 64 hasta el 5.x): con más, el
        /// envío falla. Windows no lo limita más que por el tamaño total.
        /// </summary>
        private const int MaxSegmentosLinux = 64;

        /// <summary>
        /// Si el sistema sabe partir un envío en datagramas de un tamaño: USO en Windows y GSO en Linux. En el resto
        /// —macOS; Android e iOS, que no se prueban en un dispositivo—, los lotes salen uno a uno.
        /// </summary>
        private static readonly bool SabePartir = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

        /// <summary>
        /// En Windows el tamaño de segmento va en cada envío partido —un mensaje de control <c>UDP_SEND_MSG_SIZE</c> de
        /// <c>WSASendMsg</c>, como hace msquic— y no en el socket: el socket no lleva nunca segmento, y un envío suelto no
        /// tiene que quitarlo ni tomar el cerrojo. Con el segmento en el socket, cada lote de otra medida era un setsockopt, y
        /// uno más para volver a mandar sueltos los datagramas más largos: en CONNECT-IP, donde los datagramas miden lo que
        /// mide cada segmento TCP, ~95 000 por pasada del banco, casi uno por lote. En Linux sigue en el socket
        /// (<c>UDP_SEGMENT</c>): su <c>sendmsg</c> con el mensaje de control no se puede probar aquí. Antes de .NET 8, también:
        /// la dirección ya serializada es de sus sobrecargas.
        /// </summary>
#if NET8_0_OR_GREATER
        internal static readonly bool SegmentoPorEnvio = OperatingSystem.IsWindows();
#else
        internal const bool SegmentoPorEnvio = false;
#endif

        /// <summary>
        /// Lo que cabe en un lote de datagramas que salen con una sola llamada al sistema. Por debajo de los 65 507 bytes de
        /// carga de un datagrama IPv4, que es lo que admite el envío segmentado. Lo usan QUIC y los lotes de KCP y mKCP.
        /// </summary>
        internal const int TopeDeLote = 60_000;

        private readonly EstadoDeEnvio _estado;

        /// <param name="socket">El socket, ya atado.</param>
        /// <param name="ajustarSocket">
        /// En falso cuando otro ya lo ajustó: la escucha lo hace una vez para todas sus
        /// conexiones.
        /// </param>
        public CanalSocket(Socket socket, bool ajustarSocket = true)
        {
            _socket = socket;

            // Sin esto, en Windows un ICMP de «puerto inalcanzable» hace fallar la siguiente
            // recepción del socket: una excepción por cada uno que llegue.
            if (ajustarSocket) Cloudflare.QuicheNet.Internal.SocketCompat.DisableUdpConnReset(socket);

            // USO en Windows y GSO en Linux; en otros sistemas los lotes salen uno a uno.
            _estado = Estados.GetValue(socket, static _ => new EstadoDeEnvio { SinSegmentar = !SabePartir });
        }

        public EndPoint Local => _socket.LocalEndPoint ?? throw new ArgumentException(
            "Given socket was not bound to a valid local endpoint!", "socket");

        /// <summary>
        /// Con DF, lo que el sistema deja mandar hacia <paramref name="remoto"/>; sin él, lo que llegue del par. Un par en el
        /// proceso dice el suyo. El transporte interpreta la pista sin añadir política al canal.
        /// </summary>
        public Cloudflare.QuicheNet.Internal.PistaCaminoUdp PistaHacia(EndPoint remoto)
            => Cloudflare.QuicheNet.Internal.PistaCaminoUdp.DeSocket(_socket, remoto);

        public void Enviar(ReadOnlySpan<byte> datagrama, EndPoint destino)
        {
            // Un par que no está al otro lado del socket sino en el proceso —el cliente de un flujo del relay en un nodo—: se
            // le entrega sin tocar el socket.

            // Sin segmentar nunca hubo segmento puesto, y con el segmento en cada envío el socket no lo lleva: no hace falta
            // el cerrojo.
            if (_estado.SinSegmentar || SegmentoPorEnvio)
            {
                EnviarEntero(datagrama, destino);
                return;
            }

            lock (_estado)
            {
                // Con un segmento puesto, un datagrama más largo que él saldría partido en varios.
                if (_estado.Segmento != 0 && datagrama.Length > _estado.Segmento) PonerSegmento(0);
                EnviarEntero(datagrama, destino);
            }
        }

        public void EnviarLote(ReadOnlySpan<byte> lote, int segmento, EndPoint destino)
        {

#if NET8_0_OR_GREATER
            if (SegmentoPorEnvio)
            {
                if (!_estado.SinSegmentar && lote.Length > segmento && destino.AddressFamily == _socket.AddressFamily)
                {
                    try
                    {
                        try { EnviarPartido(lote, segmento, destino); }
                        catch (SocketException ex) when (SinDfPorqueNoCabe(ex, segmento)) { EnviarPartido(lote, segmento, destino); }
                        _estado.Probado = true;
                        return;
                    }
                    catch (SocketException ex) when (!_estado.Probado
                                                     || ex.SocketErrorCode is SocketError.InvalidArgument
                                                     or SocketError.OperationNotSupported
                                                     or SocketError.ProtocolOption
                                                     or SocketError.ProtocolNotSupported
                                                     or SocketError.MessageSize)
                    {
                        // Este sistema no parte los envíos: desde aquí, uno a uno. El socket no lleva segmento que quitar.
                        _estado.SinSegmentar = true;
                    }
                }

                // Uno a uno: sin partir, o un destino de otra familia, que no va con la dirección ya serializada.
                while (lote.Length > 0)
                {
                    int n = Math.Min(segmento, lote.Length);
                    EnviarEntero(lote[..n], destino);
                    lote = lote[n..];
                }
                return;
            }
#endif
            if (!_estado.SinSegmentar && lote.Length > segmento)
            {
                lock (_estado)
                {
                    if (!_estado.SinSegmentar)
                    {
                        try
                        {
                            if (_estado.Segmento != segmento) PonerSegmento(segmento);

                            // Linux parte como mucho 64 segmentos por envío: un lote de datagramas pequeños —acuses de
                            // KCP, de 24 bytes— sale en tandas. Lo que haya salido ya no se repite si una tanda falla.
                            int porEnvio = OperatingSystem.IsWindows() ? lote.Length : MaxSegmentosLinux * segmento;
                            while (lote.Length > 0)
                            {
                                int n = Math.Min(porEnvio, lote.Length);
                                EnviarEntero(lote[..n], destino);
                                lote = lote[n..];
                            }
                            _estado.Probado = true;
                            return;
                        }
                        catch (SocketException ex) when (!_estado.Probado
                                                         || ex.SocketErrorCode is SocketError.InvalidArgument
                                                         or SocketError.OperationNotSupported
                                                         or SocketError.ProtocolOption
                                                         or SocketError.ProtocolNotSupported
                                                         or SocketError.MessageSize)
                        {
                            // Este sistema, o este socket, no parte los envíos: desde aquí, uno a uno. La marca va DESPUÉS
                            // de quitar el segmento, y solo si se pudo: Enviar la lee sin cerrojo, y con el segmento aún
                            // puesto un datagrama más largo saldría partido.
                            QuitarSegmentoYNoSegmentar(_estado, _socket);
                        }
                    }
                }
            }

            while (lote.Length > 0)
            {
                int n = Math.Min(segmento, lote.Length);
                Enviar(lote[..n], destino);
                lote = lote[n..];
            }
        }

        /// <summary>
        /// Quita el segmento y, si queda sin él, deja de segmentar. Si no se pudo quitar, la marca no se pone: así Enviar
        /// sigue entrando por el cerrojo y lo intenta quitar antes de un datagrama largo.
        /// </summary>
        private static void QuitarSegmentoYNoSegmentar(EstadoDeEnvio estado, Socket socket)
        {
            lock (estado)
            {
                if (estado.Segmento != 0)
                {
                    try
                    {
                        FijarSegmento(socket, 0);
                        estado.Segmento = 0;
                    }
                    catch (SocketException) { return; }
                    catch (ObjectDisposedException) { return; }
                }
                estado.SinSegmentar = true;
            }
        }

        /// <summary>Pone el tamaño de segmento en el socket. Con el cerrojo del estado tomado.</summary>
        private void PonerSegmento(int segmento)
        {
            FijarSegmento(_socket, segmento);
            _estado.Segmento = segmento;
        }

        /// <summary>El tamaño de segmento del socket, con la opción de cada sistema; 0 lo quita.</summary>
        private static void FijarSegmento(Socket socket, int segmento)
        {
            if (OperatingSystem.IsWindows())
            {
                socket.SetSocketOption(SocketOptionLevel.Udp, UdpSendMsgSize, segmento);
                return;
            }

            // UDP_SEGMENT no tiene nombre en SocketOptionName: va a pelo, con el entero en el orden de la máquina.
            Span<byte> valor = stackalloc byte[sizeof(int)];
            BitConverter.TryWriteBytes(valor, segmento);
            socket.SetRawSocketOption(SolUdp, UdpSegment, valor);
        }

#if NET8_0_OR_GREATER
        /// <summary>El socket, para <see cref="EnviarPartido"/>: leer <see cref="Socket.SafeHandle"/> cada vez lo marca de nuevo.</summary>
        private SafeSocketHandle? _manija;

        /// <summary><c>IPPROTO_UDP</c>: el nivel del mensaje de control de <see cref="EnviarPartido"/>.</summary>
        private const int IpprotoUdp = 17;

        /// <summary>
        /// Manda <paramref name="lote"/> partido en datagramas de <paramref name="segmento"/> bytes con una sola llamada:
        /// <c>WSASendMsg</c> con el tamaño de segmento en un mensaje de control (<c>UDP_SEND_MSG_SIZE</c>), sin tocar el socket.
        /// Solo Windows: ver <see cref="SegmentoPorEnvio"/>.
        /// </summary>
        private unsafe void EnviarPartido(ReadOnlySpan<byte> lote, int segmento, EndPoint destino)
        {
            SimularRechazo(segmento);
            SocketAddress direccion = Cloudflare.QuicheNet.Internal.OrigenesRecientes.Serializada(destino);

            // WSACMSGHDR —su largo, del tamaño de un puntero, el nivel y el tipo— y detrás el segmento, alineado al tamaño de
            // un puntero (WSA_CMSG_DATA). El espacio, WSA_CMSG_SPACE: los datos también alineados.
            int datos   = (IntPtr.Size + 2 * sizeof(int) + IntPtr.Size - 1) & ~(IntPtr.Size - 1);
            int espacio = datos + ((sizeof(uint) + IntPtr.Size - 1) & ~(IntPtr.Size - 1));
            byte* control = stackalloc byte[32];
            new Span<byte>(control, 32).Clear();
            *(nint*)control = datos + sizeof(uint);
            *(int*)(control + IntPtr.Size) = IpprotoUdp;
            *(int*)(control + IntPtr.Size + sizeof(int)) = (int)UdpSendMsgSize;
            *(uint*)(control + datos) = (uint)segmento;

            SafeSocketHandle manija = _manija ??= _socket.SafeHandle;
            bool tomada = false;
            try
            {
                manija.DangerousAddRef(ref tomada);
                fixed (byte* pLote = lote)
                fixed (byte* pDireccion = direccion.Buffer.Span)
                {
                    WsaBuf datosDelLote = new() { Len = (uint)lote.Length, Buf = pLote };
                    WsaMsg mensaje = new()
                    {
                        Name        = pDireccion,
                        NameLen     = direccion.Size,
                        Buffers     = &datosDelLote,
                        BufferCount = 1,
                        Control     = new WsaBuf { Len = (uint)espacio, Buf = control },
                    };
                    uint enviados;
                    if (WSASendMsg(manija.DangerousGetHandle(), &mensaje, 0, &enviados, IntPtr.Zero, IntPtr.Zero) != 0)
                        throw new SocketException(Marshal.GetLastSystemError());
                }
            }
            finally
            {
                if (tomada) manija.DangerousRelease();
            }
        }

        // WSABUF y WSAMSG de ws2def.h, con los punteros de la máquina: en x86 y en 64 bits cuadran con los de C.
        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct WsaBuf
        {
            public uint Len;
            public byte* Buf;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct WsaMsg
        {
            public byte* Name;
            public int NameLen;
            public WsaBuf* Buffers;
            public uint BufferCount;
            public WsaBuf Control;
            public uint Flags;
        }

        // El ensamblado desactiva el marshalling del runtime: tipos blittables y punteros, y el error se lee después.
        [DllImport("ws2_32.dll")]
        private static extern unsafe int WSASendMsg(IntPtr socket, WsaMsg* mensaje, uint flags, uint* enviados,
                                                    IntPtr solapado, IntPtr rutina);
#endif

        private void EnviarEntero(ReadOnlySpan<byte> datagrama, EndPoint destino)
        {
            try { EnviarEnteroUnaVez(datagrama, destino); }
            catch (SocketException ex) when (SinDfPorqueNoCabe(ex, LargoDeCadaDatagrama(datagrama.Length)))
            {
                EnviarEnteroUnaVez(datagrama, destino);
            }
        }

        /// <summary>
        /// Lo que todo camino tiene que llevar con DF: el Initial de QUIC (1200) y el suelo de la búsqueda del MTU de KCP, mKCP
        /// y turn (<see cref="1232"/>, 1232). Por debajo no se busca nada: es lo que se manda
        /// sin saber el camino.
        /// </summary>
        internal static readonly int DebeCaber = Math.Max(Cloudflare.QuicheNet.Internal.LimitesDeDatagrama.MinimoInicialQuic, 1232);

        /// <summary>
        /// El sistema rechazó por tamaño, con DF, un datagrama que todo camino tiene que llevar (<see cref="DebeCaber"/>): la
        /// ruta es más estrecha que eso —el WARP de Cloudflare deja 1193 de carga— o lo es desde hace un momento. Se le quita
        /// el DF al socket y se repite: lo trocea el equipo. Con él no salía ni el saludo; lo mayor sigue siendo cosa de quien
        /// busca el MTU, que sin DF no busca por encima del suelo.
        /// </summary>
        private bool SinDfPorqueNoCabe(SocketException ex, int largo)
            => ex.SocketErrorCode == SocketError.MessageSize && largo <= DebeCaber
               && Cloudflare.QuicheNet.Internal.SocketUdpDf.Fragmentar(_socket);

        private void EnviarEnteroUnaVez(ReadOnlySpan<byte> datagrama, EndPoint destino)
        {
            SimularRechazo(LargoDeCadaDatagrama(datagrama.Length));

            // Un datagrama sale entero o no sale; el bucle es por si el sistema dijera otra cosa.
            int enviado = 0;
            while (enviado < datagrama.Length)
            {
                int n = Cloudflare.QuicheNet.Internal.OrigenesRecientes.EnviarA(_socket, datagrama[enviado..], destino);
                if (n <= 0) break;
                enviado += n;
            }
        }

        /// <summary>
        /// Lo que mide cada datagrama de un envío: el envío entero, o su segmento si el socket lleva uno puesto (GSO de Linux).
        /// </summary>
        private int LargoDeCadaDatagrama(int envio) => _estado.Segmento != 0 ? Math.Min(_estado.Segmento, envio) : envio;

        /// <summary>Ver <see cref="Cloudflare.QuicheNet.Internal.SocketUdpDf.RechazoSimulado"/>: solo en las pruebas.</summary>
        private void SimularRechazo(int largo) => Cloudflare.QuicheNet.Internal.SocketUdpDf.SimularRechazo(_socket, largo);

        public ValueTask<SocketReceiveFromResult> RecibirAsync(Memory<byte> destino, EndPoint esperado, CancellationToken ct)
            => (_lector ??= new Cloudflare.QuicheNet.Internal.LectorDeDatagramas(_socket, Cloudflare.QuicheNet.Internal.LimitesDeDatagrama.MaxDatagramaUdp)).RecibirAsync(destino, ct);
    }
}
