using System;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// Quien lee un socket de datagramas: de uno en uno para quien llama y, en Linux, de muchos en muchos al sistema.
    /// <para>
    /// <b>Por qué.</b> Cada <c>ReceiveFromAsync</c> es una llamada al sistema y una vuelta por la maquinaria asíncrona de los
    /// sockets de .NET, por cada datagrama. En los sockets que más mueven —el de cada conexión QUIC de salida, el de la escucha
    /// QUIC, el de un puerto KCP o mKCP y el UDP de «auto»— eso es una llamada por cada 1452 bytes, y del otro lado el envío ya
    /// sale en lotes partidos por el sistema (GSO, ver <c>CanalSocket.EnviarLote</c>): el lote se manda con una llamada y se
    /// leía con cuarenta.
    /// </para>
    /// <para>
    /// <b>Cómo, en Linux</b> (y Android, que es su núcleo):
    /// </para>
    /// <list type="bullet">
    /// <item><b>UDP_GRO</b>: el sistema entrega juntos los datagramas seguidos de un mismo origen y del mismo tamaño, en un
    /// búfer, con el tamaño de cada trozo en un mensaje de control; aquí se parten. Por el bucle local, el lote que salió con
    /// GSO llega tal cual, sin que el núcleo lo trocee al recibir; por una tarjeta, lo junta el GRO del núcleo.</item>
    /// <item><b>recvmmsg</b>: varios mensajes por llamada —cada uno, un datagrama o un lote de GRO—, de orígenes
    /// distintos.</item>
    /// </list>
    /// <para>
    /// <b>Medido</b> en el banco, Debian en 4 núcleos, rondas alternas frente a lo de antes: con GRO, la CPU por MB baja un
    /// 18-22 % en todos los casos de QUIC (con la tanda de la bomba de recepción, ver <c>QuicheConnection.TandaDeRecepcion</c>),
    /// un 25 % en KCP y un 23 % en mKCP, y el caudal sube hasta un 30 %. recvmmsg sin GRO se queda en un 0-5 % en QUIC, un
    /// 2 % en KCP y un 11 % en mKCP: el núcleo sigue troceando cada lote al recibirlo, y lo que se ahorra son solo las
    /// llamadas. Por eso, sin GRO, de uno en uno.
    /// </para>
    /// <para>
    /// La llamada no espera nunca (<c>MSG_DONTWAIT</c>). Cuando no queda nada, se espera a que haya con una lectura de cero
    /// bytes del propio socket de .NET, que en Unix mira sin sacar (<c>MSG_PEEK</c>): así la espera sigue siendo la de .NET,
    /// con su epoll, su cancelación y su cierre, y lo que llega lo saca la llamada siguiente.
    /// </para>
    /// <para>
    /// <b>Para quien llama no cambia nada</b>: <see cref="RecibirAsync"/> devuelve un datagrama, con su origen, como
    /// <see cref="OrigenesRecientes.RecibirAsync"/>; lo que queda del lote se entrega en las llamadas siguientes sin tocar el
    /// sistema, ya completado. El bucle que mira si la lectura acabó en el acto —el fin de una ráfaga en KCP— ve lo mismo
    /// que antes: acaba la ráfaga cuando ya no queda nada en el socket.
    /// </para>
    /// <para>
    /// <b>Plan B.</b> En Windows, macOS, iOS, con .NET 7, o si la función no está o el sistema no la deja —una vez, y se
    /// recuerda para todo el proceso—, el camino de siempre. GRO se pide por socket, y si el núcleo no lo conoce —anterior al
    /// 5.0—, ese socket también va de uno en uno. Un socket con GRO no puede leerlo nadie más con un búfer de un datagrama —le
    /// llegaría un lote cortado—: por eso lo pone el lector, que es el único que lee su socket, y solo tras ver que la
    /// llamada funciona.
    /// </para>
    /// </summary>
    internal sealed class LectorDeDatagramas
    {
        /// <summary>Cómo se lee. Lo elige el lector; las pruebas y el banco lo fuerzan con <see cref="Forzada"/>.</summary>
        internal enum Forma
        {
            /// <summary>Una llamada por datagrama, con el <c>ReceiveFromAsync</c> de .NET: lo de siempre.</summary>
            DeUnoEnUno,

            /// <summary>recvmmsg, sin GRO: varios datagramas por llamada, cada uno en su hueco. Solo forzada: no baja la CPU lo bastante.</summary>
            Lotes,

            /// <summary>recvmmsg con GRO: varios mensajes por llamada, y cada uno puede traer un lote de datagramas.</summary>
            Gro,
        }

        /// <summary>La forma para los lectores que se creen desde ahora; nula, la de por omisión. Para las pruebas y el banco.</summary>
        internal static Forma? Forzada { get; set; }

        /// <summary>Mensajes por llamada sin GRO: cada uno es un datagrama, en un hueco del tamaño del mayor que se admite.</summary>
        internal static int MensajesSinGro { get; set; } = 32;

        /// <summary>Mensajes por llamada con GRO: cada uno puede ser un lote entero, en un hueco de 64 KiB.</summary>
        internal static int MensajesConGro { get; set; } = 4;

        /// <summary>Lo más que trae un mensaje de GRO: lo que cabe en un datagrama IP, cabeceras incluidas.</summary>
        internal const int HuecoDeGro = 64 * 1024;

        private readonly Socket _socket;
        private readonly int _maxDatagrama;
        private readonly OrigenesRecientes _recientes;

        /// <summary>La forma con que lee; puede bajar a <see cref="Forma.DeUnoEnUno"/> en la primera lectura.</summary>
        private Forma _forma;

        /// <param name="socket">El socket, ya atado. Este lector es el único que lo lee.</param>
        /// <param name="maxDatagrama">El datagrama más largo que se admite: lo que sea mayor se tira, como al leerlo de uno en uno.</param>
        /// <param name="forma">La forma, para las pruebas; donde no se puede leer por lotes, de uno en uno igualmente.</param>
        public LectorDeDatagramas(Socket socket, int maxDatagrama, Forma? forma = null)
        {
            _socket       = socket;
            _maxDatagrama = maxDatagrama;
            _recientes    = new OrigenesRecientes(socket.AddressFamily);
            _forma        = PorLotesDisponible ? forma ?? Forzada ?? Forma.Gro : Forma.DeUnoEnUno;
#if NET8_0_OR_GREATER
            _leida = new SocketAddress(socket.AddressFamily);
#endif
        }

        /// <summary>La forma con que lee ahora: para las pruebas.</summary>
        internal Forma FormaActual => _forma;

        /// <summary>Lo que ocupa el lote prestado ahora, en bytes; 0 sin lote. Para las pruebas.</summary>
#if NET8_0_OR_GREATER
        internal int BytesDelLote => _lote?.Length ?? 0;
#else
        internal int BytesDelLote => 0;
#endif

        /// <summary>Las llamadas al sistema que trajeron algo, y los datagramas que entregaron: para las pruebas.</summary>
        internal long LlamadasConDatos { get; private set; }

        /// <inheritdoc cref="LlamadasConDatos"/>
        internal long Entregados { get; private set; }

        /// <summary>Los mensajes que llegaron juntados por GRO —con más de un datagrama—: para las pruebas.</summary>
        internal long LotesDeGro { get; private set; }

        /// <summary>Lo mismo, de todos los lectores del proceso: llamadas con datos, datagramas en ellas y lotes de GRO. Para medir.</summary>
        internal static long LlamadasDelProceso, DatagramasDelProceso, LotesDeGroDelProceso;

        /// <summary>
        /// Si este proceso puede leer por lotes: Linux o Android con .NET 8 o más, y la llamada sin fallar todavía. Lo que se
        /// sabe sin haber leído aún; la primera lectura puede decir que no.
        /// </summary>
        internal static bool PorLotesDisponible
        {
            get
            {
#if NET8_0_OR_GREATER
                return PlataformaConLotes && !_sinLlamada;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Si en esta plataforma se lee por lotes: Linux o Android, con .NET 8 o más. Lo que se ajusta a ello —la tanda de la
        /// bomba de recepción de QUIC— lo mira aquí, sin depender de que la llamada ya se haya probado.
        /// </summary>
#if NET8_0_OR_GREATER
        internal static readonly bool PlataformaConLotes = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();
#else
        internal const bool PlataformaConLotes = false;
#endif

        /// <summary>Para las pruebas: como si la llamada no estuviera —o volviera a estar— en este proceso.</summary>
        internal static void SimularSinLlamada(bool sin)
        {
#if NET8_0_OR_GREATER
            _sinLlamada = sin;
#endif
        }

        /// <summary>Para las pruebas: como si el núcleo no conociera UDP_GRO —o volviera a conocerlo—.</summary>
        internal static void SimularSinGro(bool sin)
        {
#if NET8_0_OR_GREATER
            _sinGro = sin;
#endif
        }

        /// <summary>
        /// El siguiente datagrama, escrito en <paramref name="destino"/>, y quién lo mandó. Un solo lector a la vez. Lanza lo
        /// mismo que <c>ReceiveFromAsync</c>: <see cref="OperationCanceledException"/>, <see cref="ObjectDisposedException"/>
        /// y <see cref="SocketException"/>.
        /// </summary>
        public ValueTask<SocketReceiveFromResult> RecibirAsync(Memory<byte> destino, CancellationToken ct)
        {
#if NET8_0_OR_GREATER
            // Lo que quede del lote, con la forma que sea: la primera llamada puede traer datagramas y dejar el lector de uno
            // en uno, si el núcleo no tiene GRO.
            if (_actual < _cuantos && Tomar(destino.Span, out var r)) return new ValueTask<SocketReceiveFromResult>(r);
            if (_forma != Forma.DeUnoEnUno) return RecibirDelSistemaAsync(destino, ct);
            if (_lote is not null) Soltar();
#endif
            return _recientes.RecibirAsync(_socket, destino, ct);
        }

#if NET8_0_OR_GREATER
        // ── El lote ──────────────────────────────────────────────────────────

        /// <summary>
        /// Los datos del lote: un hueco por mensaje, de <see cref="_hueco"/> bytes. Prestado mientras hay algo que leer, y de
        /// vuelta al almacén antes de esperar: un socket callado no se queda con él.
        /// </summary>
        private byte[]? _lote;

        private int _hueco;

        /// <summary>Por cada mensaje del lote: lo que mide (−1 si se tira), el tamaño de sus trozos con GRO (0 si es uno) y su origen.</summary>
        private int[] _largos = Array.Empty<int>();
        private int[] _segmentos = Array.Empty<int>();
        private EndPoint?[] _origenes = Array.Empty<EndPoint?>();

        /// <summary>Cuántos mensajes trajo la última llamada, por cuál va la entrega y por dónde dentro de él.</summary>
        private int _cuantos, _actual, _dentro;

        /// <summary>
        /// La última llamada no llenó el lote: el socket quedó vacío, y lo siguiente es esperar —que mira una vez antes de
        /// dormir—, sin otra llamada que diría lo mismo.
        /// </summary>
        private bool _vaciado;

        /// <summary>La dirección del último mensaje, para <see cref="OrigenesRecientes.Origen"/>, que la convierte sin reservar.</summary>
        private readonly SocketAddress _leida;

        /// <summary>El socket, para las llamadas: leer <see cref="Socket.SafeHandle"/> cada vez lo marca de nuevo.</summary>
        private SafeSocketHandle? _manija;

        /// <summary>Ya se miró que la llamada funciona en este socket, y se pidió GRO si tocaba.</summary>
        private bool _probado;

        /// <summary>La llamada no está, o el sistema no la deja: desde la primera vez que se vio, el camino de siempre para todos.</summary>
        private static volatile bool _sinLlamada;

        /// <summary>El núcleo no conoce UDP_GRO: no se vuelve a pedir en otros sockets.</summary>
        private static volatile bool _sinGro;

        /// <summary>El siguiente datagrama del lote ya leído, si queda alguno. Lo que no cabe en <paramref name="destino"/> se tira.</summary>
        private bool Tomar(Span<byte> destino, out SocketReceiveFromResult r)
        {
            while (_actual < _cuantos)
            {
                int i     = _actual;
                int largo = _largos[i];
                if (largo < 0)
                {
                    _actual++;
                    continue;
                }

                int segmento = _segmentos[i];
                int desde    = _dentro;
                int n        = segmento > 0 ? Math.Min(segmento, largo - desde) : largo;
                if (segmento > 0 && desde + n < largo) _dentro = desde + n;
                else { _actual++; _dentro = 0; }

                if (n > destino.Length) continue;
                _lote.AsSpan(i * _hueco + desde, n).CopyTo(destino);
                Entregados++;
                r = new SocketReceiveFromResult { ReceivedBytes = n, RemoteEndPoint = _origenes[i]! };
                return true;
            }

            r = default;
            return false;
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<SocketReceiveFromResult> RecibirDelSistemaAsync(Memory<byte> destino, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (!_vaciado)
                {
                    int n = Leer();
                    if (n > 0 && Tomar(destino.Span, out var r)) return r;
                    if (_forma == Forma.DeUnoEnUno)
                    {
                        Soltar();
                        return await _recientes.RecibirAsync(_socket, destino, ct).ConfigureAwait(false);
                    }
                    if (n > 0) continue;   // todo lo que trajo se tiró: otra vez
                }

                // Nada en el socket: el lote vuelve al almacén, y se espera a que llegue algo sin sacarlo. La lectura de cero
                // bytes mira una vez antes de dormir, así que lo que haya llegado entretanto no espera.
                _vaciado = false;
                Soltar();
                await _socket.ReceiveAsync(Memory<byte>.Empty, SocketFlags.Peek, ct).ConfigureAwait(false);
            }
        }

        private void Soltar()
        {
            if (_lote is { } lote)
            {
                _lote = null;
                ArrayPool<byte>.Shared.Return(lote);
            }
        }

        /// <summary>
        /// Una llamada al sistema, sin esperar: los mensajes que haya, hasta llenar el lote. Devuelve cuántos —0 si no había
        /// nada—; si la llamada no sirve aquí, deja el lector en <see cref="Forma.DeUnoEnUno"/> y devuelve 0.
        /// </summary>
        private int Leer()
        {
            if (!_probado)
            {
                // La primera, sin GRO: si la llamada falla por no estar, nadie habrá recibido un lote juntado.
                int probada = LlamarYRepartir(conGro: false, MensajesSinGro);
                if (_forma == Forma.DeUnoEnUno) return 0;
                _probado = true;

                // Sin GRO en el núcleo, de uno en uno: recvmmsg solo no baja lo bastante la CPU por MB (ver la clase). Lo que
                // trajo esta llamada se entrega igual.
                if (_forma == Forma.Gro && !PedirGro()) _forma = Forma.DeUnoEnUno;
                return probada;
            }

            return _forma == Forma.Gro ? LlamarYRepartir(conGro: true, MensajesConGro)
                                       : LlamarYRepartir(conGro: false, MensajesSinGro);
        }

        /// <summary>UDP_GRO en el socket. Falso si el núcleo no lo conoce —anterior al 5.0— o el socket no lo admite.</summary>
        private bool PedirGro()
        {
            if (_sinGro) return false;
            Span<byte> uno = stackalloc byte[sizeof(int)];
            BitConverter.TryWriteBytes(uno, 1);
            try
            {
                _socket.SetRawSocketOption(Linux.SolUdp, Linux.UdpGro, uno);
                return true;
            }
            catch (SocketException) { _sinGro = true; return false; }
            catch (PlatformNotSupportedException) { _sinGro = true; return false; }
        }

        /// <summary>
        /// Llama al sistema y deja el lote listo para <see cref="Tomar"/>. Ver <see cref="Leer"/>.
        /// <para>
        /// El lote no pasa de lo que ocupa el de GRO —<see cref="MensajesConGro"/> huecos de <see cref="HuecoDeGro"/>—: sin GRO,
        /// cada hueco mide el datagrama más largo que se admite, y con los de 64 KB de QUIC, los 32 mensajes de la primera lectura
        /// de cada socket —la que prueba la llamada— eran 2 MB prestados hasta que el socket se vaciaba.
        /// </para>
        /// </summary>
        private unsafe int LlamarYRepartir(bool conGro, int mensajes)
        {
            int hueco = conGro ? HuecoDeGro : (_maxDatagrama + 63) & ~63;
            mensajes = Math.Clamp(Math.Min(mensajes, Math.Max(1, MensajesConGro) * HuecoDeGro / hueco), 1, Linux.MaxMensajes);
            _hueco = hueco;
            byte[] lote = _lote ??= ArrayPool<byte>.Shared.Rent(mensajes * hueco);
            if (lote.Length < mensajes * hueco)
            {
                // Prestado para otra forma más pequeña: se cambia.
                ArrayPool<byte>.Shared.Return(lote);
                _lote = lote = ArrayPool<byte>.Shared.Rent(mensajes * hueco);
            }
            if (_largos.Length < mensajes)
            {
                _largos    = new int[mensajes];
                _segmentos = new int[mensajes];
                _origenes  = new EndPoint?[mensajes];
            }

            int control = conGro ? Linux.EspacioDeControl : 0;
            Linux.MMsgHdr* cabeceras = stackalloc Linux.MMsgHdr[mensajes];
            Linux.IoVec* trozos      = stackalloc Linux.IoVec[mensajes];
            byte* nombres            = stackalloc byte[mensajes * Linux.EspacioDeNombre];
            byte* controles          = stackalloc byte[Math.Max(1, mensajes * control)];

            int recibidos, error = 0;
            fixed (byte* datos = lote)
            {
                for (int i = 0; i < mensajes; i++)
                {
                    trozos[i] = new Linux.IoVec { Base = datos + i * hueco, Len = (nuint)hueco };
                    cabeceras[i] = new Linux.MMsgHdr
                    {
                        Hdr = new Linux.MsgHdr
                        {
                            Name       = nombres + i * Linux.EspacioDeNombre,
                            NameLen    = Linux.EspacioDeNombre,
                            Iov        = trozos + i,
                            IovLen     = 1,
                            Control    = control > 0 ? controles + i * control : null,
                            ControlLen = (nuint)control,
                        },
                    };
                }

                SafeSocketHandle manija = _manija ??= _socket.SafeHandle;
                bool tomada = false;
                try
                {
                    manija.DangerousAddRef(ref tomada);
                    int fd = (int)manija.DangerousGetHandle();
                    do
                    {
                        recibidos = Linux.Recvmmsg(fd, cabeceras, (uint)mensajes, Linux.MsgDontWait, null);
                        error = recibidos < 0 ? Marshal.GetLastSystemError() : 0;
                    }
                    while (recibidos < 0 && error == Linux.EIntr);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    SinLlamada();
                    return 0;
                }
                finally
                {
                    if (tomada) manija.DangerousRelease();
                }
            }

            if (recibidos < 0)
            {
                if (error is Linux.EAgain) { _vaciado = false; return 0; }

                // Sin la llamada —un núcleo o un filtro seccomp que no la dejan—, la primera vez: el camino de siempre.
                if (!_probado && error is Linux.ENoSys or Linux.EPerm or Linux.EOpNotSupp or Linux.EInval)
                {
                    SinLlamada();
                    return 0;
                }
                if (error is Linux.EBadF) throw new ObjectDisposedException(nameof(Socket));
                throw new SocketException((int)Linux.ErrorDeSocket(error));
            }

            _cuantos = recibidos;
            _actual  = 0;
            _dentro  = 0;
            _vaciado = recibidos < mensajes;
            LlamadasConDatos++;
            long datagramas = 0, lotesDeGro = 0;

            for (int i = 0; i < recibidos; i++)
            {
                ref Linux.MsgHdr h = ref cabeceras[i].Hdr;
                int largo = (int)cabeceras[i].Len;

                // Más largo que el hueco: llegó cortado y se tira, como el que no cabe al leerlo de uno en uno. Con GRO, sin
                // su mensaje de control no se sabe dónde parte: también.
                bool cortado = (h.Flags & Linux.MsgTrunc) != 0 || (conGro && (h.Flags & Linux.MsgCtrunc) != 0);
                int segmento = conGro ? Linux.SegmentoDeGro(ref h) : 0;
                if (segmento >= largo) segmento = 0;
                if (segmento == 0 && largo > _maxDatagrama) cortado = true;
                if (segmento > _maxDatagrama) cortado = true;

                _largos[i]    = cortado ? -1 : largo;
                _segmentos[i] = segmento;
                _origenes[i]  = Origen(nombres + i * Linux.EspacioDeNombre, (int)h.NameLen);
                if (segmento > 0 && !cortado)
                {
                    lotesDeGro++;
                    datagramas += (largo + segmento - 1) / segmento;
                }
                else datagramas++;
            }
            LotesDeGro += lotesDeGro;
            Interlocked.Increment(ref LlamadasDelProceso);
            Interlocked.Add(ref DatagramasDelProceso, datagramas);
            if (lotesDeGro > 0) Interlocked.Add(ref LotesDeGroDelProceso, lotesDeGro);
            return recibidos;
        }

        /// <summary>El EndPoint de la dirección que escribió el sistema: el mismo objeto si ya habló hace poco.</summary>
        private unsafe EndPoint Origen(byte* nombre, int largo)
        {
            largo = Math.Min(largo, Math.Min(Linux.EspacioDeNombre, _leida.Buffer.Length));
            new ReadOnlySpan<byte>(nombre, largo).CopyTo(_leida.Buffer.Span);
            _leida.Size = largo;
            return _recientes.Origen(_leida);
        }

        /// <summary>La llamada no sirve en este proceso: este lector y los que vengan leen de uno en uno.</summary>
        private void SinLlamada()
        {
            _sinLlamada = true;
            _forma = Forma.DeUnoEnUno;
            _cuantos = _actual = _dentro = 0;
            Soltar();
        }

        /// <summary>
        /// Lo de Linux que hace falta para recvmmsg, con los tipos de su ABI: los punteros y los <c>size_t</c> miden lo que la
        /// máquina —<see cref="nint"/>—, así que valen igual en 64 y en 32 bits (ARM de Android, linux-arm). Los números de error
        /// y las banderas son los genéricos de Linux, los mismos en x64, x86, ARM y ARM64.
        /// </summary>
        private static class Linux
        {
            public const int SolUdp = 17, UdpGro = 104;
            public const int MsgDontWait = 0x40, MsgTrunc = 0x20, MsgCtrunc = 0x08;
            public const int EPerm = 1, EIntr = 4, EBadF = 9, EAgain = 11, EInval = 22, ENoSys = 38, EOpNotSupp = 95;

            /// <summary>Lo más que se pide en una llamada: lo que se aparta en la pila por cada uno es ~170 bytes.</summary>
            public const int MaxMensajes = 64;

            /// <summary>Sitio para la dirección de cada mensaje: un sockaddr_in6 mide 28.</summary>
            public const int EspacioDeNombre = 32;

            /// <summary>
            /// Sitio para los mensajes de control de cada uno: el de GRO mide 24 bytes en 64 bits (CMSG_SPACE de un int), y
            /// sobra para otro; si no cupiera, el sistema avisa con MSG_CTRUNC.
            /// </summary>
            public const int EspacioDeControl = 64;

            [StructLayout(LayoutKind.Sequential)]
            public unsafe struct IoVec
            {
                public byte* Base;
                public nuint Len;
            }

            [StructLayout(LayoutKind.Sequential)]
            public unsafe struct MsgHdr
            {
                public byte* Name;
                public uint NameLen;
                public IoVec* Iov;
                public nuint IovLen;
                public byte* Control;
                public nuint ControlLen;
                public int Flags;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MMsgHdr
            {
                public MsgHdr Hdr;
                public uint Len;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct CmsgHdr
            {
                public nuint Len;
                public int Level;
                public int Type;
            }

            /// <summary>CMSG_ALIGN: al tamaño de un puntero.</summary>
            private static int Alinear(int n) => (n + IntPtr.Size - 1) & ~(IntPtr.Size - 1);

            /// <summary>El tamaño de los trozos de un mensaje juntado por GRO, o 0 si llegó solo.</summary>
            public static unsafe int SegmentoDeGro(ref MsgHdr h)
            {
                if (h.Control is null) return 0;
                byte* p   = h.Control;
                byte* fin = p + (nint)h.ControlLen;
                int datos = Alinear(sizeof(CmsgHdr));
                while (p + sizeof(CmsgHdr) <= fin)
                {
                    var c = (CmsgHdr*)p;
                    if (c->Len < (nuint)sizeof(CmsgHdr) || p + (nint)c->Len > fin) break;
                    if (c->Level == SolUdp && c->Type == UdpGro && c->Len >= (nuint)(datos + sizeof(int)))
                        return *(int*)(p + datos);
                    p += Alinear((int)c->Len);
                }
                return 0;
            }

            /// <summary>El SocketError de un errno de Linux, para lo que se le dice a quien lee.</summary>
            public static SocketError ErrorDeSocket(int errno) => errno switch
            {
                EInval => SocketError.InvalidArgument,
                12 or 105 => SocketError.NoBufferSpaceAvailable,   // ENOMEM, ENOBUFS
                88  => SocketError.NotSocket,
                90  => SocketError.MessageSize,
                100 => SocketError.NetworkDown,
                101 => SocketError.NetworkUnreachable,
                102 => SocketError.NetworkReset,
                104 => SocketError.ConnectionReset,
                108 => SocketError.Shutdown,
                110 => SocketError.TimedOut,
                111 => SocketError.ConnectionRefused,
                112 => SocketError.HostDown,
                113 => SocketError.HostUnreachable,
                _   => SocketError.SocketError,
            };

            // El ensamblado desactiva el marshalling del runtime: tipos blittables y punteros, y el error se lee después.
            [DllImport("libc", EntryPoint = "recvmmsg")]
            public static extern unsafe int Recvmmsg(int fd, MMsgHdr* mensajes, uint cuantos, int flags, void* plazo);
        }
#endif
    }
}
