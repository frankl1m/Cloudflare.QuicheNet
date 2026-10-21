using Cloudflare.Quiche;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using static Cloudflare.Quiche.NativeMethods;
using static Cloudflare.QuicheNet.QuicheStream;

namespace Cloudflare.QuicheNet;

public class QuicheConnection : IDisposable
{
    /// <summary>
    /// La dirección, serializada e inmovilizada para que quiche la lea por puntero.
    /// <para>
    /// Quien llama la guarda o la suelta: quiche la pide en <b>cada</b> paquete —la
    /// bomba de recepción la usaba por <c>Recv</c>—, y serializarla, copiarla y
    /// fijarla por paquete era basura continua en el camino caliente. Las direcciones
    /// de una conexión no cambian en su vida: se fijan una vez en el constructor.
    /// </para>
    /// </summary>
    private static (MemoryHandle Handle, int Len) Fijar(EndPoint endPoint)
    {
        SocketAddress? sa = endPoint?.Serialize()
            ?? throw new ArgumentException("endPoint required", nameof(endPoint));

        // net7/net8 no exponen SocketAddress.Buffer: copiar los bytes lógicos.
        byte[] buf = new byte[sa.Size];
        for (int i = 0; i < buf.Length; i++)
        {
            buf[i] = sa[i];
        }
        return (buf.AsMemory().Pin(), buf.Length);
    }

    /// <summary>
    /// Para las pruebas: ve cada conexión que acepta un servidor, al nacer. Las de un nodo tras un relay no se ven de otro modo.
    /// </summary>
    internal static Action<QuicheConnection>? AlAceptarParaPruebas = null;

    /// <param name="techoMaximo">Lo más con que nace, diga lo que diga su camino: ver <see cref="QuicheListener.TechoMaximo"/>.</param>
    internal static QuicheConnection Accept(Socket socket,
        EndPoint remoteEndPoint, ReadOnlyMemory<byte> initialData,
        QuicheConfig config, byte[]? cid = null, bool esH3 = false, int? techoMaximo = null)
    {
        EndPoint localEndPoint = socket.LocalEndPoint ?? throw new ArgumentException(
            "Given socket was not bound to a valid local endpoint!", nameof(socket));

        var (local, local_len) = Fijar(localEndPoint);
        var (remote, remote_len) = Fijar(remoteEndPoint);

        try
        {
            byte[] scidBuf = (byte[]?)cid?.Clone() ?? RandomNumberGenerator
                .GetBytes((int)QuicheLibrary.MAX_CONN_ID_LEN);

            // El servidor comparte el socket de su escucha, que ya lo ajustó. El techo es el del camino hasta este cliente:
            // con DF, lo que deja el sistema hacia él; sin DF, lo que llegue de él. Ver TechoDeQuic.
            var canal = new CanalSocket(socket, ajustarSocket: false);
            var techo = canal.TechoHacia(remoteEndPoint);
            if (techoMaximo is int maximo) techo = techo.Acotado(maximo);
            unsafe
            {
                fixed (byte* scidPtr = scidBuf)
                {
                    // Como al conectar: la configuración puede servir a más de una escucha.
                    quiche_conn* nativa;
                    int alNacer;
                    lock (config)
                    {
                        alNacer = PonerTecho(config, techo, recordado: null);
                        nativa = quiche_accept(
                            scidPtr, (nuint)scidBuf.Length, null, 0,
                            (sockaddr*)local.Pointer, (size_t)local_len,
                            (sockaddr*)remote.Pointer, (size_t)remote_len,
                            config.NativePtr);
                    }

                    QuicheConnection aceptada = new(nativa,
                        config, canal, remoteEndPoint,
                        initialData, scidBuf,
                        local, local_len, remote, remote_len, esH3, techo: techo, techoAlNacer: alNacer);
                    AlAceptarParaPruebas?.Invoke(aceptada);
                    return aceptada;
                }
            }
        }
        catch
        {
            // El saludo falló antes de que la conexión naciera: las direcciones
            // fijadas son de aquí.
            local.Dispose();
            remote.Dispose();
            throw;
        }
    }

    /// <summary>Connects a UDP socket to a QUIC peer.</summary>
    public static Task<QuicheConnection> ConnectAsync(Socket socket, EndPoint remoteEndPoint,
        QuicheConfig config, string? hostname = null, byte[]? cid = null, CancellationToken cancellationToken = default)
        => ConnectWithOptionsAsync(socket, remoteEndPoint, config, hostname, cid, cancellationToken);

    /// <summary>
    /// Conecta como cliente.
    /// <para>
    /// Con <paramref name="sesion"/>, la que entregó <paramref name="alRecibirSesion"/> en una
    /// conexión anterior al mismo sitio, el saludo se reanuda. Si además va
    /// <paramref name="earlyData"/> y la config lo activa, vuelve en cuanto sale el 0-RTT, sin
    /// esperar al saludo: lo que se escriba viaja con el primer vuelo.
    /// </para>
    /// <para>
    /// <paramref name="hostname"/> es el nombre del saludo: va por SNI y, con <c>verify_peer</c>, es el que se comprueba
    /// en el certificado. Con <c>null</c> no va ninguno de los dos: es lo que se hace con una IP, que no va por SNI y que
    /// quiche compararía con los nombres DNS del certificado y no con sus IP. Se pasaba siempre, vacío si era <c>null</c>,
    /// y quiche no creaba la conexión.
    /// </para>
    /// <para>
    /// Un saludo que este lado corta con una alerta TLS —el certificado que no se pudo verificar— falla en cuanto se ve,
    /// con la alerta en <see cref="QuicheException.AlertaTls"/>.
    /// </para>
    /// </summary>
    public static async Task<QuicheConnection> ConnectWithOptionsAsync(Socket socket, EndPoint remoteEndPoint,
        QuicheConfig config, string? hostname = null, byte[]? cid = null, CancellationToken cancellationToken = default,
        bool esH3 = false, byte[]? sesion = null, bool earlyData = false, Action<byte[]>? alRecibirSesion = null)
        => await ConnectAsync(new CanalSocket(socket), remoteEndPoint, config, hostname, cid,
                              cancellationToken, esH3, sesion, earlyData, alRecibirSesion);

    /// <summary>Connects using a previously serialized TLS session.</summary>
    public static Task<QuicheConnection> ConnectAsync(Socket socket, EndPoint remoteEndPoint,
        QuicheConfig config, ReadOnlyMemory<byte> session, string? hostname = null,
        byte[]? cid = null, CancellationToken cancellationToken = default)
        => ConnectWithOptionsAsync(socket, remoteEndPoint, config, hostname, cid, cancellationToken,
            sesion: session.ToArray(), earlyData: config.IsEarlyDataEnabled);

    /// <summary>The serialized TLS session, available after a client receives a session ticket.</summary>
    public unsafe byte[]? Session
    {
        get
        {
            lock (_candado)
            {
                if (NativePtr is null) return null;
                byte* data = null;
                nuint length = 0;
                NativePtr->Session(&data, &length);
                return length == 0 ? null : new ReadOnlySpan<byte>(data, checked((int)length)).ToArray();
            }
        }
    }

    public bool IsInEarlyData => EnEarlyData;
    public bool IsResumed => Reanudada;
    public unsafe bool IsEstablished
    {
        get { lock (_candado) return NativePtr is not null && NativePtr->IsEstablished(); }
    }
    public void SetBrutalRate(ulong bytesPerSecond) => EstablecerRitmoBrutal(bytesPerSecond);
    public byte[] ExportKeyingMaterial(ReadOnlySpan<byte> label, ReadOnlySpan<byte> context, int length)
    {
        byte[] output = new byte[length];
        ExportarMaterialDeClaves(label, context, output);
        return output;
    }
    public void ExportKeyingMaterial(ReadOnlySpan<byte> label, ReadOnlySpan<byte> context, Span<byte> output)
        => ExportarMaterialDeClaves(label, context, output);

    internal static Task<QuicheConnection> ConnectAsync(Socket socket, EndPoint remoteEndPoint, QuicheConfig config,
        Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic techo, string? hostname, CancellationToken cancellationToken)
        => ConnectAsync(new CanalSocket(socket), remoteEndPoint, config, hostname, cancellationToken: cancellationToken,
                        techo: techo);

    /// <summary>
    /// Conecta como cliente por un canal de datagramas cualquiera: el mismo saludo que por
    /// socket, pero todo lo que entra y sale pasa por <paramref name="canal"/>. Es lo que deja
    /// poner debajo un camino que no es un socket.
    /// </summary>
    /// <param name="techo">El techo de sus paquetes; sin él, el derivado de la pista del canal hacia el par (ver <see cref="ICanalDatagramas.PistaHacia"/>).</param>
    internal static async Task<QuicheConnection> ConnectAsync(ICanalDatagramas canal, EndPoint remoteEndPoint,
        QuicheConfig config, string? hostname = null, byte[]? cid = null, CancellationToken cancellationToken = default,
        bool esH3 = false, byte[]? sesion = null, bool earlyData = false, Action<byte[]>? alRecibirSesion = null,
        Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic? techo = null)
    {
        EndPoint localEndPoint = canal.Local;

        var (local, local_len) = Fijar(localEndPoint);
        var (remote, remote_len) = Fijar(remoteEndPoint);

        QuicheConnection? conn = null;
        try
        {
            // Sin nombre, un puntero nulo: quiche no pone SNI ni nombre que comprobar. Ver la nota del método.
            byte[]? hostnameBuf = hostname is null ? null : Encoding.UTF8.GetBytes([.. hostname.ToCharArray(), '\u0000']);
            byte[] scidBuf = (byte[]?)cid?.Clone() ?? RandomNumberGenerator
                .GetBytes((int)QuicheLibrary.MAX_CONN_ID_LEN);
            bool reanuda = false;

            // El techo del camino de esta conexión, y lo que anunció este par la última vez: lo de más no le llega.
            techo ??= canal.TechoHacia(remoteEndPoint);
            int? recordado = MaxDelParRecordado(remoteEndPoint);
            unsafe
            {
                fixed (byte* hostnamePtr = hostnameBuf)
                fixed (byte* scidPtr = scidBuf)
                {
                    // La configuración puede ser compartida entre conexiones, y quiche la toma como
                    // exclusiva mientras crea una: dos que nacen a la vez van en fila. El techo se escribe en ella con el
                    // mismo candado, justo antes: quiche lo copia a la conexión al crearla. Ver TechoDeQuic.
                    quiche_conn* nativa;
                    int alNacer;
                    lock (config)
                    {
                        alNacer = PonerTecho(config, techo, recordado);
                        nativa = quiche_connect(hostnamePtr,
                            scidPtr, (nuint)scidBuf.Length,
                            (sockaddr*)local.Pointer, (size_t)local_len,
                            (sockaddr*)remote.Pointer, (size_t)remote_len,
                            config.NativePtr);
                    }

                    // La sesión se ofrece antes de que salga ningún paquete: después quiche no
                    // la admite. Una sesión que ya no vale no impide conectar; solo no reanuda.
                    if (nativa is not null && sesion is { Length: > 0 })
                    {
                        fixed (byte* sesionPtr = sesion)
                        {
                            reanuda = nativa->SetSession(sesionPtr, (nuint)sesion.Length) == 0;
                        }
                    }

                    conn = new(nativa, config, canal, remoteEndPoint,
                        ReadOnlyMemory<byte>.Empty, scidBuf,
                        local, local_len, remote, remote_len, esH3, alRecibirSesion, techo, alNacer);
                }
            }

            Task lista = reanuda && earlyData && config.IsEarlyDataEnabled
                ? conn.UtilizableTask
                : conn.ConnectionEstablishedTask;
            await lista.WaitAsync(cancellationToken);
            return conn;
        }
        catch
        {
            if (conn is null)
            {
                // Falló antes de nacer: las direcciones fijadas son de aquí.
                local.Dispose();
                remote.Dispose();
            }
            else
            {
                try { conn.Dispose(); } catch { }
            }
            throw;
        }
    }

    private readonly QuicheConfig config;

    private readonly Task? listenTask;
    private readonly Task recvTask, sendTask;
    private readonly Task? recvStreamTask, sendStreamTask;
    private readonly CancellationTokenSource cts;

    private readonly TaskCompletionSource establishedTcs;

    /// <summary>
    /// Se completa cuando la conexión ya admite datos de flujo: al establecerse o, con early
    /// data, en cuanto el 0-RTT sale (cliente) o se acepta (servidor).
    /// </summary>
    private readonly TaskCompletionSource utilizableTcs;

    /// <summary>La config activa early data: hay que mirar si la conexión entra en 0-RTT.</summary>
    private readonly bool _earlyData;

    /// <summary>Quien guarda la sesión TLS para reanudar. Se llama una vez y se suelta.</summary>
    private Action<byte[]>? _alRecibirSesion;

    /// <summary>Tandas de paquetes miradas buscando el ticket, para no mirar siempre.</summary>
    private int _busquedasDeSesion;
    private readonly ConcurrentDictionary<ulong, QuicheStream> streamMap;
    private readonly Channel<QuicheStream> streamChannel;

    // ── Despertares de las bombas ───────────────────────────────────────
    // Cada etapa del camino de datos despierta a la siguiente en vez de esperar
    // un tic de sondeo: en Windows un Task.Delay corto nunca baja de ~15,6 ms
    // —el cuanto del reloj—, y con cuatro etapas por sentido la vuelta entera
    // se comía ~125 ms en tics puros. El dato que llega despierta al instante.

    // Sin reservar nada al dormir: ver AvisoDeBomba. El plazo de seguridad de las tres primeras lo vigila _guardia.

    /// <summary>Hay paquetes en la cola de recepción (los pone el socket).</summary>
    private readonly AvisoDeBomba bombaRecv = new();

    /// <summary>Los paquetes procesados dejaron flujos legibles (avisa la bomba de paquetes).</summary>
    private readonly AvisoDeBomba bombaFlujosRecv = new();

    /// <summary>Un flujo encoló datos o su FIN (los manda el bucle de flujos).</summary>
    private readonly AvisoDeBomba bombaFlujos = new();

    /// <summary>quiche tiene paquetes que mandar (lo avisa quien le metió datos).</summary>
    private readonly AvisoDeBomba bombaEnvio = new();

    /// <summary>El plazo de seguridad de las bombas que lo tienen: una cita para todas. Ver <see cref="GuardiaDeBombas"/>.</summary>
    private readonly GuardiaDeBombas _guardia;

    // Un aviso pendiente por bomba, no uno por llamada. El semáforo de antes acumulaba la cuenta: una
    // ráfaga de mil datagramas dejaba mil vueltas vacías a la bomba, cada una con su cerrojo y
    // su llamada nativa, y lo que podía salir junto salía en paquetes sueltos. La bomba borra
    // la marca ANTES de mirar si hay trabajo, y quien avisa deja el trabajo antes de marcar:
    // así ningún aviso se pierde.
    private int _avisoRecv, _avisoFlujos, _avisoEnvio;

    /// <summary>
    /// El cerrojo del motor nativo. Antes era <c>lock (this)</c> sobre una clase pública: cualquiera que bloqueara
    /// sobre la conexión se metía en la exclusión de las bombas. Es <c>internal</c> y no privado porque el HTTP/3 de
    /// encima —<see cref="QuicheH3Conn"/>— llama al mismo motor y tiene que serializarse con ellas.
    /// </summary>
    private readonly object _candado = new();

    /// <summary>Ver <see cref="_candado"/>: solo para quien comparte el motor nativo con esta conexión.</summary>
    internal object Candado => _candado;

    internal void SeñalizarRecv()
    {
        if (Interlocked.Exchange(ref _avisoRecv, 1) == 0) bombaRecv.Avisar();
    }

    /// <summary>
    /// Encola un paquete arrendado que trae otro bucle —el del listener— y avisa a la bomba. Si la conexión ya se
    /// dispuso, su Dispose pudo vaciar la cola antes de este Enqueue, y el búfer se quedaba fuera del pool: se vacía
    /// aquí también. Dos que vacían a la vez no se pisan, cada TryDequeue saca un búfer distinto.
    /// </summary>
    /// <param name="origenNuevo">El origen, si no es el del camino: un rebinding del cliente, a confirmar. Ver <see cref="Encolar"/>.</param>
    internal void EncolarRecibido(byte[] buf, int largo, EndPoint? origenNuevo = null)
    {
        if (!Encolar(buf, largo, origenNuevo)) return;
        SeñalizarRecv();

        if (_sinRecepcion)
            while (Sacar(out var sobra)) Devolver(sobra.Buf);
    }

    /// <summary>
    /// Desde aquí la conexión ya no recibe: sus bombas se están parando. No vale con el Dispose empezado —la recepción sigue
    /// durante el cierre con gracia, que vive de las confirmaciones del par—: se tiraba todo lo que llegaba desde el primer
    /// momento, y la ventana de congestión no se abría nunca.
    /// </summary>
    private volatile bool _sinRecepcion;

    /// <summary>
    /// Una bomba murió por un error de quiche: la conexión ya no sirve. Se da por cerrada —las demás bombas salen al verlo— y
    /// los lectores de sus flujos ven el fin. Salía sin más, y sin nadie que atendiera ya ese lado, la conexión se quedaba a
    /// medias: viva a ojos de todos, sin fin para los flujos.
    /// </summary>
    private void MarcarMuerta()
    {
        _cerrada = true;
        AnotarCerrada();
        CompletarFlujos();
    }

    internal void SeñalizarFlujos()
    {
        if (Interlocked.Exchange(ref _avisoFlujos, 1) == 0) bombaFlujos.Avisar();
    }

    /// <summary>
    /// Los flujos con algo por pasar a quiche —datos o su FIN—: lo único que recorre la bomba de flujos. Recorría el mapa
    /// entero en cada vuelta, con un enumerador nuevo y el cerrojo de cada flujo, también de los ociosos; y cada tanda de
    /// paquetes con trabajo la despierta, así que con el UDP de Hysteria 2, TUIC o QSOCKS eran un objeto y una pasada por
    /// todos los flujos por datagrama recibido, sin nada que mandar. Cada flujo está una vez como mucho: ver
    /// <see cref="QuicheStream.MarcarPendiente"/>.
    /// </summary>
    private readonly ConcurrentQueue<QuicheStream> _flujosPendientes = new();

    /// <summary>Los pendientes que recorre la vuelta en curso de la bomba de flujos. Solo los toca ella.</summary>
    private readonly List<QuicheStream> _flujosEnCurso = new();

    /// <summary>Los flujos marcados como pendientes —en la lista o en la vuelta en curso—: ver <see cref="HayOtrosEsperando"/>.</summary>
    private int _flujosMarcados;

    /// <summary>
    /// Lo más que manda un flujo por turno cuando varios esperan ventana: ver la bomba de flujos. Con uno solo, sin tope. El
    /// de un paquete haría justo el reparto a costa de una llamada a quiche por paquete; con esto, los pequeños —una respuesta,
    /// un saludo— caben en su primer turno.
    /// </summary>
    private const int TurnoDeEnvio = 16 * 1024;

    /// <summary>
    /// Si otro flujo que no es <paramref name="yo"/> espera ventana. Entonces quien escribe no manda en línea: se pone a la cola
    /// de la bomba, que reparte por turnos. En línea se llevaba la ventana que abrían los ACK quien escribiera primero —las
    /// bajadas que escriben sin parar—, y lo pendiente de los demás esperaba detrás.
    /// </summary>
    internal bool HayOtrosEsperando(QuicheStream yo)
        => Volatile.Read(ref _flujosMarcados) - (yo.EstaPendiente ? 1 : 0) > 0;

    /// <summary>
    /// <paramref name="flujo"/> tiene algo por pasar a quiche. Con <paramref name="avisar"/> despierta además a la bomba: lo
    /// que se quedó por una ventana llena, o por el tope de flujos del par, no la despierta —la despierta el paquete que traiga
    /// la ventana nueva: ver <see cref="_flujosBloqueados"/>—, y se reintenta entonces, como antes.
    /// </summary>
    internal void AnotarPendiente(QuicheStream flujo, bool avisar)
    {
        if (flujo.MarcarPendiente())
        {
            Interlocked.Increment(ref _flujosMarcados);
            _flujosPendientes.Enqueue(flujo);
        }
        if (avisar) SeñalizarFlujos();
    }

    /// <summary>Saca a un flujo de la vuelta en curso, y lo vuelve a anotar sin despertar a nadie si aún le queda algo.</summary>
    private void Reanotar(QuicheStream flujo)
    {
        flujo.DesmarcarPendiente();
        Interlocked.Decrement(ref _flujosMarcados);
        if (flujo.EnvioPendiente) AnotarPendiente(flujo, avisar: false);
    }

    internal void SeñalizarEnvio()
    {
        if (Interlocked.Exchange(ref _avisoEnvio, 1) == 0) bombaEnvio.Avisar();
    }

    /// <summary>
    /// El aviso de «salió o llegó algo» para el vigilante del camino, que con la conexión ociosa no mira nada hasta que hay
    /// tráfico. Lo pone <see cref="SiguienteActividadAsync"/>, y lo completan la bomba de envío al vaciar un lote y la de
    /// recepción al procesar paquetes.
    /// </summary>
    private TaskCompletionSource? _actividad;

    /// <summary>
    /// Se completa en cuanto salga o llegue el siguiente paquete. Para el vigilante del camino: leía el camino cuatro veces
    /// por segundo con la conexión ociosa, sin nada que vigilar, y ahora duerme hasta que hay tráfico. Lo que llega también
    /// lo despierta: si no, al despertar con un envío no sabría cuándo llegó lo último, y el silencio del camino contaría
    /// desde que despierta —un camino mudo tras un rato de reposo se veía un segundo más tarde—. Quien espera también espera
    /// al cierre (<see cref="WaitForShutdownAsync"/>).
    /// </summary>
    internal Task SiguienteActividadAsync()
    {
        var aviso = Volatile.Read(ref _actividad);
        if (aviso is null)
        {
            var nuevo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            aviso = Interlocked.CompareExchange(ref _actividad, nuevo, null) ?? nuevo;
        }
        return aviso.Task;
    }

    /// <summary>Salió o llegó algo: quien lo esperaba, sigue. Una lectura sin nadie esperando.</summary>
    private void AvisarDeActividad()
    {
        if (Volatile.Read(ref _actividad) is not null) Interlocked.Exchange(ref _actividad, null)?.TrySetResult();
    }

    /// <summary>
    /// Los datagramas recibidos, en orden. Los mete la bomba de recepción en cuanto quiche
    /// procesa el paquete que los trae, sin sondeo. Si el lector no da abasto se pierde el
    /// más viejo, como hace quiche con su propia cola, y su búfer vuelve al almacén.
    /// <para>
    /// Un solo lector a la vez, que espera sin reservar nada también con un token cancelable:
    /// con un canal, cada espera cancelable era un objeto, y la bomba de datagramas de los
    /// servidores espera con el token de su conexión.
    /// </para>
    /// </summary>
    private readonly Cloudflare.QuicheNet.Internal.ColaAcotada<DatagramaQuic>? datagramasRecibidos;

    /// <summary>
    /// Un datagrama recibido, en un búfer prestado del almacén del que valen <see cref="Largo"/> bytes. Era un arreglo de su
    /// medida por datagrama: quien lo lee y copia lo que se queda —el reparto del UDP de las salidas— lo devuelve al acabar.
    /// </summary>
    internal readonly record struct DatagramaQuic(byte[] Buf, int Largo)
    {
        public ReadOnlyMemory<byte> Memoria => Buf.AsMemory(0, Largo);

        /// <summary>Devuelve el búfer al almacén: desde ahí no se lee.</summary>
        public void Devolver() => ArrayPool<byte>.Shared.Return(Buf);

        /// <summary>Un arreglo de su medida con lo recibido, y el búfer de vuelta al almacén.</summary>
        public byte[] Soltar()
        {
            byte[] propio = Largo == 0 ? Array.Empty<byte>() : Buf.AsSpan(0, Largo).ToArray();
            Devolver();
            return propio;
        }
    }

    /// <summary>
    /// Lo completa la bomba de envío cada vez que vacía quiche. Así despierta a quien espera
    /// sitio en la cola de envío de datagramas.
    /// </summary>
    private TaskCompletionSource? _huecoDgram;

    private readonly ICanalDatagramas canal;
    private EndPoint remoteEndPoint;

    /// <summary>El extremo remoto de la conexión, para mostrarlo o sondear.</summary>
    public EndPoint? RemoteEndPoint => remoteEndPoint;

    /// <summary>
    /// Los paquetes recibidos sin procesar, con su origen si no es el del camino: se repunta solo si quiche lo acepta —ver la
    /// bomba de recepción—. Con tope: ver <see cref="TopeDeCola"/>.
    /// </summary>
    internal readonly ConcurrentQueue<(byte[] Buf, int Len, EndPoint? Origen)> recvQueue;

    /// <summary>
    /// Lo que hay en la cola, y su tope. Por encima se tira, como haría el socket con su búfer lleno: sin tope, una bomba que
    /// se retrasaba dejaba crecer la cola —un búfer de 8 KB por paquete— sin que nada frenara.
    /// </summary>
    private int _enCola;
    private const int TopeDeCola = 1024;

    /// <summary>Paquetes tirados porque la cola estaba llena. Ver <see cref="TopeDeCola"/>.</summary>
    internal long PaquetesTirados;

    /// <summary>
    /// Lo más que procesa la bomba de recepción en una vuelta antes de dejar salir el ACK y avisar a los lectores de los
    /// flujos; lo que quede va en la vuelta siguiente, sin dormir.
    /// <para>
    /// Con la lectura por lotes de Linux (<see cref="Cloudflare.QuicheNet.Internal.LectorDeDatagramas"/>) el socket mete en la cola un
    /// lote entero de golpe: los ~40 paquetes de un envío partido, que llegan juntos por GRO. La bomba los procesaba todos
    /// antes de avisar, y el ACK y lo que espera la aplicación salían a tirones, sin solaparse con el resto del lote: por
    /// lotes y sin tope, la CPU por MB de QUIC no bajaba, y la de masque h3 subía un 20 %. Con el tope en 32 baja un
    /// 16-22 %; con 16, un 12-17 %; con 8, un 8-12 %, y con 48, como con 32 pero con más ruido. El tope sin leer por lotes
    /// no cambia nada. Leyendo de uno en uno —Windows, macOS, iOS— la cola no se llena de golpe: sin tope, como siempre.
    /// </para>
    /// </summary>
    private static readonly int TandaDeRecepcion =
        Cloudflare.QuicheNet.Internal.LectorDeDatagramas.PlataformaConLotes ? 32 : int.MaxValue;

    /// <summary>
    /// A la cola, si cabe: si no, el búfer vuelve al pool y el paquete cuenta como tirado. Lo más largo que llega del par es
    /// el techo de lo que se le manda por un socket sin DF: ver <see cref="LimiteDeAhora"/>.
    /// </summary>
    private bool Encolar(byte[] buf, int largo, EndPoint? origen)
    {
        if (largo > _mayorRecibido)
        {
            if (_mayorRecibido == 0) _primerRecibido = largo;
            Volatile.Write(ref _mayorRecibido, largo);
        }
        if (Interlocked.Increment(ref _enCola) > TopeDeCola)
        {
            Interlocked.Decrement(ref _enCola);
            Interlocked.Increment(ref PaquetesTirados);
            Devolver(buf);
            return false;
        }
        recvQueue.Enqueue((buf, largo, origen));
        return true;
    }

    /// <summary>De la cola, con su cuenta.</summary>
    private bool Sacar(out (byte[] Buf, int Len, EndPoint? Origen) paquete)
    {
        if (!recvQueue.TryDequeue(out paquete)) return false;
        Interlocked.Decrement(ref _enCola);
        return true;
    }

    /// <summary>El HTTP/3 de quiche vive encima: las bombas de flujos crudos están apagadas.</summary>
    internal bool EsH3 { get; }

    /// <summary>
    /// Se completa cuando la conexión se acaba —la primera bomba que se entera lo anota—.
    /// Es lo que avisa al h3 de encima para que deje de esperar paquetes que no vendrán.
    /// </summary>
    private readonly TaskCompletionSource cerradaTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Cerrada => cerradaTcs.Task;

    private void AnotarCerrada()
    {
        cerradaTcs.TrySetResult();

        // Cerrada sin llegar a establecerse —el saludo caducó, o el par se fue—: quien
        // espera el saludo se entera ya. Antes se quedaba esperando para siempre. Y con el
        // porqué, si quiche lo guarda: salía solo «QUICHE_ERR_DONE», y un servidor que
        // rechazaba el saludo —por ALPN, por versión, por el certificado— no decía nada.
        if (!establishedTcs.Task.IsCompleted)
            establishedTcs.TrySetException(new QuicheException(QuicheError.QUICHE_ERR_DONE,
                DescribirCierre() is { } porque
                    ? $"La conexión se cerró antes de establecerse: {porque}."
                    : "La conexión se cerró antes de establecerse."));
        utilizableTcs.TrySetException(new QuicheException(QuicheError.QUICHE_ERR_DONE,
            "La conexión se cerró antes de poder usarse."));
    }

    /// <summary>La bomba de h3 espera aquí: lo despierta la bomba de paquetes al procesar.</summary>
    internal AvisoDeBomba AvisoFlujosRecv => bombaFlujosRecv;

    /// <summary>Los parámetros de transporte del par, para sondear límites.</summary>
    internal unsafe quiche_transport_params ParametrosDelPar()
    {
        lock (_candado)
        {
            var salida = default(quiche_transport_params);
            if (NativePtr is null) return salida;   // ya liberada
            NativeMethods.quiche_conn_peer_transport_params(NativePtr, &salida);
            return salida;
        }
    }

    /// <summary>
    /// Cuántos flujos bidireccionales de salida quedan antes del límite del par.
    /// <para>
    /// Es el valor VIVO —cuenta también los MAX_STREAMS que lleguen después—, no el
    /// parámetro inicial. Lo usa la conexión compartida como defensa de fondo: el
    /// registro admite con margen, pero si una carrera colara una sesión de más, abrir
    /// el flujo por encima del tope devuelve <c>QUICHE_ERR_STREAM_LIMIT</c> y antes
    /// eso mataba la conexión entera con sus flujos vivos dentro.
    /// </para>
    /// </summary>
    internal unsafe ulong FlujosBidiLibres()
    {
        lock (_candado)
        {
            return NativePtr is null ? 0 : NativeMethods.quiche_conn_peer_streams_left_bidi(NativePtr);
        }
    }

    /// <summary>
    /// Cuántos flujos unidireccionales de salida quedan antes del límite del par. Lo mismo que
    /// <see cref="FlujosBidiLibres"/> para quien abre un flujo por mensaje —el UDP de TUIC en modo «quic»—: abrir por
    /// encima del tope devuelve <c>QUICHE_ERR_STREAM_LIMIT</c>, y es mejor saberlo antes y tirar el mensaje.
    /// </summary>
    internal unsafe ulong FlujosUniLibres()
    {
        lock (_candado)
        {
            return NativePtr is null ? 0 : NativeMethods.quiche_conn_peer_streams_left_uni(NativePtr);
        }
    }

    /// <summary>
    /// El tope de flujos bidireccionales que anunció el par, para calcular cuántos admitir.
    /// <para>
    /// En 0-RTT quiche no enseña los parámetros del par hasta que llega el saludo, pero ya
    /// aplica los límites de la sesión reanudada. Sin ningún flujo abierto aún, los libres son
    /// justo ese tope, así que hay que llamarlo nada más conectar.
    /// </para>
    /// </summary>
    internal ulong TopeBidiDelPar() => Math.Max(ParametrosDelPar().peer_initial_max_streams_bidi, FlujosBidiLibres());

    /// <summary>
    /// El ALPN que se negoció en el saludo, o vacío si aún no hay ninguno. Es lo que deja a un listener con varios
    /// ALPN repartir cada conexión establecida a quien habla el suyo.
    /// </summary>
    internal unsafe string ProtocoloDeAplicacion()
    {
        lock (_candado)
        {
            if (NativePtr is null) return "";
            byte* alpn = null; nuint largo = 0;
            NativeMethods.quiche_conn_application_proto(NativePtr, &alpn, &largo);
            return largo == 0 ? "" : System.Text.Encoding.ASCII.GetString(alpn, (int)largo);
        }
    }

    /// <summary>El error de cierre local, para sondear por qué se cerró la conexión.</summary>
    internal unsafe string MotivoDelCierre()
    {
        lock (_candado)
        {
            if (NativePtr is null) return "conexión ya liberada";
            bool esApp = false; ulong codigo = 0; byte* motivo = null; nuint len = 0;
            if (NativeMethods.quiche_conn_local_error(NativePtr, &esApp, &codigo, &motivo, &len))
            {
                string texto = len > 0 ? System.Text.Encoding.UTF8.GetString(motivo, (int)len) : "";
                return $"local: app={esApp} código=0x{codigo:X} «{texto}»";
            }
            // El del PAR también: sin él, una conexión que cerraba el servidor salía como
            // «sin error local» y no había forma de saber por qué.
            if (NativeMethods.quiche_conn_peer_error(NativePtr, &esApp, &codigo, &motivo, &len))
            {
                string texto = len > 0 ? System.Text.Encoding.UTF8.GetString(motivo, (int)len) : "";
                return $"par: app={esApp} código=0x{codigo:X} «{texto}»";
            }
            if (NativeMethods.quiche_conn_is_timed_out(NativePtr)) return "inactividad (timeout de quiche)";
            return "sin error local ni del par";
        }
    }

    /// <summary>
    /// Quién cerró la conexión y con qué, en palabras: el CONNECTION_CLOSE del par, o el de este lado, con el nombre de su
    /// código —RFC 9000 §20.1, RFC 9114 §8.1 y las alertas TLS de RFC 8446 §6.2— y el motivo que traiga. null si quiche no
    /// guarda ninguno.
    /// </summary>
    internal unsafe string? DescribirCierre()
    {
        lock (_candado)
        {
            if (NativePtr is null) return null;
            bool esApp = false; ulong codigo = 0; byte* motivo = null; nuint len = 0;
            string quien;
            if (NativeMethods.quiche_conn_peer_error(NativePtr, &esApp, &codigo, &motivo, &len)) quien = "el par cerró";
            else if (NativeMethods.quiche_conn_local_error(NativePtr, &esApp, &codigo, &motivo, &len)) quien = "este lado cerró";
            else return NativeMethods.quiche_conn_is_timed_out(NativePtr) ? "sin respuesta del par en el plazo de quiche" : null;
            string texto = len > 0 ? System.Text.Encoding.UTF8.GetString(motivo, (int)len) : "";
            return $"{quien} con {NombreDelError(esApp, codigo)}" + (texto.Length > 0 ? $" «{texto}»" : "");
        }
    }

    /// <summary>El nombre de un código de CONNECTION_CLOSE: de transporte, de aplicación (h3) o una alerta TLS.</summary>
    internal static string NombreDelError(bool esApp, ulong codigo)
    {
        if (esApp)
            return (codigo switch
            {
                0x100 => "H3_NO_ERROR",                0x101 => "H3_GENERAL_PROTOCOL_ERROR", 0x102 => "H3_INTERNAL_ERROR",
                0x103 => "H3_STREAM_CREATION_ERROR",   0x104 => "H3_CLOSED_CRITICAL_STREAM", 0x105 => "H3_FRAME_UNEXPECTED",
                0x106 => "H3_FRAME_ERROR",             0x107 => "H3_EXCESSIVE_LOAD",         0x108 => "H3_ID_ERROR",
                0x109 => "H3_SETTINGS_ERROR",          0x10A => "H3_MISSING_SETTINGS",       0x10B => "H3_REQUEST_REJECTED",
                0x10C => "H3_REQUEST_CANCELLED",       0x10D => "H3_REQUEST_INCOMPLETE",     0x10E => "H3_MESSAGE_ERROR",
                0x10F => "H3_CONNECT_ERROR",           0x110 => "H3_VERSION_FALLBACK",
                _     => "un error de la aplicación",
            }) + $" (0x{codigo:X})";

        if (codigo is >= 0x100 and <= 0x1FF)
        {
            byte alerta = (byte)(codigo - 0x100);
            string? nombre = alerta switch
            {
                10  => "unexpected_message",   20 => "bad_record_mac",          40 => "handshake_failure",
                42  => "bad_certificate",      43 => "unsupported_certificate", 44 => "certificate_revoked",
                45  => "certificate_expired",  46 => "certificate_unknown",     47 => "illegal_parameter",
                48  => "unknown_ca",           49 => "access_denied",           50 => "decode_error",
                51  => "decrypt_error",        70 => "protocol_version",        71 => "insufficient_security",
                80  => "internal_error",       86 => "inappropriate_fallback",  90 => "user_canceled",
                109 => "missing_extension",   110 => "unsupported_extension",  112 => "unrecognized_name",
                113 => "bad_certificate_status_response", 115 => "unknown_psk_identity",
                116 => "certificate_required", 120 => "no_application_protocol",
                _   => null,
            };
            return $"CRYPTO_ERROR, la alerta TLS {alerta}" + (nombre is null ? "" : $" ({nombre})") + $" (0x{codigo:X})";
        }

        return (codigo switch
        {
            0x0 => "NO_ERROR",                  0x1 => "INTERNAL_ERROR",            0x2 => "CONNECTION_REFUSED",
            0x3 => "FLOW_CONTROL_ERROR",        0x4 => "STREAM_LIMIT_ERROR",        0x5 => "STREAM_STATE_ERROR",
            0x6 => "FINAL_SIZE_ERROR",          0x7 => "FRAME_ENCODING_ERROR",      0x8 => "TRANSPORT_PARAMETER_ERROR",
            0x9 => "CONNECTION_ID_LIMIT_ERROR", 0xA => "PROTOCOL_VIOLATION",        0xB => "INVALID_TOKEN",
            0xC => "APPLICATION_ERROR",         0xD => "CRYPTO_BUFFER_EXCEEDED",    0xE => "KEY_UPDATE_ERROR",
            0xF => "AEAD_LIMIT_REACHED",        0x10 => "NO_VIABLE_PATH",
            _   => "un error de transporte",
        }) + $" (0x{codigo:X})";
    }

    /// <summary>
    /// La alerta TLS con la que este lado cerró la conexión, o <c>null</c> si no la cerró así. quiche la deja como su error
    /// local de transporte: 0x100 más la alerta (RFC 9001 §4.8), la que mandó BoringSSL —la 48, <c>unknown_ca</c>, si no
    /// pudo verificar el certificado del par; la 42, <c>bad_certificate</c>, si no es para el nombre del saludo—.
    /// </summary>
    internal unsafe byte? AlertaTlsLocal()
    {
        lock (_candado)
        {
            if (NativePtr is null) return null;
            bool esApp = false; ulong codigo = 0; byte* motivo = null; nuint len = 0;
            if (!NativeMethods.quiche_conn_local_error(NativePtr, &esApp, &codigo, &motivo, &len) || esApp) return null;
            return codigo is >= 0x100 and <= 0x1FF ? (byte)(codigo - 0x100) : null;
        }
    }

    /// <summary>
    /// La alerta TLS con la que el PAR cerró la conexión, o <c>null</c> si no la cerró así: la 48, <c>unknown_ca</c>, de un
    /// cliente que no confía en la CA del certificado que se le dio; la 42, <c>bad_certificate</c>, si no es para su nombre.
    /// <see cref="AlertaTlsLocal"/>, del otro lado.
    /// </summary>
    internal unsafe byte? AlertaTlsDelPar()
    {
        lock (_candado)
        {
            if (NativePtr is null) return null;
            bool esApp = false; ulong codigo = 0; byte* motivo = null; nuint len = 0;
            if (!NativeMethods.quiche_conn_peer_error(NativePtr, &esApp, &codigo, &motivo, &len) || esApp) return null;
            return codigo is >= 0x100 and <= 0x1FF ? (byte)(codigo - 0x100) : null;
        }
    }

    /// <summary>
    /// El saludo lo cortó este lado con una alerta TLS: quien lo espera se entera ya, y con la alerta. Se enteraba cuando la
    /// conexión acababa de cerrarse, con «la conexión se cerró antes de establecerse» y sin saber por qué. El
    /// CONNECTION_CLOSE con la alerta sale igual: se despierta a la bomba de envío para que salga ya.
    /// </summary>
    private void FallarSaludoPorTls()
    {
        if (establishedTcs.Task.IsCompleted || AlertaTlsLocal() is not byte alerta) return;

        var error = new QuicheException(QuicheError.QUICHE_ERR_TLS_FAIL,
            $"El saludo TLS lo cortó este lado con la alerta {alerta}.") { AlertaTls = alerta };
        establishedTcs.TrySetException(error);
        utilizableTcs.TrySetException(error);
        SeñalizarEnvio();
    }

    /// <summary>
    /// La conexión se cerró durante el saludo —el CONNECTION_CLOSE de un par que lo rechaza: por ALPN, por versión, por el
    /// nombre; el h3 de www.microsoft.com, con la alerta TLS 80—: quien lo espera se entera ya, y con el porqué
    /// (<see cref="DescribirCierre"/>). Se enteraba al acabar el drenaje, tres PTO después: sin RTT medido, unos 3 s, también
    /// el Auto que esperaba ese h3 para no abrir por TCP.
    /// </summary>
    private void FallarSaludoPorCierre()
    {
        if (establishedTcs.Task.IsCompleted || !IsDraining) return;
        var error = new QuicheException(QuicheError.QUICHE_ERR_DONE,
            $"La conexión se cerró antes de establecerse: {DescribirCierre() ?? "sin motivo"}.");
        establishedTcs.TrySetException(error);
        utilizableTcs.TrySetException(error);
    }

    /// <summary>
    /// Una lectura del camino de la conexión, tal como la cuenta quiche: paquetes, PTO vencidos
    /// y RTT, este en nanosegundos.
    /// </summary>
    internal readonly record struct MuestraDeCamino(
        ulong Recibidos, ulong Enviados, ulong Perdidos, ulong PtoTotales,
        ulong RttNs, ulong RttVarNs, ulong Pmtu)
    {
        /// <summary>
        /// Lo que el par puede tardar en confirmar un paquete: el <c>max_ack_delay</c> por omisión de quiche. Estaba escrito a
        /// mano en el vigilante del camino y en el cierre.
        /// </summary>
        public const double RetrasoDeAckMs = 25;

        /// <summary>
        /// Un PTO, sin su backoff: el RTT suavizado, cuatro veces su variación y <see cref="RetrasoDeAckMs"/> (RFC 9002
        /// §6.2.1), en milisegundos. Es lo que tarda en poder contestarse lo que sale: lo usan el vigilante del camino —la
        /// espera de las últimas sondas— y el cierre —la quietud que espera a las retransmisiones—, que lo calculaban cada
        /// uno por su cuenta.
        /// </summary>
        public double PtoMs => (RttNs + 4.0 * RttVarNs) / 1_000_000.0 + RetrasoDeAckMs;
    }

    /// <summary>
    /// Lo que quiche sabe del camino, sin mandar nada. Lo lee el vigilante de camino mudo cada
    /// cuarto de segundo, y cuesta un cerrojo y una copia. Es siempre el camino 0: el rebinding
    /// no se lo cuenta a quiche, así que nunca nace otro.
    /// </summary>
    /// <returns>Falso con la conexión ya liberada, o si quiche no tiene ese camino.</returns>
    internal unsafe bool MuestraDelCamino(out MuestraDeCamino muestra)
    {
        lock (_candado)
        {
            muestra = default;
            if (NativePtr is null) return false;

            if (DisposicionUnix32.LeerPathStats(NativePtr, 0, out quiche_path_stats s) != 0) return false;

            muestra = new MuestraDeCamino(s.recv, s.sent, s.lost, s.total_pto_count,
                                          s.rtt, s.rttvar, s.pmtu);
            return true;
        }
    }

    /// <summary>
    /// ¿Sale ya un datagrama de <paramref name="largo"/> bytes, en el próximo paquete? quiche lo acepta en su cola si cabe en
    /// <see cref="MaxDatagramSize"/>, que mientras su PMTUD sondea se mide con el tamaño de la sonda; pero sus paquetes salen
    /// con la MTU ya confirmada —1200 hasta que la sonda vuelve, un RTT tras el saludo—, y uno que no cabe en ella espera en
    /// la cabeza de la cola, con todos los de detrás. Quien puede esperar a mandarlo, como un sondeo, lo mira antes.
    /// <para>
    /// Quien pregunta va a mandarlo, y no lo puede trocear —el eco de 1280 bytes de CONNECT-IP—: si no sale porque no cabe en
    /// lo que ha llegado de un par que no busca, este lado busca su camino por su cuenta (ver <see cref="PedirSitio"/>).
    /// </para>
    /// </summary>
    internal bool DatagramaSaleYa(int largo)
    {
        if (largo <= MaxDatagramaQueSaleYa) return true;
        PedirSitioPara(largo);
        return false;
    }

    /// <summary>
    /// Quien llama tiene que mandar un datagrama de <paramref name="largo"/> bytes que no cabe ahora, y no lo puede trocear —un
    /// HTTP Datagram de MASQUE, el eco de 1280 bytes de CONNECT-IP—: si cabría en lo que arma quiche, y lo que falta es lo que
    /// ha llegado de un par que no busca su camino, este lado busca por su cuenta (ver <see cref="PedirSitio"/>). Quien sabe
    /// trocear —el UDP de Hysteria 2 y QSOCKS— no lo pide: trocea.
    /// </summary>
    internal unsafe void PedirSitioPara(long largo)
    {
        long cabria;
        lock (_candado)
        {
            if (NativePtr is null) return;
            cabria = (long)NativePtr->DgramMaxWritableLen();
        }
        if (largo <= cabria) PedirSitio();
    }

    /// <summary>
    /// Lo más que mide un datagrama que sale ya, en el próximo paquete: <see cref="MaxDatagramSize"/> menos lo que la MTU de
    /// la sonda del PMTUD tiene de más sobre la confirmada (ver <see cref="DatagramaSaleYa"/>). Pasado el sondeo son lo mismo.
    /// Con quien trocea —el UDP de Hysteria 2 y QSOCKS—, trocear por esto manda ya lo que, troceado por
    /// <see cref="MaxDatagramSize"/>, esperaba un RTT a la sonda en la cabeza de la cola.
    /// </summary>
    internal unsafe long MaxDatagramaQueSaleYa
    {
        get
        {
            int limite = LimiteDeAhora();
            lock (_candado)
            {
                if (NativePtr is null) return 0;
                long cabe = (long)NativePtr->DgramMaxWritableLen();
                if (cabe <= 0 || DisposicionUnix32.LeerPathStats(NativePtr, 0, out quiche_path_stats s) != 0) return cabe;

                // Lo que la MTU de la sonda tiene de más sobre la confirmada —o sobre lo que cabe ahora por el camino, si es
                // menos—, se le quita a lo que cabe.
                long confirmado = limite > 0 ? Math.Min((long)s.pmtu, limite) : (long)s.pmtu;
                long sobra = (long)NativePtr->MaxSendUdpPayloadSize() - confirmado;
                return cabe - Math.Max(0, sobra);
            }
        }
    }

    /// <summary>Devuelve el paquete al arrendador una vez procesado.</summary>
    private static void Devolver(byte[] buf) => ArrayPool<byte>.Shared.Return(buf);

    /// <summary>
    /// Las direcciones del socket, fijadas al construir y sin tocar jamás: quiche las
    /// pide por puntero en cada paquete que procesa la bomba de recepción, y
    /// serializarlas y fijarlas por paquete era basura continua en el camino caliente.
    /// La remota NO cambia ni con el rebinding de NAT: el mundo de quiche se queda
    /// como nació —solo el destino del SendTo se mueve—, así que nunca ve un cambio
    /// de camino. Se sueltan en el Dispose.
    /// </summary>
    private readonly MemoryHandle _sockaddrLocal;
    private readonly int _sockaddrLocalLen;
    private readonly MemoryHandle _sockaddrRemoto;
    private readonly int _sockaddrRemotoLen;

    /// <summary>El ofuscador de esta conexión; null = paquetes en claro.</summary>
    private readonly IPaqueteOfuscador? _ofuscador;

    /// <summary>La cadencia del PING de aplicación; 0 = apagado.</summary>
    private readonly TimeSpan _keepAliveInterval;

    /// <summary>La cadencia del PING sale, tras el saludo, de la inactividad de los dos lados.</summary>
    private readonly bool _keepAliveAuto;

    /// <summary>La inactividad propia, en ms (0 = sin plazo), para la cadencia automática.</summary>
    private readonly long _inactividadLocalMs;

    /// <summary>El PING de aplicación, como cita del planificador compartido; null sin keepalive, o antes del saludo.</summary>
    private Latido? _latido;

    /// <summary>Cuándo salió el último lote, en <see cref="Environment.TickCount64"/>: el latido solo late tras un silencio.</summary>
    private long _ultimaSalida = Environment.TickCount64;

    /// <summary>Cuándo llegó el último paquete que quiche procesó: con <see cref="_ultimaSalida"/>, el silencio del camino.</summary>
    private long _ultimaEntrada = Environment.TickCount64;

    /// <summary>
    /// El destino en los plazos de NAT (<see cref="Cloudflare.QuicheNet.Internal.PlazosDeNat"/>), si esta conexión sale: el latido
    /// pregunta ahí su cadencia, y anota lo que le pasa al camino tras cada silencio. Nulo en las del servidor.
    /// </summary>
    private readonly string? _claveNat;

    /// <summary>
    /// Cuándo salió el latido que aún no tiene respuesta —0, ninguno— y el silencio que lo precedió: si el camino muere sin
    /// contestarle, murió tras ese silencio, y si contesta, lo superó. Ver <see cref="AnotarCaminoMudo"/>.
    /// </summary>
    private long _latidoSinRespuesta, _silencioDelLatido;

    /// <summary>Los PING de aplicación que ha mandado el latido. Para las pruebas.</summary>
    internal int LatidosEnviados;

    /// <summary>
    /// Si el primer datagrama del saludo sale dos veces: ver <see cref="RepetirPrimeroDelSaludo"/>. Para las pruebas: se lee
    /// cuando la conexión empieza a mandar.
    /// </summary>
    internal static bool RepetirSaludo = true;

    /// <summary>Las conexiones que repitieron el primer datagrama de su saludo. Solo para medir.</summary>
    internal static long SaludosRepetidos;

    /// <summary>Lo que llegó del otro extremo y quiche aceptó antes del saludo: el límite de amplificación del servidor.</summary>
    private long _recibidoEnSaludo;

    private readonly byte[] connectionId;
    internal ReadOnlySpan<byte> ConnectionId => connectionId;

    internal unsafe quiche_conn* NativePtr { get; private set; }

    internal Task ConnectionEstablishedTask => establishedTcs.Task;

    /// <summary>
    /// Cuándo se puede empezar a mandar: al establecerse, o antes con 0-RTT. Es lo que espera
    /// quien quiere ahorrarse el RTT del saludo.
    /// </summary>
    internal Task UtilizableTask => utilizableTcs.Task;

    public bool IsServer { get; }

    /// <summary>
    /// ¿Se cerró la conexión? Se lee sin cerrojo: las bombas lo preguntan en cada vuelta y en cada paquete, y con el
    /// cerrojo y la llamada nativa era el 13 % de la CPU de un flujo QUIC a plena carga —y contendía con quien mandaba
    /// datos—. quiche solo pasa a cerrada dentro de <c>recv</c>, <c>send</c>, <c>on_timeout</c> y <c>close</c>, y cada
    /// una la anota al volver (<see cref="AnotarSiCerrada"/>); soltar el motor también la anota.
    /// </summary>
    public bool IsClosed => _cerrada;

    private volatile bool _cerrada;

    /// <summary>Anota si quiche ya da la conexión por cerrada. Con el cerrojo tomado y el motor vivo.</summary>
    private unsafe void AnotarSiCerrada()
    {
        if (!_cerrada && NativePtr->IsClosed())
        {
            _cerrada = true;
            // Vencida por inactividad con un latido sin respuesta: el camino murió tras su silencio.
            if (NativeMethods.quiche_conn_is_timed_out(NativePtr)) AnotarMuerteDelLatido();
            cerradaTcs.TrySetResult();   // sus continuaciones van al pool, fuera del cerrojo
        }
    }

    /// <summary>
    /// Llegó un paquete: si un latido esperaba respuesta, el camino superó su silencio. Con el cerrojo tomado, en cada
    /// paquete: una hora y una comparación.
    /// </summary>
    private void AnotarEntrada()
    {
        _ultimaEntrada = Environment.TickCount64;
        if (_latidoSinRespuesta == 0) return;
        _latidoSinRespuesta = 0;
        Cloudflare.QuicheNet.Internal.PlazosDeNat.Proceso.AnotarSuperado(_claveNat, _silencioDelLatido);
    }

    /// <summary>
    /// El camino enmudeció —lo dice su vigilante, o quiche al vencer la inactividad—: si lo último que salió fue un latido
    /// tras un silencio, y no tuvo respuesta, el NAT del camino olvidó la asociación en ese silencio, y los caminos siguientes
    /// a este destino latirán antes. Se anota una vez.
    /// </summary>
    internal void AnotarCaminoMudo()
    {
        lock (_candado) AnotarMuerteDelLatido();
    }

    /// <summary>Lo de <see cref="AnotarCaminoMudo"/>, con el cerrojo tomado.</summary>
    private void AnotarMuerteDelLatido()
    {
        if (_latidoSinRespuesta == 0) return;
        _latidoSinRespuesta = 0;
        Cloudflare.QuicheNet.Internal.PlazosDeNat.Proceso.AnotarMuerte(_claveNat, _silencioDelLatido);
    }

    /// <summary>
    /// ¿Se cruzó ya el cierre? Desde ahí quiche no manda ni entrega nada —un datagrama se sigue
    /// aceptando en la cola, y se pierde—, pero <see cref="IsClosed"/> no se pone hasta que vence
    /// el drenaje, tres PTO después. Quien reparte trabajo nuevo tiene que mirar esto también.
    /// </summary>
    public bool IsDraining
    {
        get
        {
            unsafe
            {
                lock (_candado)
                {
                    return NativePtr is null || NativePtr->IsDraining();
                }
            }
        }
    }

    /// <summary>
    /// ¿Ya no admite trabajo nuevo? Cerrada o drenando, mirado bajo un solo cerrojo: es lo que
    /// comprueban las portadoras cada vez que abren un flujo o una sesión. Hacen falta las dos,
    /// porque una conexión que vence por inactividad se cierra sin pasar por el drenaje.
    /// </summary>
    internal bool CerradaODrenando
    {
        get
        {
            unsafe
            {
                lock (_candado)
                {
                    return NativePtr is null || NativePtr->IsDraining() || NativePtr->IsClosed();
                }
            }
        }
    }

    /// <summary>
    /// El datagrama más largo que admite quiche ahora: lo que cabe en el paquete más largo que arma —el de la sonda, si su
    /// PMTUD busca—, sin pasar de lo que cabe ahora por el camino (ver <see cref="LimiteDeAhora"/>).
    /// </summary>
    public long MaxDatagramSize
    {
        get
        {
            int limite = LimiteDeAhora();
            unsafe
            {
                lock (_candado)
                {
                    if (NativePtr is null) return 0;
                    long cabe = (long)NativePtr->DgramMaxWritableLen();
                    if (limite <= 0 || cabe <= 0) return cabe;
                    return cabe - Math.Max(0, (long)NativePtr->MaxSendUdpPayloadSize() - limite);
                }
            }
        }
    }

    // ── El techo de los paquetes ────────────────────────────────────────
    // Ver Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic: quiche busca el MTU entre el mínimo de QUIC y lo que le den al crear la
    // conexión; lo que cambie después lo hace cumplir la bomba de envío.

    /// <summary>
    /// Escribe en <paramref name="config"/> el techo de la conexión que va a nacer: hasta dónde busca quiche —el datagrama sin
    /// lo que ponga el ofuscador, y como mucho lo que arma quiche— y si busca. Con el candado de la configuración tomado: quiche
    /// lo copia a la conexión al crearla. Devuelve el techo escrito.
    /// </summary>
    /// <param name="recordado">Lo que anunció este par la última vez como lo más que recibe: ver <see cref="MaxDelParRecordado"/>.</param>
    internal static int PonerTecho(QuicheConfig config, Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic techo, int? recordado)
    {
        int hueco = config.Ofuscador?.Sobrecarga ?? 0;
        int carga = techo.AlNacer;
        if (recordado is int r) carga = Math.Min(carga, r);
        int techoDeQuiche = Math.Clamp(carga - hueco, Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Minimo,
                                       Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.MaxPaquete);
        config.MaxSendUdpPayloadSize = techoDeQuiche;
        config.ShouldDiscoverPathMtu = techo.Busca && techoDeQuiche > Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Minimo;
        return techoDeQuiche;
    }

    /// <summary>
    /// Lo que anunció cada par como lo más que recibe (<c>max_udp_payload_size</c>), por su extremo: la conexión siguiente nace
    /// con eso de techo. quiche no lo mira al buscar el MTU —sus sondas van hasta su techo, y las de más se pierden en el par—;
    /// la bomba de envío no las deja salir (ver <see cref="LimiteDeAhora"/>), pero cada tamaño que no cabe cuesta tres sondas
    /// perdidas: con quic-go, que anuncia 1452, desde el techo del bucle local eran unas cuarenta. Con tope, y caduca.
    /// </summary>
    private static readonly ConcurrentDictionary<EndPoint, (int Max, long HastaMs)> _maxDeLosPares = new();
    private const int MaxParesRecordados = 4096;
    private static readonly long RecuerdoDelParMs = (long)Cloudflare.QuicheNet.Internal.CambioDeRed.Recuerdo.TotalMilliseconds;

    private static int? MaxDelParRecordado(EndPoint remoto)
        => Recordable(remoto) && _maxDeLosPares.TryGetValue(remoto, out var m) && Environment.TickCount64 < m.HastaMs
            ? m.Max : null;

    private static void RecordarMaxDelPar(EndPoint remoto, int max)
    {
        if (!Recordable(remoto)) return;
        if (_maxDeLosPares.Count >= MaxParesRecordados) _maxDeLosPares.Clear();
        _maxDeLosPares[remoto] = (max, Environment.TickCount64 + RecuerdoDelParMs);
    }

    /// <summary>
    /// Un extremo que es un par de verdad: no uno del proceso, ni la dirección fija con que va por el salto un destino que es
    /// un nombre —la de todos los nombres con ese puerto—.
    /// </summary>
    private static bool Recordable(EndPoint remoto)
        => remoto is IPEndPoint ip
           && !ip.Address.Equals(IPAddress.Any) && !ip.Address.Equals(IPAddress.IPv6Any);

    /// <summary>Para las pruebas: lo que se recuerda de cada par, fuera.</summary>
    internal static void OlvidarPares() => _maxDeLosPares.Clear();

    /// <summary>El techo de esta conexión: de dónde sale, y con cuánto nació.</summary>
    private readonly Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic _techo;

    /// <summary>Hasta dónde busca quiche en esta conexión —sin el ofuscador—: de ahí no pasa nunca lo que arma.</summary>
    private readonly int _techoAlNacer;

    /// <summary>Lo que pone el ofuscador delante de cada paquete.</summary>
    private readonly int _hueco;

    /// <summary>Lo más que anuncia el par que recibe, sin el ofuscador; 0 hasta saberlo, tras el saludo.</summary>
    private int _maxDelPar;

    /// <summary>El datagrama más largo que ha llegado del par, sin el ofuscador: el techo de un camino sin DF.</summary>
    private int _mayorRecibido;

    /// <summary>El primero que llegó del par —su Initial, o su primer vuelo—: si nada lo pasa, el par no busca su camino.</summary>
    private int _primerRecibido;

    /// <summary>Los paquetes que quiche armó y no salieron porque no cabían: sondas del PMTUD, casi siempre. Para las pruebas.</summary>
    internal long PaquetesQueNoCaben;

    /// <summary>El techo de esta conexión: para las pruebas.</summary>
    internal Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic Techo => _techo;

    /// <summary>Hasta dónde busca quiche en esta conexión: para las pruebas.</summary>
    internal int TechoAlNacer => _techoAlNacer;

    /// <summary>
    /// Lo más largo que puede salir ahora, en lo que arma quiche —sin el ofuscador—; 0 si no hay más límite que el de quiche,
    /// que no pasa de su techo al nacer. Lo pone el camino si cambia (<see cref="Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Origen.Medido"/>),
    /// lo que llega del par en un socket sin DF, y lo que el par anuncia que recibe: un par que anuncia poco —quic-go, 1452—
    /// tira lo que pase de ahí, y un Xray en Windows deja de leer del todo.
    /// </summary>
    internal int LimiteDeAhora()
    {
        int limite = _techo.De switch
        {
            Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Origen.Medido     => (_techo.Ahora ?? _techo.AlNacer) - _hueco,
            Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Origen.LoQueLlega => LoQueHaProbadoElPar(),
            _                                                     => int.MaxValue,
        };
        int delPar = Volatile.Read(ref _maxDelPar);
        if (delPar > 0 && delPar < limite) limite = delPar;
        if (limite < Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Minimo) limite = Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Minimo;
        return limite >= _techoAlNacer ? 0 : limite;
    }

    /// <summary>
    /// El límite de un socket sin DF (<see cref="Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Origen.LoQueLlega"/>): lo más largo que ha
    /// llegado del par, que mide su camino con DF. Por encima, este lado no manda nada mientras no haga falta: una sonda suya
    /// sin DF la puede trocear un router de IPv4, y quiche la daría por buena. Solo si hay que mandar algo que no cabe en lo que
    /// ha llegado, y no hay otro modo —el eco IPv6 de 1280 bytes con que la pasarela de CONNECT-IP comprueba el túnel (RFC 9484
    /// §7.2), que no se trocea—, y el par no ha buscado su camino, busca este lado por su cuenta (<see cref="_buscaSolo"/>): es
    /// eso o perderlo. Con un par que busca, manda lo que él ha probado.
    /// <para>
    /// Buscaba solo también al vencer un plazo, tres PTO sin que llegara del par nada mayor que su primer datagrama. Un par que
    /// busca tarda más que eso en enseñar su primera sonda buena —quic-go, el de sing-box, sondea cinco RTT después de
    /// confirmar el saludo; el cliente de la librería, desde el techo de una Ethernet por un camino de 1300, pierde dos tamaños
    /// antes de acertar—, y con más de unos 30 ms de vuelta este lado confirmaba un tamaño que solo pasaba troceado, y lo
    /// mandaba troceado hasta que llegaba esa sonda. Esperando sin fin y sin más, en cambio, a un par que no busca no se le
    /// mandaba nunca más de 1200, y la pasarela de CONNECT-IP le cortaba el túnel.
    /// </para>
    /// </summary>
    private int LoQueHaProbadoElPar()
    {
        int recibido = Volatile.Read(ref _mayorRecibido);
        if (recibido > Volatile.Read(ref _primerRecibido)) return recibido;
        return _buscaSolo ? int.MaxValue : recibido;
    }

    /// <summary>¿Ha llegado del par algo mayor que su primer datagrama? Ver <see cref="LoQueHaProbadoElPar"/>.</summary>
    private bool ElParHaBuscado() => Volatile.Read(ref _mayorRecibido) > Volatile.Read(ref _primerRecibido);

    /// <summary>
    /// Hubo que mandar algo que no cabía en lo que ha llegado de un par que no busca: desde ahí busca este lado por su cuenta.
    /// Ver <see cref="LoQueHaProbadoElPar"/> y <see cref="PedirSitio"/>.
    /// </summary>
    private volatile bool _buscaSolo;

    /// <summary>
    /// Alguien tiene que mandar un datagrama que no cabe en lo que deja ahora un socket sin DF —lo que ha llegado del par—, y
    /// cabría en lo que arma quiche: si el par no ha buscado su camino, este lado busca por su cuenta, y la bomba de envío suelta
    /// la sonda que retenía. Ver <see cref="LoQueHaProbadoElPar"/>.
    /// </summary>
    private void PedirSitio()
    {
        if (_techo.De != Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Origen.LoQueLlega || _buscaSolo || ElParHaBuscado()) return;
        _buscaSolo = true;
        SeñalizarEnvio();
    }

    /// <summary>
    /// ¿Espera todavía la sonda de <paramref name="sonda"/> bytes, sin que quiche la pueda armar? Solo por un socket sin DF
    /// (<see cref="Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Origen.LoQueLlega"/>), mientras pase de lo que ha llegado del par y no de
    /// lo que el par anuncia que recibe —lo que no cabe de todos modos sale y se pierde aquí, y quiche busca por debajo—:
    /// <list type="bullet">
    /// <item>Con un par que aún no ha buscado su camino, sin plazo: hasta que busque, o hasta que haga falta más
    /// (<see cref="PedirSitio"/>).</item>
    /// <item>Con un par que busca, mientras lo que llega de él siga subiendo: ver <see cref="RetenerSonda"/>.</item>
    /// </list>
    /// </summary>
    private bool SondaEnEspera(long sonda, long ptoNs)
    {
        if (_techo.De != Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Origen.LoQueLlega || _buscaSolo) return false;
        int delPar = Volatile.Read(ref _maxDelPar);
        if (delPar > 0 && sonda > delPar) return false;
        if (!ElParHaBuscado()) return true;
        return RetenerSonda(sonda, Volatile.Read(ref _mayorRecibido), ptoNs);
    }

    /// <summary>Lo más largo que puede armar quiche ahora sin pasarse del camino: su techo, o lo que cabe si es menos.</summary>
    private int PaqueteQueCabeAhora()
    {
        int limite = LimiteDeAhora();
        return limite > 0 ? limite : _techoAlNacer;
    }

    /// <summary>
    /// Lo que anuncia el par que recibe, una vez, con el saludo hecho: desde ahí la bomba de envío no le manda nada mayor, y
    /// se recuerda para la próxima conexión a él.
    /// </summary>
    private void LeerMaxDelPar()
    {
        ulong anunciado = ParametrosDelPar().peer_max_udp_payload_size;
        if (anunciado == 0) return;
        int max = (int)Math.Min(anunciado, (ulong)QuicheLibrary.MaxDatagramaUdp);
        Volatile.Write(ref _maxDelPar, Math.Max(Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Minimo, max - _hueco));
        if (!IsServer) RecordarMaxDelPar(remoteEndPoint, max);
    }

    /// <summary>
    /// Lo que la conexión sabe de su PMTUD: lo más que arma quiche ahora —la sonda, si está buscando—, lo confirmado y lo que
    /// tarda en darse por perdido lo que sale (un PTO, en ns). Con el cerrojo; falso con el motor ya suelto.
    /// </summary>
    private unsafe bool EstadoDelPmtud(out long maxDeQuiche, out long confirmado, out long ptoNs)
    {
        maxDeQuiche = confirmado = ptoNs = 0;
        lock (_candado)
        {
            if (NativePtr is null) return false;
            maxDeQuiche = (long)NativePtr->MaxSendUdpPayloadSize();
            if (DisposicionUnix32.LeerPathStats(NativePtr, 0, out quiche_path_stats s) != 0) return false;
            confirmado = (long)s.pmtu;
            ptoNs = (long)(new MuestraDeCamino(0, 0, 0, 0, s.rtt, s.rttvar, s.pmtu).PtoMs * 1_000_000);
            return true;
        }
    }

    /// <summary>
    /// La sonda del PMTUD que espera a que el par pruebe su tamaño —ver <see cref="RetenerSonda"/>—, lo que había llegado del
    /// par al empezar a esperar, y desde cuándo, en marcas de Stopwatch. Solo los toca la bomba de envío.
    /// </summary>
    private long _sondaRetenida, _llegadoAlRetener, _retenidaDesde;

    /// <summary>Despierta a la bomba de envío cuando la sonda retenida deja de esperar.</summary>
    private readonly CitaPrecisa _citaDeLaSonda;

    /// <summary>
    /// Lo que espera una sonda por encima de lo que ha llegado de un par que busca, en PTO, desde lo último que subió: ver
    /// <see cref="RetenerSonda"/>. Con tres, la búsqueda de este lado va a la mitad de paso que la del par —cada tamaño que no
    /// cabe le cuesta tres PTO—, y lo que el par confirma llega antes de que este lado dé por perdido ese tamaño.
    /// </summary>
    private const int PtosDeEspera = 3;

    /// <summary>
    /// ¿Espera todavía la sonda de <paramref name="sonda"/> bytes a que un par que busca pruebe ese tamaño? Por un socket sin
    /// DF el techo es lo que llega del par (<see cref="Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Origen.LoQueLlega"/>), que mide su
    /// camino a la vez que este lado mide el suyo. Una sonda por encima que no saliera se daría por perdida, y quiche no vuelve
    /// a probar un tamaño perdido: con el par aún buscando, este lado se quedaba para siempre en lo que el par había confirmado
    /// en ese momento —1258 en vez de 1300, por un camino de 1300—. Así que la sonda espera a que llegue algo de su tamaño
    /// mientras lo que llega siga subiendo: <see cref="PtosDeEspera"/> PTO desde la última vez que subió
    /// (<paramref name="llegado"/>). Pasado eso, sale, se pierde aquí, y quiche busca por debajo de ella.
    /// </summary>
    private bool RetenerSonda(long sonda, long llegado, long ptoNs)
    {
        long ahora = System.Diagnostics.Stopwatch.GetTimestamp();
        if (sonda != _sondaRetenida || llegado != _llegadoAlRetener)
        {
            _sondaRetenida    = sonda;
            _llegadoAlRetener = llegado;
            _retenidaDesde    = ahora;
        }

        long esperaNs = PtosDeEspera * Math.Max(ptoNs, 1_000_000);
        long pasadoNs = (long)((ahora - _retenidaDesde) * (1e9 / System.Diagnostics.Stopwatch.Frequency));
        if (pasadoNs >= esperaNs) return false;

        // Sin tráfico nada despertaría a la bomba para soltarla: su cita.
        DespertadorPreciso.Adelantar(_citaDeLaSonda, DespertadorPreciso.AhoraNs() + (esperaNs - pasadoNs));
        return true;
    }

    /// <summary>
    /// Saca de la cola de DATAGRAM de quiche lo que no cabe en <paramref name="capacidad"/>: con el camino estrechado, quiche
    /// los dejaría en la cabeza de su cola —los admitió con su PMTU, y no caben en lo que se le deja armar— y con ellos
    /// esperarían todos los de detrás. Un datagrama no se reenvía: el que no cabe se pierde, como en el camino.
    /// <para>
    /// Una vez por capacidad: desde ahí <see cref="EnviarDatagrama(ReadOnlySpan{byte}, int)"/> ya no admite nada que no
    /// quepa. Se purgaba en cada vuelta de la bomba mientras lo confirmado no cupiera —y eso no se acaba, porque quiche no
    /// baja su PMTU—, recorriendo la cola entera con una llamada de vuelta por datagrama.
    /// </para>
    /// </summary>
    private unsafe void PurgarLoQueNoCabe(long capacidad)
    {
        if (capacidad == _purgadoCon) return;
        _purgadoCon = capacidad;
        lock (_candado)
        {
            if (NativePtr is null || NativePtr->DgramSendQueueLen() == 0) return;
            Purgas++;
            _capacidadDePurga = capacidad;
            NativePtr->DgramPurgeOutgoing(
                (delegate* unmanaged[Cdecl]<byte*, nuint, bool>)(delegate* unmanaged[Cdecl]<byte*, nuint, byte>)&PurgarGrande);
            _capacidadDePurga = 0;
        }
    }

    /// <summary>
    /// La capacidad con que se purgó la última vez, o 0 si lo confirmado cabe: ver <see cref="PurgarLoQueNoCabe"/>. Solo la toca
    /// la bomba de envío.
    /// </summary>
    private long _purgadoCon;

    /// <summary>Las veces que se recorrió la cola de DATAGRAM para sacar lo que no cabe: para las pruebas.</summary>
    internal long Purgas;

    /// <summary>La capacidad de la purga en curso: ver <see cref="PurgarLoQueNoCabe"/>. Del hilo, como <see cref="_porPurgar"/>.</summary>
    [ThreadStatic] private static long _capacidadDePurga;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte PurgarGrande(byte* datos, nuint largo)
        => _capacidadDePurga > 0 && (long)largo > _capacidadDePurga ? (byte)1 : (byte)0;

    private unsafe QuicheConnection(quiche_conn* nativePtr, QuicheConfig config, ICanalDatagramas canal, EndPoint remoteEndPoint, ReadOnlyMemory<byte> initialData, ReadOnlyMemory<byte> connectionId,
        MemoryHandle local, int localLen, MemoryHandle remote, int remoteLen,
        bool esH3, Action<byte[]>? alRecibirSesion = null,
        Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic? techo = null, int techoAlNacer = 0)
    {
        // quiche_connect/quiche_accept devuelven NULL cuando el saludo no puede ni
        // nacer —por ejemplo un SNI vacío o una config inválida—. Sin esta guarda el
        // primer acceso nativo sería un AccessViolation que mata el PROCESO entero,
        // no solo esta conexión.
        if (nativePtr is null)
            throw new QuicheException(QuicheError.QUICHE_ERR_INVALID_STATE,
                "quiche no pudo crear la conexión");

        NativePtr = nativePtr;

        IsServer = NativePtr->IsServer();

        // Al vencer, la bomba de recepción despierta y arma la siguiente: ver ArmarTemporizador.
        _citaDelTemporizador = new CitaPrecisa(SeñalizarRecv);
        _citaDelAck = new CitaDelAck(this);
        _citaDeLaSonda = new CitaPrecisa(SeñalizarEnvio);

        // El HTTP/3 vive encima de esta conexión y sondea sus flujos por dentro del
        // motor: las bombas de flujos crudos se apagan o le robarían los bytes —un
        // flujo legible iría a StreamRecv y a un canal que nadie lee—. El h3 trae su
        // propia bomba, despertada por el mismo aviso.
        EsH3 = esH3;

        // El ofuscador es el de la configuración, el mismo para todas sus conexiones: no
        // guarda estado entre paquetes.
        _ofuscador = config.Ofuscador;
        _hueco = _ofuscador?.Sobrecarga ?? 0;
        _techo = techo ?? Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.SinBuscar;
        _techoAlNacer = techoAlNacer > 0 ? techoAlNacer : Cloudflare.QuicheNet.Internal.Quic.TechoDeQuic.Minimo;
        _guardia = new GuardiaDeBombas(this);
        _keepAliveInterval = config.KeepAliveInterval;
        _keepAliveAuto = config.KeepAliveAuto;
        _inactividadLocalMs = config.MaxIdleTimeout;

        this.config = config;

        this.canal = canal;
        this.remoteEndPoint = remoteEndPoint;
        if (!IsServer) _claveNat = Cloudflare.QuicheNet.Internal.PlazosDeNat.ClaveDe(remoteEndPoint);

        _sockaddrLocal = local;
        _sockaddrLocalLen = localLen;
        _sockaddrRemoto = remote;
        _sockaddrRemotoLen = remoteLen;

        this.connectionId = new byte[QuicheLibrary.MAX_CONN_ID_LEN];
        connectionId.CopyTo(this.connectionId);

        recvQueue = new();

        streamMap = new();
        streamChannel = Channel.CreateUnbounded<QuicheStream>();

        if (config.DatagramOptions.Enabled)
        {
            // Los datagramas salen por EnviarDatagrama directamente a quiche; solo la
            // recepción necesita cola propia. Lo que se tira con la cola llena vuelve al
            // almacén.
            datagramasRecibidos = new Cloudflare.QuicheNet.Internal.ColaAcotada<DatagramaQuic>(
                Math.Max(1, config.DatagramOptions.ReceiveQueueLength),
                Cloudflare.QuicheNet.Internal.QueuePolicy.DescartarViejos,
                descartar: static tirado => tirado.Devolver());
        }

        // Las continuaciones del saludo NO corren en el hilo de la bomba de recepción:
        // es ella quien completa el TCS, y una continuación síncrona podría llegar hasta
        // el Dispose de la conexión —que hace WaitAll de las bombas, incluida la de este
        // mismo hilo— y esperarse a sí misma para siempre.
        establishedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        utilizableTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _earlyData = config.IsEarlyDataEnabled;
        _alRecibirSesion = alRecibirSesion;

        cts = new();

        recvTask = Task.Run(() => ReceiveAsync(cts.Token));
        sendTask = Task.Run(() => SendAsync(cts.Token));

        if (!esH3)
        {
            recvStreamTask = Task.Run(() => ReceiveStreamAsync(cts.Token));
            sendStreamTask = Task.Run(() => SendStreamAsync(cts.Token));
        }

        if (_keepAliveAuto || _keepAliveInterval > TimeSpan.Zero) ArrancarLatido();

        if (!IsServer) ApuntarSalida();

        if (initialData.IsEmpty)
        {
            listenTask = Task.Run(() => ListenAsync(cts.Token));
        }
        else
        {
            // El primer datagrama llegó con el accept: se copia al arrendado para que
            // la cola siga siendo dueña de sus búferes. Y se avisa a la bomba, que ya arrancó: si había vaciado la cola y
            // se había dormido, el Initial del cliente esperaba a su plazo —hasta ~31 ms de saludo en cada conexión
            // entrante, con el sondeo de 25 ms— o a que el cliente lo repitiera.
            byte[] inicial = ArrayPool<byte>.Shared.Rent(initialData.Length);
            initialData.CopyTo(inicial);
            if (Encolar(inicial, initialData.Length, origen: null)) SeñalizarRecv();
        }
    }

    private int flujosCompletados;

    /// <summary>
    /// La conexión se acabó: los lectores de sus flujos ven EOF en vez de colgarse.
    /// Idempotente; lo llama el primer bucle que se entera del cierre.
    /// </summary>
    private void CompletarFlujos()
    {
        if (Interlocked.Exchange(ref flujosCompletados, 1) == 0)
        {
            foreach (var (_, stream) in streamMap)
            {
                stream.AbortRecv();
            }

            // Quien espera datagramas se entera también; los que ya llegaron se leen igual.
            datagramasRecibidos?.Completar();
            Interlocked.Exchange(ref _huecoDgram, null)?.TrySetResult();
        }
    }

    /// <summary>Lo que toca salir dentro de 2 ms sale ya, en la misma ráfaga que lo anterior.</summary>
    private const long HolguraPacingNs = 2_000_000;

    /// <summary>Un «at» a más de un segundo no es pacing, es un reloj que no casa: se ignora.</summary>
    private const long TopePacingNs = 1_000_000_000;

    /// <summary>
    /// Cuánto antes de su hora se despierta al paquete. El pacer de quiche conserva el ritmo si
    /// el paquete sale antes de su «at», y lo pierde si sale tarde.
    /// </summary>
    private const long AdelantoPacingNs = 1_000_000;

    /// <summary>Cuántas veces esperó la bomba de envío por el pacing. Solo para medir.</summary>
    internal long EsperasDePacing;

    /// <summary>
    /// Cuánto falta para <paramref name="at"/>, en nanosegundos; 0 o menos si ya toca.
    /// <para>
    /// quiche da el «at» en el reloj monotónico, el mismo de <c>Stopwatch</c>:
    /// QueryPerformanceCounter en Windows (parche windows-send-at) y CLOCK_MONOTONIC en
    /// Linux. En macOS llega a 0, y entonces no hay pacing.
    /// </para>
    /// </summary>
    internal static long NanosHasta(in timespec at)
    {
        long segundos = at.tv_sec;
        long nanos = at.tv_nsec.Value;   // CLong: 32 bits en Windows, 64 en Linux
        if (segundos == 0 && nanos == 0) return 0;

        long falta = segundos * 1_000_000_000 + nanos - DespertadorPreciso.AhoraNs();
        return falta > TopePacingNs ? 0 : falta;
    }

    /// <summary>Lo que cabe en un lote de paquetes que salen con una sola llamada al sistema: ver <see cref="CanalSocket.TopeDeLote"/>.</summary>
    private const int TopeDeLote = CanalSocket.TopeDeLote;

    /// <summary>Cuántos lotes de más de un paquete salieron. Solo para medir.</summary>
    internal long LotesEnviados;


    /// <summary>Las vueltas de las bombas de envío, de flujos y de recepción, entre todas. Solo para medir.</summary>
    internal long DespertaresDeBombas;

    /// <summary>Los flujos que ha recorrido la bomba de flujos, sumando cada vuelta. Solo para medir.</summary>
    internal long FlujosRecorridos;

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        // Los paquetes se escriben seguidos en el lote: los que miden lo mismo y salen ya van juntos en una sola
        // llamada al sistema, que los parte en datagramas (USO en Windows). Era un sendto por paquete, el 25 % de la
        // CPU de un flujo QUIC a plena carga, y la bomba de envío de cada conexión no daba más de sí.
        // Hasta el saludo, un lote de un solo paquete: el saludo sale de uno en uno de todos modos, y una conexión a medio
        // saludo no reserva el lote entero —casi 70 KB, por cada Initial de un chorro—. Ver QuicheListener.TopeAMedioSaludo.
        // El sitio de un paquete es el más largo que puede armar quiche en esta conexión: su techo al nacer, con el ofuscador.
        int unPaquete = _techoAlNacer + _hueco;
        byte[] lote = new byte[2 * unPaquete];
        int topeDelLote = unPaquete;
        int usado = 0, segmento = 0, paquetes = 0;
        bool cerrado = false;   // el último paquete fue más corto: detrás no puede ir otro
        var seguro = new EsperaDeBomba();
        bool avisada = true, salioAlgo = false;   // cómo despertó la vuelta, y si mandó algo: ver EsperaDeBomba
        long ultimoEnvio = 0;                     // cuándo salió lo anterior: ver TemporizadorTrasEnviar

        // El primer datagrama del saludo, que sale otra vez al acabar su tanda, y lo que salió hasta entonces: ver
        // RepetirPrimeroDelSaludo.
        byte[]? primero = null;
        int largoPrimero = 0;
        long enSaludo = 0;
        bool repetido = !RepetirSaludo;

        void Vaciar()
        {
            if (usado == 0) return;
            try
            {
                if (!repetido)
                {
                    // Antes del saludo el lote lleva un solo paquete: el primero que sale es el Initial.
                    if (primero is null)
                    {
                        primero = ArrayPool<byte>.Shared.Rent(usado);
                        lote.AsSpan(0, usado).CopyTo(primero);
                        largoPrimero = usado;
                    }
                    enSaludo += usado;
                }
                if (paquetes == 1) canal.Enviar(lote.AsSpan(0, usado), remoteEndPoint);
                else
                {
                    canal.EnviarLote(lote.AsSpan(0, usado), segmento, remoteEndPoint);
                    LotesEnviados++;
                }
                if (usado > 64 * paquetes) Volatile.Write(ref _ultimoEnvioDeDatos, System.Diagnostics.Stopwatch.GetTimestamp());
                salioAlgo = true;
                Volatile.Write(ref _ultimaSalida, Environment.TickCount64);
                AvisarDeActividad();
            }
            catch (SocketException)
            {
                // Un ICMP suelto del par que se fue no mata la bomba de envío: quiche retransmite por PTO lo que no
                // salió. Si el camino está muerto de verdad, el siguiente envío falla igual y el bucle acaba por la
                // cancelación o el cierre.
            }
            usado = 0; segmento = 0; paquetes = 0; cerrado = false;
        }

        // El ofuscador pone su cabecera delante del paquete: quiche escribe detrás de ese hueco.
        int hueco = _ofuscador?.Sobrecarga ?? 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsClosed)
            {
                AnotarCerrada();
                CompletarFlujos();
                throw new QuicheException(QuicheError.QUICHE_ERR_DONE, "Connection was closed.");
            }

            // El aviso se borra antes de generar: lo que llegue desde aquí vuelve a marcarlo. Y lo que genere lleva el ACK de lo
            // procesado hasta ahora —quiche lo pone en lo primero que manda—: el aplazado, si lo había, ya no espera. Ver
            // _ackAplazado.
            Volatile.Write(ref _avisoEnvio, 0);
            if (Volatile.Read(ref _ackAplazado) != 0) Interlocked.Exchange(ref _ackAplazado, 0);
            Volatile.Write(ref _paquetesSinAck, 0);
            Interlocked.Increment(ref DespertaresDeBombas);
            salioAlgo = false;

            // Con el saludo hecho —o el 0-RTT—, el lote entero.
            if (topeDelLote < TopeDeLote && usado == 0
                && (establishedTcs.Task.IsCompletedSuccessfully || utilizableTcs.Task.IsCompletedSuccessfully))
            {
                lote = new byte[TopeDeLote + unPaquete];
                topeDelLote = Math.Max(TopeDeLote, unPaquete);
            }

            // Lo que cabe ahora por el camino, si es menos que el techo con que nació la conexión: ver LimiteDeAhora. Lo que
            // quiche arme por encima no sale —casi siempre una sonda de su PMTUD, que para él se pierde, y sigue buscando por
            // debajo—. Y si lo que ya confirmó tampoco cabe —el camino se estrechó—, se le deja armar solo hasta ahí, y lo que
            // tenga en su cola de DATAGRAM que ya no quepa se tira: si no, esperaría en la cabeza con todo lo de detrás.
            // Por un socket sin DF, una sonda por encima de lo que ha llegado del par espera, sin salir, a que el par pruebe ese
            // tamaño: ver SondaEnEspera.
            if (Volatile.Read(ref _maxDelPar) == 0 && establishedTcs.Task.IsCompletedSuccessfully) LeerMaxDelPar();
            int limite = LimiteDeAhora(), largoMax = int.MaxValue;
            if (limite > 0 && EstadoDelPmtud(out long deQuiche, out long confirmado, out long ptoNs))
            {
                if (confirmado > limite)
                {
                    largoMax = limite;
                    PurgarLoQueNoCabe(MaxDatagramSize);
                }
                else
                {
                    _purgadoCon = 0;
                    if (deQuiche > limite && SondaEnEspera(deQuiche, ptoNs)) largoMax = limite;
                }
            }
            else _purgadoCon = 0;

            try
            {
                // Drenar de una sentada: quiche dice «llámame hasta que devuelva
                // DONE». Antes cada paquete pagaba un tic de sondeo; ahora un lote
                // sale entero en cuanto hay trabajo.
                while (true)
                {
                    // Sitio para un paquete entero: con menos, quiche lo escribiría más corto y cerraría el lote. Y detrás
                    // de uno más corto no va ninguno.
                    if (cerrado || usado + unPaquete > topeDelLote) Vaciar();

                    long resultOrError = GenerarPaquete(lote, usado + hueco, largoMax, out timespec momento);

                    if (resultOrError == (long)QuicheError.QUICHE_ERR_DONE) break;

                    QuicheException.ThrowIfError((QuicheError)resultOrError);

                    // No cabe por el camino: no sale, y quiche lo da por perdido, como si lo hubiera tirado el camino.
                    if (limite > 0 && resultOrError > limite)
                    {
                        Interlocked.Increment(ref PaquetesQueNoCaben);
                        continue;
                    }

                    // El ofuscador envuelve el paquete YA cifrado por quiche, justo antes
                    // del socket; el par lo desenvuelve con la misma pieza.
                    int largo = hueco + (int)resultOrError;
                    _ofuscador?.Codificar(lote.AsSpan(usado, largo));

                    // El pacing de quiche: «at» es cuándo debe salir el paquete. Lo que toca
                    // dentro de la holgura sale ya, en la misma ráfaga; lo demás espera su
                    // momento con el despertador preciso. Antes se leían solo los 32 bits bajos
                    // de los segundos y nunca se esperaba: Brutal a 250 KB/s mandaba a más de
                    // 11 MB/s.
                    long faltaNs = NanosHasta(momento);
                    bool espera = faltaNs > HolguraPacingNs && DespertadorPreciso.Disponible;

                    // Lo de delante sale antes que este si este tiene que esperar —a lo de delante le tocaba ya— o si este
                    // es más largo, que no cabe en sus segmentos. Este pasa entonces a abrir el lote.
                    if (paquetes > 0 && (espera || largo > segmento))
                    {
                        int inicio = usado;
                        Vaciar();
                        lote.AsSpan(inicio, largo).CopyTo(lote);
                    }

                    if (espera)
                    {
                        EsperasDePacing++;
                        // Una tarea por espera, a propósito: con una espera reutilizable (EsperaReutilizable), la subida de
                        // socks5+quic por «lejos» bajaba de 8,5 a 7,1 MB/s (2 de 13 rondas), y con la tarea queda a la par.
                        try { await DespertadorPreciso.EsperarAsync(faltaNs - AdelantoPacingNs, cancellationToken); }
                        catch (OperationCanceledException)
                        {
                            // Cortada con el paquete ya hecho: quiche lo da por enviado, y tirarlo es perderlo —lo de un cierre,
                            // sobre todo—. Sale ya, solo: lo de delante salió al abrir el lote. Ver Dispose.
                            usado = largo; segmento = largo; paquetes = 1;
                            Vaciar();
                            throw;
                        }
                    }

                    if (paquetes == 0) segmento = largo;
                    else if (largo < segmento) cerrado = true;
                    usado += largo;
                    paquetes++;
                }

                Vaciar();

                // La primera tanda que salió entera: su primer datagrama, otra vez.
                if (primero is not null)
                {
                    RepetirPrimeroDelSaludo(primero, largoPrimero, enSaludo);
                    ArrayPool<byte>.Shared.Return(primero);
                    primero  = null;
                    repetido = true;
                }
            }
            catch (QuicheException ex)
            when (ex.ErrorCode == QuicheError.QUICHE_ERR_DONE)
            {
                if (IsClosed) { throw; }
                Vaciar();
            }
            catch (QuicheException ex)
            {
                // Si el saludo lo cortó una alerta TLS de este lado, quien lo espera se entera con ella, antes que con esto.
                if (ex.ErrorCode == QuicheError.QUICHE_ERR_TLS_FAIL) FallarSaludoPorTls();
                MarcarMuerta();
                establishedTcs.TrySetException(ex);
                throw;
            }
            catch (OperationCanceledException)
            {
                establishedTcs.TrySetCanceled(cancellationToken);
                throw;
            }

            // Lo que salió armó su PTO en quiche, y la cita del temporizador solo la armaba la bomba de recepción al despertar:
            // ver TemporizadorTrasEnviar.
            if (salioAlgo) TemporizadorTrasEnviar(ref ultimoEnvio);

            // Con una sesión reanudada, el cliente entra en 0-RTT al escribir su ClientHello:
            // quien espera para mandar ya no tiene que aguardar al saludo entero.
            if (_earlyData && !IsServer && !utilizableTcs.Task.IsCompleted && EnEarlyData)
            {
                if (utilizableTcs.TrySetResult())
                    Interlocked.Increment(ref QuicheDiagnostics.EarlyDataCount);

                // Lo que se encoló antes ya puede salir: la bomba de flujos no sondea.
                SeñalizarFlujos();
            }

            // quiche ya no tiene nada que mandar ahora mismo: quien esperaba sitio para un
            // datagrama lo vuelve a intentar.
            Interlocked.Exchange(ref _huecoDgram, null)?.TrySetResult();

            // Nada que mandar: a dormir hasta que un flujo encole algo —aviso al instante— o avise la bomba de recepción,
            // tras procesar paquetes —los ACK abren la ventana de congestión— o al vencer el temporizador —las
            // retransmisiones—. El plazo es de seguridad, y crece en reposo: ver EsperaDeBomba.
            seguro.Tras(avisada || salioAlgo);
            avisada = await Dormir(bombaEnvio, seguro.Actual, cancellationToken);
        }
    }

    /// <summary>
    /// Manda otra vez el primer datagrama del saludo —el Initial del cliente, o el primero del servidor, con su
    /// ServerHello—, al acabar la tanda en que salió.
    /// <para>
    /// Hasta tener un RTT medido, quiche supone 333 ms, y su PTO es de ~1 s: si se pierde el Initial del cliente, o el
    /// primer datagrama del servidor —el que le da al cliente su primera medida—, abrir cuesta un segundo más, y dos o tres si
    /// se pierden dos. Con dos copias, lo que pierde el saludo es perder las dos: por «móvil», con un 1 % de pérdida por
    /// sentido, de ~2 de cada 100 aperturas en frío a ~2 de cada 10 000. Lo que cuesta es un datagrama por conexión nueva, y el
    /// otro extremo tira la copia como un paquete repetido (RFC 9000 §12.3), sea quiche, quic-go o cualquiera. Un RTT inicial
    /// más corto (<see cref="QuicheConfig.FijarRttInicial"/>) solo acortaría la espera, y la copia no espera nada: medido,
    /// empezar con el RTT ya aprendido hacia el destino no mejoró abrir en frío.
    /// </para>
    /// <para>
    /// El servidor no pasa de su límite de amplificación (RFC 9000 §8.1): antes de validar la dirección del cliente, lo
    /// mandado no puede pasar de tres veces lo recibido. quiche lo cumple con lo suyo; la copia sale solo si cabe con ello
    /// dentro. Con un certificado corto cabe: el Initial del cliente trae 1200 bytes o más, y el primer vuelo va por los
    /// 1500-2000.
    /// </para>
    /// </summary>
    private void RepetirPrimeroDelSaludo(byte[] primero, int largo, long enviado)
    {
        if (IsServer && enviado + largo > 3 * Interlocked.Read(ref _recibidoEnSaludo)) return;
        try
        {
            canal.Enviar(primero.AsSpan(0, largo), remoteEndPoint);
            Interlocked.Increment(ref SaludosRepetidos);
        }
        catch (SocketException) { /* como cualquier envío que falla: lo recupera el PTO */ }
    }

    private async Task SendStreamAsync(CancellationToken cancellationToken)
    {
        var seguro = new EsperaDeBomba();
        bool avisada = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsClosed)
            {
                CompletarFlujos();
                throw new QuicheException(QuicheError.QUICHE_ERR_DONE, "Connection was closed.");
            }

            // El aviso se borra antes de recorrer los flujos: lo que se encole desde aquí vuelve a marcarlo.
            Volatile.Write(ref _avisoFlujos, 0);
            Interlocked.Increment(ref DespertaresDeBombas);
            bool huboProgreso = false;

            try
            {
                bool establecido;
                unsafe
                {
                    lock (_candado)
                    {
                        establecido = NativePtr is not null
                                      && (NativePtr->IsEstablished() || NativePtr->IsInEarlyData());
                    }
                }

                if (establecido)
                {
                    // Solo los flujos con algo pendiente. Cada uno drena SU cola en orden,
                    // hasta que nadie progrese: lo que el control de flujo no admite se
                    // queda en el flujo y se reintenta en la pasada siguiente.
                    while (_flujosPendientes.TryDequeue(out var pendiente)) _flujosEnCurso.Add(pendiente);
                    if (_flujosEnCurso.Count > 0) Interlocked.Add(ref FlujosRecorridos, _flujosEnCurso.Count);
                    try
                    {
                        // Por turnos: con varios esperando ventana, cada uno manda como mucho TurnoDeEnvio por vuelta, y se dan
                        // vueltas mientras alguno avance. Cada uno vaciaba lo suyo antes de pasar al siguiente, así que el primero
                        // de la lista se llevaba entera la ventana que abrían los ACK. Uno solo, lo que quepa.
                        int turno = _flujosEnCurso.Count > 1 ? TurnoDeEnvio : int.MaxValue;
                        bool progreso = _flujosEnCurso.Count > 0;
                        while (progreso)
                        {
                            progreso = false;
                            foreach (var stream in _flujosEnCurso)
                            {
                                if (!stream.IntentarEnviar(turno)) continue;
                                stream.TuvoTurno = true;
                                progreso = true;
                            }
                            huboProgreso |= progreso;
                        }
                    }
                    finally
                    {
                        // Cada uno sale de la lista, y vuelve sin despertar a nadie si aún le queda algo: primero los que se
                        // quedaron sin mandar nada, que la ventana que abran los próximos ACK es para ellos. Volvían en el
                        // mismo orden, y el primero de la lista era siempre el mismo: con las bajadas de otras sesiones por
                        // delante, la respuesta de un flujo nuevo esperaba segundos por un enlace lleno. La marca se borra con
                        // barrera antes de mirar: lo que un escritor encole desde aquí lo anota él, y lo de antes se ve.
                        foreach (var stream in _flujosEnCurso)
                            if (!stream.TuvoTurno) Reanotar(stream);
                        foreach (var stream in _flujosEnCurso)
                            if (stream.TuvoTurno)
                            {
                                stream.TuvoTurno = false;
                                Reanotar(stream);
                            }
                        _flujosEnCurso.Clear();
                    }
                }
            }
            catch (QuicheException ex)
            when (ex.ErrorCode == QuicheError.QUICHE_ERR_DONE)
            {
                if (IsClosed) { throw; }
            }
            catch (QuicheException ex)
            {
                MarcarMuerta();
                establishedTcs.TrySetException(ex);
                throw;
            }
            catch (OperationCanceledException)
            {
                establishedTcs.TrySetCanceled(cancellationToken);
                throw;
            }

            // A dormir hasta que un flujo encole —aviso al instante— o lleguen ventanas nuevas para lo que chocó con ellas,
            // que avisa la bomba de recepción: ver _flujosBloqueados. El plazo es de seguridad, y crece en reposo: ver
            // EsperaDeBomba.
            seguro.Tras(avisada || huboProgreso);
            avisada = await Dormir(bombaFlujos, seguro.Actual, cancellationToken);
        }
    }

    private async Task ReceiveAsync(CancellationToken cancellationToken)
    {
        var seguro = new EsperaDeBomba();
        bool avisada = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsClosed)
            {
                AnotarCerrada();
                CompletarFlujos();
                throw new QuicheException(QuicheError.QUICHE_ERR_DONE, "Connection was closed.");
            }

            // El aviso se borra antes de vaciar la cola: un paquete que entre desde aquí vuelve a marcarlo.
            Volatile.Write(ref _avisoRecv, 0);
            Interlocked.Increment(ref DespertaresDeBombas);
            bool trabajo = false;

            try
            {
                bool vencido;
                unsafe
                {
                    lock (_candado)
                    {
                        // Solo con el temporizador vencido, y avisando a la bomba de envío: lo que deja
                        // listo —la retransmisión de un PTO, un ACK retrasado— esperaba antes a que esa
                        // bomba despertara por su cuenta, hasta 30 ms tarde.
                        vencido = NativePtr is not null && NativeMethods.quiche_conn_timeout_as_nanos(NativePtr) == 0;
                        if (vencido)
                        {
                            NativePtr->OnTimeout();   // la inactividad y el fin del drenaje cierran aquí
                            AnotarSiCerrada();
                        }
                    }
                }
                if (vencido)
                {
                    SeñalizarEnvio();

                    // Lo que el temporizador da por perdido deja de contar en vuelo, y la ventana de congestión —que limita
                    // también lo que quiche admite de cada flujo— se abre sin paquete que lo diga: lo que chocó con ella,
                    // otra vez. Ver _flujosBloqueados. En h3 lo mismo lo espera su bomba, que despierta a quien espera sitio
                    // para un cuerpo: esperaba hasta el paquete siguiente, o sus 250 ms de seguridad.
                    if (Interlocked.Exchange(ref _flujosBloqueados, 0) != 0) SeñalizarFlujos();
                    if (EsH3) bombaFlujosRecv.Avisar();
                }

                // Drenar la cola de una sentada, hasta la tanda (ver TandaDeRecepcion); cada paquete procesado puede
                // dejar ACKs pendientes, así que se avisa a la bomba de envío al final.
                // El paquete se lee directo del arrendado —sin copia intermedia— y se
                // devuelve al pool en cuanto quiche lo procesa.
                bool procesado = false;
                int procesados = 0, sacados = 0;
                while (sacados < TandaDeRecepcion && Sacar(out var paquete))
                {
                    sacados++;
                    if (IsClosed)
                    {
                        Devolver(paquete.Buf);
                        CompletarFlujos();
                        throw new QuicheException(QuicheError.QUICHE_ERR_DONE, "Connection was closed.");
                    }

                    long resultOrError = ProcesarPaquete(paquete.Buf, paquete.Len, paquete.Origen is not null, out bool aceptado);

                    // Lo que cuenta para el límite de amplificación del servidor: ver RepetirPrimeroDelSaludo.
                    if (resultOrError >= 0 && !establishedTcs.Task.IsCompleted) Interlocked.Add(ref _recibidoEnSaludo, paquete.Len);

                    Devolver(paquete.Buf);

                    // Un paquete que quiche no acepta no para la bomba: si el fallo es de la conexión, quiche la cierra él —con
                    // su CONNECTION_CLOSE— y el drenaje acaba en on_timeout, como cualquier cierre: los flujos ven su fin y quien
                    // espera a la conexión se entera. Salir de aquí la dejaba a medias: sin fin para los flujos, sin IsClosed, y
                    // sin nadie que atendiera ya sus temporizadores.
                    //
                    // Uno que corta el saludo con una alerta TLS se dice ya, a quien lo espera: ver FallarSaludoPorTls.
                    if (resultOrError < 0)
                    {
                        if (resultOrError == (long)QuicheError.QUICHE_ERR_TLS_FAIL) FallarSaludoPorTls();
                        continue;
                    }

                    // Un rebinding del otro lado, confirmado: quiche aceptó el paquete, así que es del par. Se repuntaba al
                    // recibirlo, antes de autenticarlo, y un datagrama de otro servicio, ruido o uno falsificado desviaba la
                    // conexión hasta que el vigilante la diera por muda (RFC 9000 §9).
                    if (aceptado && paquete.Origen is { } nuevo) Repuntar(nuevo);
                    procesado = true;
                    procesados++;
                }

                // Lo que quedó tras la tanda, en la vuelta siguiente: el aviso hace que la bomba no se duerma.
                if (sacados >= TandaDeRecepcion && !recvQueue.IsEmpty) SeñalizarRecv();

                // Un cierre durante el saludo se dice ya: ver FallarSaludoPorCierre.
                if (procesado && !establishedTcs.Task.IsCompleted) FallarSaludoPorCierre();

                trabajo = vencido || procesado;
                if (procesado)
                {
                    AvisarDeActividad();

                    // Los datagramas, ya; y el drenaje dice si quedó algún flujo que leer. Sin ninguno, su bomba no despierta:
                    // con el tráfico de datagramas —el UDP de MASQUE, Hysteria 2, TUIC o QSOCKS, y CONNECT-IP— despertaba en
                    // cada tanda de paquetes, tomaba el cerrojo y no encontraba nada. En h3 despierta siempre: su bomba avisa
                    // además a quien espera sitio para un cuerpo.
                    //
                    // Y el ACK de la tanda sale ya, salvo que espere a la respuesta a sus datagramas: ver _ackAplazado. Con el
                    // temporizador vencido no espera: lo que deja listo son retransmisiones.
                    bool aplazado = false;
                    bool legibles = datagramasRecibidos is null
                                    || DrenarDatagramas(!vencido && Interlocked.Add(ref _paquetesSinAck, procesados) < PaquetesSinAck,
                                                        out aplazado)
                                    || EsH3;
                    if (!aplazado) SeñalizarEnvio();
                    if (legibles) bombaFlujosRecv.Avisar();  // los flujos legibles se drenan ya

                    // Lo que chocó con una ventana, otra vez: los paquetes traen las nuevas. Ver _flujosBloqueados.
                    if (Interlocked.Exchange(ref _flujosBloqueados, 0) != 0) SeñalizarFlujos();
                }

                // El saludo se mira DESPUÉS de procesar: el paquete que lo completa avisa ya
                // a quien lo espera, sin aguardar al despertar siguiente —hasta 25 ms más de
                // saludo en el lado que termina último—.
                bool isConnEstablished;
                unsafe
                {
                    lock (_candado)
                    {
                        isConnEstablished = NativePtr is not null && NativePtr->IsEstablished();
                    }
                }

                if (isConnEstablished)
                {
                    bool recien = establishedTcs.TrySetResult();
                    utilizableTcs.TrySetResult();
                    // Los flujos encolados antes del saludo ya pueden salir. Al completarse, y en cada vuelta con trabajo si hay
                    // alguno con algo por mandar —lo que no cabía en la ventana sigue anotado—; sin ninguno, la bomba de flujos
                    // no tiene qué hacer, y con el tráfico de datagramas despertaba en cada tanda de paquetes a tomar el cerrojo.
                    if (recien || (trabajo && Volatile.Read(ref _flujosMarcados) > 0)) SeñalizarFlujos();

                    // El ticket para reanudar llega tras el saludo: se entrega en cuanto aparece.
                    if (_alRecibirSesion is not null) EntregarSesionSiHay();
                }
                else if (_earlyData && IsServer && !utilizableTcs.Task.IsCompleted && EnEarlyData)
                {
                    // El servidor aceptó el 0-RTT: sus flujos ya se atienden sin esperar al
                    // Finished del cliente. Ese es el RTT que se ahorra.
                    if (utilizableTcs.TrySetResult())
                        Interlocked.Increment(ref QuicheDiagnostics.EarlyDataCount);
                    SeñalizarFlujos();
                }
            }
            catch (QuicheException ex)
            {
                MarcarMuerta();
                establishedTcs.TrySetException(ex);
                throw;
            }
            catch (OperationCanceledException)
            {
                establishedTcs.TrySetCanceled(cancellationToken);
                throw;
            }

            // A dormir hasta que el socket meta un paquete —aviso al instante— o venza el temporizador de quiche, con su cita.
            // El plazo que no es del temporizador es de seguridad, y crece en reposo: ver EsperaDeBomba.
            seguro.Tras(avisada || trabajo);
            avisada = await Dormir(bombaRecv, ArmarTemporizador(seguro.Actual), cancellationToken);
        }
    }

    /// <summary>
    /// Duerme una bomba hasta su aviso o hasta <paramref name="plazo"/>, lo que llegue antes: verdadero si la despertó un
    /// aviso. El plazo lo vigila <see cref="_guardia"/>, que se programa aquí —solo adelanta, y casi siempre ya vencía antes:
    /// una lectura y una barrera—.
    /// </summary>
    private ValueTask<bool> Dormir(AvisoDeBomba bomba, TimeSpan plazo, CancellationToken ct)
    {
        long ms = Math.Max(1, (long)Math.Ceiling(plazo.TotalMilliseconds));
        var espera = bomba.EsperarAsync(ms, ct);
        if (!espera.IsCompleted)
            Cloudflare.QuicheNet.Internal.PlanificadorDeCitas.Compartido.Programar(_guardia, Environment.TickCount64 + ms);
        return espera;
    }

    /// <summary>
    /// El plazo de seguridad de las bombas de envío, de flujos y de recepción, como una sola cita del planificador compartido
    /// y no un temporizador por espera: la bomba que se duerme la programa, y al vencer despierta a la que lleve dormida su
    /// plazo y dice cuándo vence la siguiente. Sin despertador preciso, el plazo de la de recepción es el temporizador de
    /// quiche, con la resolución del planificador —la del temporizador del runtime que había antes—.
    /// </summary>
    private sealed class GuardiaDeBombas : Cloudflare.QuicheNet.Internal.Cita
    {
        private readonly QuicheConnection _conexion;

        public GuardiaDeBombas(QuicheConnection conexion) => _conexion = conexion;

        protected internal override long Vencer(long ahora)
        {
            var c = _conexion;
            if (c.cts.IsCancellationRequested) return long.MaxValue;
            return Math.Min(c.bombaRecv.Vigilar(ahora), Math.Min(c.bombaFlujos.Vigilar(ahora), c.bombaEnvio.Vigilar(ahora)));
        }
    }

    /// <summary>
    /// La cita del próximo temporizador de quiche en el <see cref="DespertadorPreciso"/>: la arma la bomba de recepción —y la
    /// de envío tras un silencio—, y al vencer despierta a la de recepción. Una sola, que se adelanta donde está: ver
    /// <see cref="ArmarTemporizador"/>.
    /// </summary>
    private readonly CitaPrecisa _citaDelTemporizador;

    /// <summary>
    /// Lo más que duerme una bomba sin aviso. Solo cubre un aviso perdido: el dato no lo espera —quien encola avisa, y la
    /// bomba de recepción avisa tras cada tanda de paquetes y al vencer el temporizador de quiche, que tiene su cita—. Eran
    /// 25-30 ms, del orden de cien despertares por segundo por conexión ociosa entre las tres bombas.
    /// </summary>
    private static readonly TimeSpan EsperaDeSeguridad = TimeSpan.FromSeconds(1);

    /// <summary>A lo que llega la espera de seguridad de una bomba en reposo: ver <see cref="EsperaDeBomba"/>.</summary>
    internal static readonly TimeSpan EsperaDeSeguridadMaxima = TimeSpan.FromSeconds(8);

    /// <summary>
    /// La espera de seguridad de una bomba: <see cref="EsperaDeSeguridad"/> mientras la despiertan avisos o encuentra trabajo,
    /// y el doble cada vez que vence sin ninguna de las dos cosas, hasta <see cref="EsperaDeSeguridadMaxima"/>.
    /// <para>
    /// Con la conexión ociosa, cada una de las tres bombas despertaba una vez por segundo solo por si se había perdido un
    /// aviso: tres despertares por segundo y conexión, con el cerrojo y llamadas nativas, y en el móvil, batería. Tras unos
    /// segundos de reposo es uno cada ocho. Lo que cubre sigue cubierto —un aviso perdido en reposo tarda eso en atenderse,
    /// y no hay ninguno conocido: cuando aparecieron, se arreglaron sus avisos—, y en cuanto hay trabajo vuelve al segundo.
    /// </para>
    /// </summary>
    private struct EsperaDeBomba
    {
        private TimeSpan _actual;

        /// <summary>Lo que se duerme ahora como mucho.</summary>
        public readonly TimeSpan Actual => _actual == default ? EsperaDeSeguridad : _actual;

        /// <summary>Tras una vuelta: con aviso o con trabajo, al segundo; sin nada, el doble.</summary>
        public void Tras(bool avisadaOConTrabajo)
            => _actual = avisadaOConTrabajo ? EsperaDeSeguridad
                       : Actual >= EsperaDeSeguridadMaxima ? EsperaDeSeguridadMaxima
                       : Actual + Actual;
    }

    /// <summary>
    /// 1 si algún flujo se quedó con algo que quiche no admitió —su ventana, la de la conexión, el tope de flujos del par, o
    /// la ventana de congestión, que quiche también aplica a lo que admite—. Lo pone quien choca, con el cerrojo de la
    /// conexión; la bomba de recepción avisa entonces a la de flujos tras procesar paquetes —que traen las ventanas nuevas y
    /// los ACK— y al vencer el temporizador —lo que da por perdido deja de contar en vuelo—. Con el mismo cerrojo, una
    /// ventana que llegue después del choque ve la marca, y una que llegue antes ya la vio quien chocaba: la bomba de flujos
    /// no necesita sondear.
    /// </summary>
    private int _flujosBloqueados;

    /// <summary>
    /// Pone su cita precisa al próximo temporizador de quiche y dice cuánto dormir. Con despertador preciso, la cita despierta
    /// a la bomba —con ~1 ms de error— y la bomba duerme la espera de seguridad; sin él, duerme hasta el temporizador. Antes
    /// solo tenían cita los que vencían antes de 25 ms, y la bomba sondeaba cada 25 ms —en Windows, ~31—: una conexión ociosa
    /// despertaba unas cuarenta veces por segundo solo por esta bomba, y un PTO de 300 ms salía hasta 31 ms tarde. La llama
    /// la bomba de recepción, para saber cuánto dormir, y la de envío tras mandar, solo por la cita: ver
    /// <see cref="TemporizadorTrasEnviar"/>. Por eso la cita solo se adelanta, y con CAS: dos a la vez no dejan la más
    /// tardía en su sitio.
    /// </summary>
    /// <param name="seguridad">La espera de seguridad de la bomba ahora: ver <see cref="EsperaDeBomba"/>.</param>
    private unsafe TimeSpan ArmarTemporizador(TimeSpan seguridad)
    {
        ulong ns;
        lock (_candado)
        {
            if (NativePtr is null) return seguridad;
            ns = NativeMethods.quiche_conn_timeout_as_nanos(NativePtr);
        }

        // Sin temporizador —quiche da el máximo— o más lejos que la espera de seguridad: basta con ella. A menos de 1 ms —o
        // vencido mientras se procesaba—, en 1 ms, que es la resolución de los temporizadores de quiche: una cita más corta
        // vence en el acto —el despertador da por llegada la que cae en su margen, y en Linux Task.Delay no baja de 1 ms—,
        // y la bomba daba vueltas en vacío hasta la hora.
        if (ns >= (ulong)seguridad.Ticks * 100) return seguridad;
        if (ns < 1_000_000) ns = 1_000_000;

        if (!DespertadorPreciso.Disponible) return TimeSpan.FromTicks(((long)ns + 99) / 100);

        // Si no hay ya una pendiente que venza antes, se adelanta donde está. Pendiente es lo que dice el despertador, que la
        // deja sin hora al vencer: ver CitaPrecisa.Pendiente. Por la hora, una cita que vencía algo antes de la suya seguía
        // contando como pendiente: la bomba despertaba, quiche aún no tenía nada vencido, no se armaba otra, y se dormía la
        // espera de seguridad entera, un segundo, con un PTO o la inactividad por atender.
        DespertadorPreciso.Adelantar(_citaDelTemporizador, DespertadorPreciso.AhoraNs() + (long)ns);
        return seguridad;
    }

    /// <summary>
    /// Tras mandar: cada paquete que pide ACK arma su PTO en quiche, y la cita del temporizador la armaba solo la bomba de
    /// recepción al despertar, que por un camino mudo no despierta —no llega nada—. El PTO del primer paquete tras un rato
    /// sin tráfico esperaba así a la espera de seguridad de esa bomba —hasta un segundo— en vez de vencer a su hora; y el
    /// vigilante del camino tardaba eso más en ver un camino mudo. Con despertador preciso se arma aquí la cita; sin él, se
    /// despierta a la bomba de recepción, que duerme hasta el temporizador nuevo.
    /// <para>
    /// Solo sin cita pendiente o tras <see cref="QuietudParaArmar"/> sin mandar nada. Con tráfico seguido, la cadena de citas
    /// la mantiene la bomba de recepción —cada cita que vence la despierta, y arma la siguiente—, y armarla también aquí era
    /// el cerrojo y una llamada al motor por cada lote, en el camino de cada eco: por QSOCKS, la mediana del eco subía un 14 %.
    /// </para>
    /// </summary>
    /// <param name="ultimoEnvio">Cuándo salió lo anterior, en marcas de Stopwatch; se actualiza.</param>
    private void TemporizadorTrasEnviar(ref long ultimoEnvio)
    {
        long ahora = System.Diagnostics.Stopwatch.GetTimestamp();
        bool trasQuietud = ahora - ultimoEnvio > QuietudParaArmar;
        ultimoEnvio = ahora;
        if (!trasQuietud && _citaDelTemporizador.Pendiente) return;

        if (DespertadorPreciso.Disponible) _ = ArmarTemporizador(EsperaDeSeguridad);
        else SeñalizarRecv();
    }

    /// <summary>
    /// Lo que tiene que pasar sin mandar nada para que lo siguiente que sale arme la cita: más que el retraso de los ACK, en
    /// marcas de Stopwatch. Ver <see cref="TemporizadorTrasEnviar"/>.
    /// </summary>
    private static readonly long QuietudParaArmar = System.Diagnostics.Stopwatch.Frequency / 20;   // 50 ms

    /// <summary>
    /// Pasa a su canal los datagramas que dejó quiche al procesar los paquetes. Lo llama la
    /// bomba de recepción justo después, así que no hay sondeo ni espera.
    /// <para>
    /// Los saca por tandas de <see cref="DatagramasPorTanda"/> con un solo cerrojo —era uno por datagrama, disputado con la
    /// bomba de envío— y los encola fuera de él. Devuelve si queda algún flujo que leer: se mira con el mismo cerrojo al
    /// vaciar la cola de quiche, cuando lo legible ya es solo de los flujos.
    /// </para>
    /// </summary>
    /// <param name="aplazable">Si el ACK de la tanda puede esperar a la respuesta: ver <see cref="_ackAplazado"/>.</param>
    /// <param name="aplazado">Si espera: la bomba de recepción no avisa entonces a la de envío.</param>
    private bool DrenarDatagramas(bool aplazable, out bool aplazado)
    {
        aplazado = false;
        aplazable &= DespertadorPreciso.Disponible;
        var tanda = _tandaRecibida ??= new DatagramaQuic[DatagramasPorTanda];
        while (true)
        {
            int n = 0;
            bool vacia = false, legibles = true;
            unsafe
            {
                lock (_candado)
                {
                    if (NativePtr is null) return true;
                    while (n < tanda.Length)
                    {
                        long delFrente = NativePtr->DgramRecvFrontLen();
                        if (delFrente < 0)   // DONE: no queda ninguno
                        {
                            vacia = true;
                            break;
                        }

                        // En un búfer prestado: ver DatagramaQuic. Uno vacío también lleva el suyo: quiche no admite un
                        // puntero nulo ni con largo 0.
                        int largo = (int)delFrente;
                        byte[] buf = ArrayPool<byte>.Shared.Rent(Math.Max(1, largo));
                        fixed (byte* p = buf)
                        {
                            if (NativePtr->DgramRecv(p, (nuint)Math.Max(1, largo)) < 0)
                            {
                                ArrayPool<byte>.Shared.Return(buf);
                                vacia = true;
                                break;
                            }
                        }
                        tanda[n++] = new DatagramaQuic(buf, largo);
                    }

                    // Sin datagramas en quiche, lo legible es de los flujos.
                    if (vacia) legibles = NativePtr->IsReadable();

                    // Con la primera tanda, si el ACK puede esperar: con el saludo hecho —los ACK del saludo no esperan—, y sin
                    // datagramas propios esperando a la ventana de congestión, que los ACK recibidos pueden abrir.
                    if (aplazable && n > 0)
                    {
                        aplazable = false;
                        aplazado  = NativePtr->IsEstablished() && NativePtr->DgramSendQueueLen() == 0;
                    }
                }
            }

            // La marca, antes de entregar: la respuesta despierta a la bomba de envío, que la borra. Puesta después, la cita
            // despertaba en balde a esa bomba al vencer.
            if (aplazado && Volatile.Read(ref _ackAplazado) == 0)
            {
                long desde = DespertadorPreciso.AhoraNs();
                Volatile.Write(ref _ackAplazadoDesde, desde);
                Volatile.Write(ref _ackAplazado, 1);
                DespertadorPreciso.Adelantar(_citaDelAck, desde + PlazoDelAckNs);
            }

            // Con la cola ya cerrada —la conexión se acaba— el búfer vuelve al almacén aquí.
            for (int i = 0; i < n; i++)
            {
                if (!datagramasRecibidos!.TryEscribir(tanda[i])) tanda[i].Devolver();
                tanda[i] = default;
            }
            if (vacia) return legibles;
        }
    }

    /// <summary>Lo más que saca <see cref="DrenarDatagramas"/> con cada cerrojo.</summary>
    private const int DatagramasPorTanda = 16;

    /// <summary>
    /// 1 si el ACK de una tanda de paquetes espera a la respuesta a sus datagramas, en vez de salir solo en cuanto se
    /// procesan. quiche no retrasa los ACK —los pone en lo primero que manda—, y la bomba de recepción avisaba a la de envío
    /// tras cada tanda. Con datagramas en los dos sentidos —el TCP de CONNECT-IP, el UDP de MASQUE, Hysteria 2, TUIC o
    /// QSOCKS— salía así un paquete con solo el ACK y, a las pocas decenas de microsegundos, otro con la respuesta: en
    /// CONNECT-IP, uno de cada seis envíos era solo un ACK, y nueve de cada diez iban seguidos de datos en menos de 50 µs.
    /// Cada uno era un envío al sistema, y en el otro extremo una lectura y un paquete que descifrar.
    /// <para>
    /// La pone la bomba de recepción antes de entregar los datagramas, y entonces no avisa a la de envío. La borra la bomba
    /// de envío al despertar por lo que sea —lo que genere lleva el ACK—; si nadie manda nada, lo suelta
    /// <see cref="_citaDelAck"/> a los <see cref="PlazoDelAckNs"/>. No se aplaza durante el saludo, con el temporizador de
    /// quiche vencido, con datagramas propios esperando sitio en la ventana de congestión, ni con
    /// <see cref="PaquetesSinAck"/> paquetes sin confirmar. Lo que no son datagramas —los flujos— sigue con su ACK en el
    /// acto: una bajada no tiene respuesta que lo lleve, y su emisor cuenta con él para abrir la ventana.
    /// </para>
    /// <para>
    /// Esperar a que quien lee los datagramas vaciara su cola no servía: por el lado del cliente, los de MASQUE y los de UDP
    /// pasan a la cola de su sesión, y la respuesta llega después de vaciar la de la conexión.
    /// </para>
    /// <para>
    /// Lo que cuesta: en un intercambio de ida y vuelta, el paquete con solo el ACK llegaba al otro extremo justo antes que la
    /// respuesta y despertaba a sus bombas, que la atendían ya despiertas; sin él, la respuesta las encuentra dormidas. Con un
    /// eco de UDP contra un destino del mismo proceso y el proceso en 4 núcleos, la mediana de la vuelta sube de ~0,130 a
    /// ~0,143 ms. Por una red de verdad la respuesta llega cuando esas bombas ya dormían también antes. Esperar solo en las
    /// tandas de más de un paquete no lo arreglaba —el eco de CONNECT-IP salía peor en 9 de 10 rondas— y perdía lo ganado.
    /// </para>
    /// </summary>
    private int _ackAplazado;

    /// <summary>Cuándo se aplazó el ACK que espera, en el reloj del despertador: ver <see cref="VencerAck"/>.</summary>
    private long _ackAplazadoDesde;

    /// <summary>Lo más que espera un ACK aplazado: ver <see cref="_ackAplazado"/> y <see cref="VencerAck"/>.</summary>
    private readonly CitaDelAck _citaDelAck;

    /// <summary>La cita de <see cref="_citaDelAck"/>: vence en el hilo del despertador, y solo suelta o se vuelve a armar.</summary>
    private sealed class CitaDelAck : Cloudflare.QuicheNet.Internal.Cita
    {
        private readonly QuicheConnection _conexion;

        public CitaDelAck(QuicheConnection conexion) => _conexion = conexion;

        protected internal override long Vencer(long ahora) => _conexion.VencerAck(ahora);
    }

    /// <summary>
    /// Al vencer <see cref="_citaDelAck"/>: suelta el ACK aplazado si lleva <see cref="PlazoDelAckNs"/> esperando, y si es de
    /// una tanda más reciente vuelve a su hora, desde el hilo del despertador. La cita la arma el primer aplazamiento y se
    /// queda para los que siguen —los demás la ven pendiente y no tocan el despertador—, así que al vencer lo que encuentra
    /// suele ser de ahora mismo. Soltarlo entonces era un paquete con solo el ACK justo mientras se preparaba la respuesta que
    /// lo llevaba, y el aplazamiento siguiente volvía a armar la cita con el cerrojo del despertador: en CONNECT-IP, un 7 %
    /// más de CPU por MB (10 de 10 rondas).
    /// </summary>
    private long VencerAck(long ahora)
    {
        if (Volatile.Read(ref _ackAplazado) == 0) return long.MaxValue;
        long vence = Volatile.Read(ref _ackAplazadoDesde) + PlazoDelAckNs;
        if (vence > ahora + DespertadorPreciso.MargenNs) return vence;
        SoltarAck();
        return long.MaxValue;
    }

    /// <summary>
    /// Los paquetes procesados desde la última vuelta de la bomba de envío, que lleva el ACK en lo que genera: con
    /// <see cref="PaquetesSinAck"/>, el ACK ya no espera.
    /// </summary>
    private int _paquetesSinAck;

    /// <summary>
    /// Con cuántos paquetes sin confirmar el ACK sale sin esperar a la respuesta, como el ACK cada diez paquetes de Chromium:
    /// un chorro de datagramas en un solo sentido lleva así su ACK cada pocos paquetes, y no solo al vencer el plazo.
    /// </summary>
    private const int PaquetesSinAck = 10;

    /// <summary>
    /// Lo que espera como mucho un ACK aplazado a que lo lleve la respuesta, muy por debajo del max_ack_delay que quiche
    /// anuncia al par (25 ms), que es lo que el par suma a su PTO. Con 1 ms —la resolución del despertador— la cita vencía y
    /// se volvía a armar cada milisegundo mientras hubiera tráfico, y con el proceso en 4 núcleos el p99 del eco de
    /// CONNECT-IP subía a ~0,31 ms; con 5 ms, ~0,25 (mejor en 8 de 8 rondas).
    /// </summary>
    private const long PlazoDelAckNs = 5_000_000;

    /// <summary>El ACK aplazado, si lo hay, sale ya: ver <see cref="_ackAplazado"/>.</summary>
    private void SoltarAck()
    {
        if (Volatile.Read(ref _ackAplazado) != 0 && Interlocked.Exchange(ref _ackAplazado, 0) != 0) SeñalizarEnvio();
    }

    /// <summary>Donde <see cref="DrenarDatagramas"/> deja una tanda antes de encolarla: solo la toca la bomba de recepción.</summary>
    private DatagramaQuic[]? _tandaRecibida;

    /// <summary>
    /// Un flujo que la bomba dejó de leer por lleno ya tiene sitio: que vuelva a mirar. Lo llama
    /// el lector del flujo al vaciarlo.
    /// </summary>
    internal void ReanudarRecepcion() => bombaFlujosRecv.Avisar();

    /// <summary>
    /// Lo que la bomba saca de un flujo en cada lectura. Por debajo de los 85 KB del montón de
    /// objetos grandes: antes se leía hasta 1 MB de una vez, y cada copia iba a ese montón y
    /// traía recolecciones de generación 2 en plena ráfaga.
    /// </summary>
    private const int TrozoDeLectura = 64 * 1024;

    /// <summary>
    /// Los flujos con algo que leer ahora mismo, todos. Con <c>StreamReadableNext</c> solo se
    /// veía el primero: si ese estaba lleno y no se leía, quiche lo seguía dando el primero y
    /// los demás flujos de la conexión no se leían nunca.
    /// </summary>
    private unsafe bool FlujosLegibles(List<ulong> ids)
    {
        ids.Clear();
        lock (_candado)
        {
            if (NativePtr is null) return false;
            if (!NativePtr->IsEstablished() && !NativePtr->IsInEarlyData()) return false;

            quiche_stream_iter* iterador = NativeMethods.quiche_conn_readable(NativePtr);
            if (iterador is null) return true;
            ulong id;
            while (NativeMethods.quiche_stream_iter_next(iterador, &id)) ids.Add(id);
            NativeMethods.quiche_stream_iter_free(iterador);
            return true;
        }
    }

    private async Task ReceiveStreamAsync(CancellationToken cancellationToken)
    {
        byte[]? streamBuf = null;   // al primer flujo legible: una conexión a medio saludo no lo necesita
        var legibles = new List<ulong>();
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsClosed)
            {
                CompletarFlujos();
                throw new QuicheException(QuicheError.QUICHE_ERR_DONE, "Connection was closed.");
            }

            // Drenar todos los flujos legibles, ronda tras ronda, mientras alguno dé datos. Uno
            // lleno se salta —quiche se queda sus datos y no reabre la ventana al par— y su lector
            // avisa al vaciarlo. El aviso de datos nuevos llega con cada paquete procesado.
            bool progreso = true, leido = false;
            while (progreso && FlujosLegibles(legibles))
            {
                progreso = false;
                foreach (ulong streamId in legibles)
                {
                    QuicheStream stream = GetStream(streamId);
                    if (stream.CanRead && stream.PausarSiLlena()) continue;
                    streamBuf ??= new byte[TrozoDeLectura];

                    try
                    {
                        bool streamFinished = false;
                        long recvCount = long.MaxValue;
                        while (!streamFinished && recvCount > 0)
                        {
                            recvCount = LeerDeFlujo(streamId, streamBuf, out streamFinished);

                            if (recvCount > 0)
                            {
                                await stream.ReceiveDataAsync(
                                    streamBuf.AsMemory(0, (int)recvCount),
                                    streamFinished, cancellationToken
                                    );
                                progreso = true;
                                leido    = true;

                                // Lleno en mitad de la lectura: lo demás espera en quiche.
                                if (!streamFinished && stream.PausarSiLlena()) break;
                            }
                            else if (streamFinished)
                            {
                                // FIN sin datos detrás: quiche devuelve 0 con el FIN ya puesto.
                                // Sin esto el EOF se pierde y el lector del flujo cuelga.
                                await stream.ReceiveDataAsync(
                                    ReadOnlyMemory<byte>.Empty, true, cancellationToken
                                    );
                                progreso = true;
                                break;
                            }
                            else if (recvCount == (long)QuicheError.QUICHE_ERR_DONE)
                            {
                                // Nada más que leer de este flujo por ahora: es lo normal al vaciarlo. Antes se lanzaba y se
                                // recogía aquí mismo —una excepción por flujo en cada vuelta de la bomba, casi el 3 % de lo
                                // que asignaba un flujo QUIC a plena carga—. Con la conexión cerrada se lanza como antes.
                                if (IsClosed) throw new QuicheException(QuicheError.QUICHE_ERR_DONE, "Connection was closed.");
                                break;
                            }
                            else
                            {
                                QuicheException.ThrowIfError((QuicheError)recvCount);
                            }
                        }
                    }
                    catch (QuicheException ex)
                        when (ex.ErrorCode == QuicheError.QUICHE_ERR_DONE)
                    {
                        if (IsClosed) { throw; }
                    }
                    catch (QuicheException ex)
                        when (ex.ErrorCode is QuicheError.QUICHE_ERR_STREAM_RESET or QuicheError.QUICHE_ERR_STREAM_STOPPED)
                    {
                        // Un flujo abortado por el par no mata la bomba: el lector del flujo ve
                        // EOF y los demás flujos de la conexión siguen vivos.
                        stream.AbortRecv();
                        progreso = true;
                    }
                    catch (QuicheException ex) when (EsErrorDeFlujo(ex.ErrorCode))
                    {
                        // Un error de ESTE flujo —cortado desde aquí mientras se leía, por ejemplo—: su lector ve el fin y los
                        // demás siguen. Mataba la bomba de flujos de toda la conexión, que seguía viva a ojos de todos.
                        stream.AbortRecv();
                        progreso = true;
                    }
                    catch (QuicheException ex)
                    {
                        MarcarMuerta();
                        establishedTcs.TrySetException(ex);
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        establishedTcs.TrySetCanceled(cancellationToken);
                        throw;
                    }
                }
            }

            // Lo leído abre la ventana del par, y quiche la anuncia —MAX_STREAM_DATA, MAX_DATA— con lo siguiente que mande:
            // se avisa a la bomba de envío. Salía solo con el ACK de los paquetes siguientes, y si el ACK de la tanda salió
            // antes de esta lectura y el par ya había agotado la ventana, no llegaba ninguno: la ventana nueva esperaba a la
            // espera de seguridad de la bomba de envío, un segundo, y la bajada se paraba con ella. Una vez por ronda.
            if (leido) SeñalizarEnvio();

            // A dormir hasta que un paquete deje flujos legibles o un lector vacíe uno lleno.
            await bombaFlujosRecv.EsperarAsync(0, cancellationToken);
        }
    }

    /// <summary>
    /// La escucha de una conexión de salida: lo que llega por su canal, a la cola de la bomba de recepción.
    /// <para>
    /// Se lee en un búfer de lo más largo que puede traer UDP (<see cref="QuicheLibrary.MaxDatagramaUdp"/>), uno para toda la
    /// escucha, y cada datagrama pasa a la cola en uno prestado de su medida. Se leía en uno prestado de 6115 bytes por
    /// datagrama, que iba a la cola tal cual: lo de más llegaba cortado, y quiche lo tiraba —una sonda del PMTUD del par por
    /// el bucle local, por ejemplo—, mientras se le anunciaba al par que se recibían 65 527. Prestar 64 KB por datagrama para
    /// la cola sería guardar 64 KB por cada uno de 1452 que espera; la copia a uno de su medida cuesta del orden de una
    /// centésima de milisegundo por MB.
    /// </para>
    /// </summary>
    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        byte[] recepcion = ArrayPool<byte>.Shared.Rent(QuicheLibrary.MaxDatagramaUdp);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    SocketReceiveFromResult recibido = await canal.RecibirAsync(recepcion, remoteEndPoint, cancellationToken);
                    int largo = recibido.ReceivedBytes, desde = 0;
                    EndPoint origen = recibido.RemoteEndPoint;
                    if (_ofuscador is not null)
                    {
                        // Lo que no se desenvuelve no es de esta conexión: se tira sin tocar el destino del envío.
                        desde = _ofuscador.Decodificar(recepcion.AsSpan(0, largo));
                        if (desde < 0) continue;
                        largo -= desde;
                    }

                    // Rebinding del otro lado: si el origen cambió con la conexión ya establecida, se repuntará SOLO el destino
                    // del envío, y solo si quiche acepta el paquete —ver la bomba de recepción—. El mundo de quiche no se entera:
                    // el recv_info sigue clavado al extremo original, así que cada paquete —venga de donde venga— se procesa como
                    // del camino de siempre y no se dispara validación de migración alguna.
                    EndPoint? origenNuevo = origen is not null && !origen.Equals(remoteEndPoint) && Establecido ? origen : null;

                    // A la cola, en uno de su medida: quiche lo lee desde el principio del búfer.
                    byte[] paquete = ArrayPool<byte>.Shared.Rent(Math.Max(1, largo));
                    recepcion.AsSpan(desde, largo).CopyTo(paquete);
                    if (Encolar(paquete, largo, origenNuevo)) SeñalizarRecv();
                }
                catch (OperationCanceledException) { throw; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException ex)
                {
                    // Un ICMP suelto —el par cerró y el sistema avisa— no mata el socket;
                    // la pérdida de la interfaz sí. Lo mismo que el puerto UDP compartido.
                    if (ex.SocketErrorCode is SocketError.NotSocket or SocketError.Shutdown
                        or SocketError.OperationAborted or SocketError.NetworkDown
                        or SocketError.NetworkReset or SocketError.Interrupted)
                    {
                        break;
                    }
                    continue;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(recepcion);
        }

        // Sin cancelar, la escucha solo sale porque el camino murió —el socket cerrado por su dueño, o un canal que lo da por
        // caído, como la salida compartida del salto—: ya no llegará nada. La conexión se da por cerrada y sus flujos ven el
        // fin; nadie la cerraba, y lo que esperaba en ella esperaba a la inactividad de quiche.
        if (!cancellationToken.IsCancellationRequested) MarcarMuerta();
    }

    private QuicheStream GetStream(ulong streamId) =>
        streamMap.GetOrAdd(streamId, id =>
        {
            QuicheStream stream = new(this, id);
            if ((id & 1) != 0 ^ IsServer)
            {
                SpinWait.SpinUntil(() => streamChannel.Writer.TryWrite(stream));
            }
            return stream;
        });

    // ── Llamadas al motor desde las bombas ──────────────────────────────
    // Lo que quiche lee o escribe por puntero no puede ser una variable de un método
    // async: si cruza un await —y en Debug todas— vive en la caja del estado, en el
    // montón, y el GC puede moverla en plena llamada nativa. quiche escribiría
    // entonces en memoria ajena. Por eso las bombas llaman al motor desde aquí, con
    // locales de pila.

    /// <summary>
    /// Pide a quiche el siguiente paquete, escrito a partir de <paramref name="desde"/>, de <paramref name="largoMax"/> bytes
    /// como mucho: con menos sitio, quiche arma paquetes más cortos y no sondea por encima. Devuelve lo escrito o el error, y
    /// su «at».
    /// </summary>
    private unsafe long GenerarPaquete(byte[] buf, int desde, int largoMax, out timespec at)
    {
        quiche_send_info info = default;
        long resultOrError = (long)QuicheError.QUICHE_ERR_DONE;   // liberada: nada que generar
        lock (_candado)
        {
            if (NativePtr is not null)
            {
                fixed (byte* pktPtr = buf)
                {
                    resultOrError = (long)NativePtr->Send(pktPtr + desde, (nuint)Math.Min(buf.Length - desde, largoMax), &info);
                }
                AnotarSiCerrada();

                // En un cierre con gracia, una vuelta que acaba sin nada más que mandar mira, con este mismo cerrojo, si lo
                // mandado ya está confirmado: ver EsperarAQueSalga. Lo mandado menos lo confirmado y lo perdido —lo perdido que
                // se retransmite vuelve a contar como mandado—.
                if (resultOrError == (long)QuicheError.QUICHE_ERR_DONE && _enCierre)
                {
                    Interlocked.Increment(ref _vueltasDeCierre);
                    DisposicionUnix32.LeerStats(NativePtr, out quiche_stats st);
                    if ((long)st.sent_bytes - (long)st.acked_bytes - (long)st.lost_bytes <= EnVueloDeSoloAcks)
                        Interlocked.Increment(ref _vueltasVacias);
                }
            }
        }
        // En un Unix de 32 bits, el «at» del nativo no está donde lo pone la struct de C#: ver DisposicionUnix32.
        at = DisposicionUnix32.Activa ? DisposicionUnix32.At((byte*)&info) : info.at;
        return resultOrError;
    }

    /// <summary>Entrega a quiche un paquete recibido. Devuelve lo procesado o el error.</summary>
    /// <param name="mirarSiAcepta">Si hay que saber si quiche lo aceptó —autenticado y procesado—: lo dice <paramref name="aceptado"/>.</param>
    private unsafe long ProcesarPaquete(byte[] buf, int len, bool mirarSiAcepta, out bool aceptado)
    {
        aceptado = false;
        lock (_candado)
        {
            if (NativePtr is null) return (long)QuicheError.QUICHE_ERR_DONE;   // liberada

            quiche_recv_info info = new quiche_recv_info
            {
                to = (sockaddr*)_sockaddrLocal.Pointer,
                to_len = (size_t)_sockaddrLocalLen,

                from = (sockaddr*)_sockaddrRemoto.Pointer,
                from_len = (size_t)_sockaddrRemotoLen,
            };

            // Los paquetes que quiche cuenta como recibidos: suben solo con uno que aceptó. Solo cuando hace falta saberlo.
            quiche_stats antes = default;
            if (mirarSiAcepta) DisposicionUnix32.LeerStats(NativePtr, out antes);

            long resultado;
            fixed (byte* bufPtr = buf)
            {
                resultado = (long)NativePtr->Recv(bufPtr, (nuint)len, &info);
            }
            if (resultado >= 0) AnotarEntrada();
            if (mirarSiAcepta && resultado >= 0)
            {
                DisposicionUnix32.LeerStats(NativePtr, out quiche_stats despues);
                aceptado = despues.recv > antes.recv;
            }
            AnotarSiCerrada();
            return resultado;
        }
    }

    /// <summary>Lee de un flujo. Devuelve lo leído o el error, y si llegó el FIN.</summary>
    private unsafe long LeerDeFlujo(ulong streamId, byte[] buf, out bool fin)
    {
        bool finNativo = false;
        long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
        long recvCount = (long)QuicheError.QUICHE_ERR_DONE;   // liberada: nada que leer
        lock (_candado)
        {
            if (NativePtr is not null)
            {
                fixed (byte* bufPtr = buf)
                {
                    recvCount = (long)NativePtr->StreamRecv(streamId, bufPtr, (nuint)buf.Length,
                        &finNativo, (ulong*)&errorCode);
                }
            }
        }
        fin = finNativo;
        return recvCount;
    }

    /// <summary>
    /// Manda el FIN del flujo (posiblemente vacío) bajo el candado de la conexión. Falso si todavía no
    /// puede salir —un flujo vacío por encima del tope del par—, y entonces se reintenta.
    /// </summary>
    internal bool SendStreamFin(ulong streamId)
    {
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null)
                {
                    return true;   // liberada: no queda FIN que mandar
                }

                long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
                long resultOrError = (long)NativePtr->StreamSend(streamId,
                    null, 0, true,
                    (ulong*)Unsafe.AsPointer(ref errorCode)
                    );

                if (resultOrError == (long)QuicheError.QUICHE_ERR_STREAM_LIMIT)
                {
                    _flujosBloqueados = 1;   // ver EnviarParte
                    return false;
                }

                // FIN ya enviado (DONE) es normal; lo demás se propaga.
                if (resultOrError < 0 && resultOrError != (long)QuicheError.QUICHE_ERR_DONE)
                {
                    QuicheException.ThrowIfError((QuicheError)resultOrError);
                }
            }
        }

        SeñalizarEnvio();
        return true;
    }

    /// <summary>
    /// Corta un flujo a mitad, en los dos sentidos (RFC 9000 §19.4). Es la cancelación
    /// de una petición h3: no debe esperar a que el par termine de mandar su respuesta.
    /// </summary>
    internal void ResetStream(ulong streamId, ulong errorCode)
    {
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null) return;

                // Las dos direcciones, una a una. Se pasaba 3 «= READ|WRITE», pero la dirección
                // de quiche es un enum —0 lectura, 1 escritura—, no banderas: un 3 no es ninguno
                // de los dos, y lo que hiciera quiche con él no estaba definido. Cada mitad puede
                // estar ya cerrada (DONE, estado inválido): eso no impide cerrar la otra.
                int lectura   = NativePtr->StreamShutdown(streamId, 0, errorCode);
                int escritura = NativePtr->StreamShutdown(streamId, 1, errorCode);
                if (lectura < 0 && escritura < 0
                    && lectura != (int)QuicheError.QUICHE_ERR_DONE && escritura != (int)QuicheError.QUICHE_ERR_DONE)
                    QuicheException.ThrowIfError((QuicheError)escritura);
            }
        }

        SeñalizarEnvio();
    }

    /// <summary>
    /// Cierra la lectura de un flujo que ya no lee nadie (RFC 9000 §19.5): quiche tira lo que tenía de él y devuelve su
    /// crédito a la conexión, no guarda lo que llegue después —ni el flujo vuelve a darse por legible—, y manda STOP_SENDING
    /// si el par aún no había acabado. Con la conexión cerrándose no hace nada, y lo dice: el cierre ya lo dice todo al par.
    /// </summary>
    /// <returns>Si quedó cerrada: quiche no volverá a dar datos de ese flujo.</returns>
    internal bool DejarDeLeer(ulong streamId, ulong codigo = 0)
    {
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null || disposedValue || _cerrada) return false;

                // DONE —quiche ya lo soltó, o ya estaba cerrada— vale igual: no dará más datos de él.
                NativePtr->StreamShutdown(streamId, 0, codigo);
            }
        }

        SeñalizarEnvio();
        return true;
    }

    /// <summary>
    /// Si el par dejó de leer lo que se le manda por el flujo —su STOP_SENDING— o el flujo ya no existe. Es como se ve que
    /// cortó una petición cuyo cuerpo había acabado: con el FIN ya recibido, quiche no guarda su RESET_STREAM como algo que
    /// leer, y el h3 no da suceso. Ver <see cref="Cloudflare.QuicheNet.Internal.Quic.ConexionH3"/>.
    /// </summary>
    internal bool FlujoParadoPorElPar(ulong streamId)
    {
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null || disposedValue) return false;
                long r = NativePtr->StreamCapacity(streamId);
                return r == (long)QuicheError.QUICHE_ERR_STREAM_STOPPED || r == (long)QuicheError.QUICHE_ERR_INVALID_STREAM_STATE;
            }
        }
    }

    /// <summary>
    /// Cambia el destino de los envíos de la conexión (rebinding de NAT del otro
    /// lado). Solo se llama tras ver un paquete de ese extremo, que la conexión
    /// valida por CID y claves de todos modos. Es lo ÚNICO que se mueve: el
    /// <c>recv_info</c> que se pasa a quiche sigue clavado al extremo original, así
    /// que quiche nunca ve un cambio de camino ni dispara su validación de migración
    /// —que este wrapper no sabría honrar—.
    /// </summary>
    internal void Repuntar(EndPoint nuevo)
    {
        lock (_candado)
        {
            remoteEndPoint = nuevo;
        }
    }

    /// <summary>El CID que esta conexión dio al par: el DCID de lo que el par le manda.</summary>
    internal ReadOnlySpan<byte> Scid => connectionId;

    /// <summary>
    /// En el servidor, el DCID del primer Initial del cliente. Lo llevan su Initial y su 0-RTT
    /// mientras el cliente no conoce <see cref="Scid"/>. Lo anota el listener al aceptar.
    /// </summary>
    internal byte[]? DcidOriginal { get; set; }

    /// <summary>
    /// Lo que decidió el listener sobre el 0-RTT de esta conexión al crearla, con el registro contra la repetición
    /// (<see cref="Cloudflare.QuicheNet.Internal.Quic.AntiRepeticionCeroRtt"/>): 0, sin early data en el puerto; 1, se admite —su
    /// ClientHello quedó apuntado, o no lo pedía—; 2, se quita de cada datagrama antes de dárselo a quiche; 3, se admite sin
    /// registro, que está apagado (solo al medir).
    /// </summary>
    internal int CeroRttDecidido { get; set; }

    /// <summary>
    /// ¿Se puede atender ya lo que llegue en su 0-RTT? Solo si el registro contra la repetición apuntó su ClientHello: así
    /// esta es la única vez que se atiende ese early data, y una repetición no tiene ninguno. Sin él, lo pedido espera al
    /// Finished del cliente, que una repetición no manda nunca (RFC 8470 §5.1).
    /// </summary>
    internal bool CeroRttSinRepeticion => CeroRttDecidido == 1;

    /// <summary>
    /// ¿Va este DCID a esta conexión? Así la reparte el listener: por CID y no por el extremo
    /// del cliente, que puede cambiar (rebinding de NAT) o ser el de otra conexión del mismo
    /// cliente. Con ofuscador también vale: el listener desenvuelve el datagrama antes.
    /// </summary>
    internal bool EsSuDcid(ReadOnlySpan<byte> dcid)
        => dcid.SequenceEqual(connectionId) || (DcidOriginal is { } odcid && dcid.SequenceEqual(odcid));

    /// <summary>
    /// Sale un latido: se anota el silencio que lo precedió —en los dos sentidos—, para saber luego si el camino lo superó.
    /// Solo el primero sin respuesta: si ése no contesta, el camino murió tras su silencio, no tras el del siguiente. Solo en
    /// las que salen: el NAT que importa es el de este lado.
    /// </summary>
    private void AnotarLatido()
    {
        if (_claveNat is null) return;
        lock (_candado)
        {
            if (_latidoSinRespuesta != 0) return;
            long ahora = Environment.TickCount64;
            _silencioDelLatido  = ahora - Math.Max(Volatile.Read(ref _ultimaSalida), _ultimaEntrada);
            _latidoSinRespuesta = ahora;
        }
    }

    /// <summary>
    /// Un PING de aplicación (RFC 9000 §19.20 por la vía de quiche): no abre flujos,
    /// no consume el límite del par y mantiene caliente la ruta de vuelta del NAT.
    /// El par que no contesta en su plazo de inactividad cierra igualmente, así que
    /// la cadencia tiene que ir por debajo del idle timeout.
    /// </summary>
    internal void EnviarPing()
    {
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null) return;

                long r = NativePtr->SendAckEliciting();
                // DONE antes del saludo: no hay a quién hacerle ping todavía.
                if (r < 0 && r != (long)QuicheError.QUICHE_ERR_DONE)
                    QuicheException.ThrowIfError((QuicheError)r);
            }
        }

        SeñalizarEnvio();
    }

    // ── El cambio de red ─────────────────────────────────────────────────

    /// <summary>
    /// Las conexiones de salida vivas —las de todas las portadoras: quic, Hysteria 2, TUIC, QSOCKS, MASQUE, y las h3 del
    /// cliente HTTP y de xhttp—, para avisarlas del cambio de red. Entra cada una al nacer y sale al cerrarse o soltarse.
    /// </summary>
    private static readonly ConcurrentDictionary<QuicheConnection, byte> SalidasVivas = new();

    private static int _oyendoLaRed;

    /// <summary>Los avisos de cambio de red que ha recibido: su vigilante los mira para sondear. Ver <see cref="AvisarCambioDeRed"/>.</summary>
    private int _cambiosDeRed;

    /// <summary>Ver <see cref="_cambiosDeRed"/>.</summary>
    internal int CambiosDeRed => Volatile.Read(ref _cambiosDeRed);

    /// <summary>Las conexiones de salida vivas ahora mismo. Para las pruebas.</summary>
    internal static int SalidasApuntadas => SalidasVivas.Count;

    /// <summary>
    /// La apunta en <see cref="SalidasVivas"/>, y se desapunta sola al cerrarse. La primera suscribe el proceso al aviso de
    /// cambio de red, para siempre: es de todas las que vengan.
    /// </summary>
    private void ApuntarSalida()
    {
        SalidasVivas[this] = 0;
        _ = cerradaTcs.Task.ContinueWith(static (_, c) => SalidasVivas.TryRemove((QuicheConnection)c!, out byte _), this,
                                         CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (Volatile.Read(ref _oyendoLaRed) == 0 && Interlocked.Exchange(ref _oyendoLaRed, 1) == 0)
            Cloudflare.QuicheNet.Internal.CambioDeRed.Suscribir(AvisarALasSalidas);
    }

    /// <summary>
    /// El aviso de cambio de red: cada conexión de salida viva manda un PING ya. Ver <see cref="AvisarCambioDeRed"/>. En el
    /// hilo del aviso, y corto: un cerrojo y una llamada nativa por conexión, sin esperar a nada.
    /// </summary>
    private static void AvisarALasSalidas()
    {
        foreach (var (c, _) in SalidasVivas) c.AvisarCambioDeRed();
    }

    /// <summary>
    /// La red cambió: se manda un PING ya, y su vigilante sondea hasta que llegue la respuesta (ver
    /// <see cref="Cloudflare.QuicheNet.Internal.Quic.VigilanteDeCamino"/>).
    /// <para>
    /// Con la red nueva el NAT saca los paquetes por otra dirección, y el par sigue mandando a la de antes, que ya no lleva
    /// a ninguna parte. Hasta que no le llegue algo por la nueva no se entera, y una conexión que solo recibe —una bajada, o
    /// una ociosa— no manda nada que pida respuesta: esperaba a su latido, 10 s con los 30 s de inactividad de Hysteria 2,
    /// TUIC y MASQUE y 15 s en quic y QSOCKS, con la bajada parada todo ese rato. El PING cruza por la red nueva, el par
    /// repunta al recibirlo, y lo suyo sigue en un RTT más lo que tarde su PTO. Si la red nueva no lleva al par, el sondeo
    /// del vigilante lo ve en su silencio mínimo, y la portadora se retira antes de que una sesión entre en ella.
    /// </para>
    /// </summary>
    internal void AvisarCambioDeRed()
    {
        if (IsClosed || Volatile.Read(ref disposedValue) || !Establecido) return;
        Interlocked.Increment(ref _cambiosDeRed);
        try { EnviarPing(); }
        catch (QuicheException) { }        // cerrándose
        catch (ObjectDisposedException) { }
        AvisarDeActividad();   // el vigilante dormido en reposo lo ve ya, aunque el PING no llegara a salir
    }

    /// <summary>
    /// El techo de la cadencia automática del PING: 15 s. Muchos NAT —routers de casa, redes móviles— olvidan a los 30 s una
    /// asociación UDP callada, y desde entonces lo que manda el par se pierde sin aviso (RFC 9308 §3.2). Con la cadencia en
    /// un tercio de la inactividad —100 s con los 5 min de quic y QSOCKS—, una conexión guardada para reutilizarla se quedaba
    /// sorda mucho antes de que la poda de portadores la soltara, y la sesión siguiente esperaba al vigilante del camino.
    /// </summary>
    internal const long TopeDelLatidoMs = Cloudflare.QuicheNet.Internal.PlazosDeNat.TechoMs;

    /// <summary>
    /// La cadencia del PING, en ms, o 0 sin él. La explícita va tal cual. La automática es un tercio de la menor de las dos
    /// inactividades —cero en un lado es «sin plazo», y vale la del otro—, entre 1 s y <see cref="TopeDelLatidoMs"/>; y sin
    /// plazo en ninguno de los dos, el techo, porque el NAT sí lo tiene.
    /// </summary>
    internal static long CadenciaDelLatidoMs(bool auto, TimeSpan explicita, long propiaMs, ulong delParMs)
    {
        if (!auto) return explicita > TimeSpan.Zero ? Math.Max(1, (long)explicita.TotalMilliseconds) : 0;

        ulong propia = (ulong)Math.Max(0, propiaMs);
        ulong menor = delParMs == 0 ? propia : propia == 0 ? delParMs : Math.Min(delParMs, propia);
        return Cloudflare.QuicheNet.Internal.PlazosDeNat.CadenciaPrudenteMs((long)Math.Min(menor, (ulong)long.MaxValue));
    }

    /// <summary>
    /// Pone el latido en el planificador. La cadencia automática espera al saludo, que es cuando se sabe la inactividad del
    /// par; una conexión que se cierra antes no late.
    /// </summary>
    private void ArrancarLatido()
    {
        if (!_keepAliveAuto)
        {
            ProgramarLatido(CadenciaDelLatidoMs(false, _keepAliveInterval, 0, 0));
            return;
        }

        _ = ConnectionEstablishedTask.ContinueWith(
            static (saludo, estado) =>
            {
                var c = (QuicheConnection)estado!;
                if (!saludo.IsCompletedSuccessfully || c.IsClosed) return;
                c.ProgramarLatido(CadenciaDelLatidoMs(true, TimeSpan.Zero, c._inactividadLocalMs,
                                                      c.ParametrosDelPar().peer_max_idle_timeout));
            },
            this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void ProgramarLatido(long cadenciaMs)
    {
        if (cadenciaMs <= 0) return;

        var latido = new Latido(this, cadenciaMs);
        lock (_candado)
        {
            if (disposedValue) return;
            _latido = latido;
        }

        // Un Dispose entre medias lo anula antes de que entre, y entra igual: al vencer ve la conexión cerrada y no vuelve.
        // La primera cita, ya con la cadencia de su destino: con la prudente, un NAT que olvida antes la dejaba sorda en
        // su primer silencio.
        Cloudflare.QuicheNet.Internal.PlanificadorDeCitas.Compartido.Programar(latido, Environment.TickCount64 + latido.Cadencia());
    }

    /// <summary>
    /// El PING de aplicación de una conexión, como cita del planificador compartido. Era una tarea por conexión con su
    /// <c>Task.Delay</c>, que mandaba el PING a su hora aunque la conexión estuviera mandando datos: late solo tras una
    /// cadencia entera sin que salga nada —cualquier paquete que sale refresca el NAT, y los que el par contesta, su
    /// inactividad—. El PING se manda en el pool: el bucle del planificador es de todos.
    /// </summary>
    private sealed class Latido : Cloudflare.QuicheNet.Internal.CitaConEncargo
    {
        private const int Latir = 1;

        private readonly QuicheConnection _conexion;
        private readonly long _cadenciaMs;

        public Latido(QuicheConnection conexion, long cadenciaMs)
        {
            _conexion   = conexion;
            _cadenciaMs = cadenciaMs;
        }

        /// <summary>
        /// La de este destino, que puede haber bajado desde la última vez: la mitad del silencio que mató otro camino hacia
        /// él, si es menos que la suya. Ver PlazosDeNat.
        /// </summary>
        public long Cadencia() => _conexion._claveNat is { } clave
            ? Cloudflare.QuicheNet.Internal.PlazosDeNat.Proceso.CadenciaMs(clave, _cadenciaMs)
            : _cadenciaMs;

        protected internal override long Vencer(long ahora)
        {
            if (_conexion.IsClosed || Volatile.Read(ref _conexion.disposedValue)) return long.MaxValue;

            long cadencia = Cadencia();
            long callada = ahora - Volatile.Read(ref _conexion._ultimaSalida);
            if (callada < cadencia) return ahora + (cadencia - callada);

            Encargar(Latir);
            return ahora + cadencia;
        }

        protected override void Hacer(int encargo)
        {
            try
            {
                _conexion.AnotarLatido();
                _conexion.EnviarPing();
                Interlocked.Increment(ref _conexion.LatidosEnviados);
            }
            catch (QuicheException) { }        // cerrándose: el próximo vencimiento lo ve y no vuelve
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Fija el ritmo de Brutal, en bytes por segundo, para todos los caminos de la conexión;
    /// 0 la devuelve a BBRv2. Solo vale si la configuración eligió «brutal»: con otro control
    /// de congestión lanza <see cref="QuicheException"/> con QUICHE_ERR_CONGESTION_CONTROL.
    /// </summary>
    internal void EstablecerRitmoBrutal(ulong bytesPorSegundo)
    {
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null)
                    throw new ObjectDisposedException(nameof(QuicheConnection));

                QuicheException.ThrowIfError((QuicheError)NativePtr->SetBrutalRate(bytesPorSegundo),
                    "quiche no aceptó el ritmo de Brutal.");
            }
        }

        // Con más ritmo puede salir ya lo que la ventana retenía.
        SeñalizarEnvio();
    }

    /// <summary>
    /// Material de claves exportado del TLS de la conexión (RFC 5705); de aquí sale el token
    /// de TUIC v5. Solo con el saludo completo: antes lanza con QUICHE_ERR_INVALID_STATE. La
    /// etiqueta va en bytes crudos y no tiene por qué ser texto.
    /// </summary>
    internal void ExportarMaterialDeClaves(ReadOnlySpan<byte> etiqueta, ReadOnlySpan<byte> contexto,
                                           Span<byte> salida)
    {
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null)
                    throw new ObjectDisposedException(nameof(QuicheConnection));

                fixed (byte* etiquetaPtr = etiqueta)
                fixed (byte* contextoPtr = contexto)
                fixed (byte* salidaPtr = salida)
                {
                    QuicheException.ThrowIfError((QuicheError)NativePtr->ExportKeyingMaterial(
                            etiquetaPtr, (nuint)etiqueta.Length, contextoPtr, (nuint)contexto.Length,
                            salidaPtr, (nuint)salida.Length),
                        "quiche no pudo exportar el material de claves.");
                }
            }
        }
    }

    /// <summary>
    /// El certificado hoja del par, en DER, o null si no presentó ninguno. Es una copia: el búfer
    /// de quiche vive lo que la conexión. Sin <c>verify_peer</c> también está, porque el saludo lo
    /// recibe igual y comprueba con su clave la firma del par.
    /// </summary>
    internal byte[]? CertificadoDelPar()
    {
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null)
                    throw new ObjectDisposedException(nameof(QuicheConnection));

                byte* datos = null;
                nuint largo = 0;
                NativePtr->PeerCert(&datos, &largo);
                return datos is null || largo == 0 ? null : new ReadOnlySpan<byte>(datos, checked((int)largo)).ToArray();
            }
        }
    }

    /// <summary>¿Está en 0-RTT: el cliente mandando early data, o el servidor aceptándolo?</summary>
    internal bool EnEarlyData
    {
        get
        {
            unsafe
            {
                lock (_candado)
                {
                    return NativePtr is not null && NativePtr->IsInEarlyData();
                }
            }
        }
    }

    /// <summary>Si el saludo reanudó una sesión TLS anterior en vez de hacerse entero.</summary>
    internal bool Reanudada
    {
        get
        {
            unsafe
            {
                lock (_candado)
                {
                    return NativePtr is not null && NativePtr->IsResumed();
                }
            }
        }
    }

    /// <summary>
    /// Entrega la sesión TLS a quien la guarda, si el servidor ya mandó su ticket. Se mira en
    /// unas pocas tandas tras el saludo, para no mirar siempre en un servidor sin tickets.
    /// </summary>
    private void EntregarSesionSiHay()
    {
        byte[]? sesion = null;
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null) return;

                byte* datos = null;
                nuint largo = 0;
                NativePtr->Session(&datos, &largo);
                if (datos is not null && largo > 0)
                {
                    sesion = new ReadOnlySpan<byte>(datos, (int)largo).ToArray();
                }
            }
        }

        if (sesion is null)
        {
            if (++_busquedasDeSesion >= 64) _alRecibirSesion = null;
            return;
        }

        Action<byte[]>? entregar = Interlocked.Exchange(ref _alRecibirSesion, null);
        try { entregar?.Invoke(sesion); } catch { }
    }

    /// <summary>
    /// ¿La conexión ya puede mandar datos de flujo? El escritor lo pregunta para
    /// enviar en línea —sin esperar el despertar de la bomba—, que es el camino de
    /// todos los días; antes del saludo se encola y la bomba lo manda al establecerse.
    /// </summary>
    internal bool Establecido
    {
        get
        {
            // Establecida una vez, lo sigue hasta el cierre: se mira sin cerrojo ni P/Invoke. Se preguntaba en CADA escritura
            // de un flujo, y cada pregunta era el cerrojo de la conexión —el mismo que las bombas— y dos llamadas al motor.
            // Una lectura a destiempo con la conexión ya cerrándose la ve quien escribe después, bajo el cerrojo, sin motor.
            if (_establecidaVista && !disposedValue) return true;
            unsafe
            {
                lock (_candado)
                {
                    if (NativePtr is null) return false;
                    if (NativePtr->IsEstablished()) { _establecidaVista = true; return true; }
                    return NativePtr->IsInEarlyData();
                }
            }
        }
    }

    /// <summary>Ya se vio establecida: ver <see cref="Establecido"/>.</summary>
    private volatile bool _establecidaVista;

    /// <summary>
    /// Manda un trozo de flujo y devuelve lo aceptado. 0 o DONE = ventana llena
    /// o nada que hacer —el resto espera—; los errores de verdad lanzan.
    /// </summary>
    /// <param name="fin">Dónde acaban los datos en <paramref name="buf"/>: un búfer arrendado puede ser mayor.</param>
    internal long EnviarParte(ulong streamId, byte[] buf, int offset, int fin)
    {
        long resultOrError;
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null)
                {
                    return 0;   // cerrada entre el chequeo del escritor y este envío
                }

                fixed (byte* bufPtr = buf)
                {
                    long errorCode = (long)QuicheError.QUICHE_ERR_NONE;
                    resultOrError = (long)NativePtr->StreamSend(streamId,
                        bufPtr + offset, (nuint)(fin - offset),
                        false,
                        (ulong*)Unsafe.AsPointer(ref errorCode)
                        );
                }

                // Sin sitio: se reintenta cuando lleguen ventanas nuevas. Ver _flujosBloqueados.
                if (resultOrError is 0 or (long)QuicheError.QUICHE_ERR_DONE or (long)QuicheError.QUICHE_ERR_STREAM_LIMIT)
                    _flujosBloqueados = 1;
            }
        }

        if (resultOrError > 0)
        {
            SeñalizarEnvio();   // quiche tiene paquetes que mandar
            return resultOrError;
        }
        if (resultOrError is (long)QuicheError.QUICHE_ERR_DONE or (long)QuicheError.QUICHE_ERR_STREAM_LIMIT)
        {
            // Sin progreso, no es error. El tope de flujos del par tampoco: el flujo espera a que llegue
            // su MAX_STREAMS. Antes lanzaba, y en la bomba eso paraba el envío de toda la conexión.
            return 0;
        }
        QuicheException.ThrowIfError((QuicheError)resultOrError);
        return 0;
    }

    /// <summary>
    /// ¿Es un error de un flujo y no de la conexión? El par lo paró (STOP_SENDING) o quiche lo da por
    /// roto. Lo trata el propio flujo; la conexión y los demás flujos siguen.
    /// </summary>
    internal static bool EsErrorDeFlujo(QuicheError codigo) => codigo is QuicheError.QUICHE_ERR_STREAM_STOPPED
        or QuicheError.QUICHE_ERR_STREAM_RESET or QuicheError.QUICHE_ERR_INVALID_STREAM_STATE
        or QuicheError.QUICHE_ERR_FINAL_SIZE;

    /// <summary>
    /// Suelta del mapa un flujo que ya no tiene nada que hacer en quiche. Sin esto el mapa solo crecía:
    /// con un flujo por paquete —el UDP de TUIC en modo «quic»— o miles de sesiones en una conexión
    /// larga, cada despertar de la bomba de envío recorría todos los flujos de la historia de la
    /// conexión, con un cerrojo por flujo. Solo sale ESE objeto: los identificadores no se reutilizan.
    /// </summary>
    internal void OlvidarSiAcabado(QuicheStream flujo)
    {
        if (flujo.Acabado)
        {
            streamMap.TryRemove(new KeyValuePair<ulong, QuicheStream>(flujo.Id, flujo));
        }
    }

    private bool IsStreamFinished(ulong streamId)
    {
        unsafe
        {
            lock (_candado)
            {
                return NativePtr is null || NativePtr->StreamFinished(streamId);
            }
        }
    }

    /// <summary>
    /// Siguiente flujo de salida de cada tipo, <b>monótono por conexión</b>. Antes el
    /// contador era local de <see cref="CreateOutboundStreamAsync"/>: cada llamada volvía
    /// al 0, y como <see cref="streamMap"/> entonces no soltaba un flujo cerrado, el nuevo salía
    /// con el identificador de uno muerto —el lector veía su EOF, las respuestas caían en
    /// un canal completado y el servidor ni se enteraba—. Las sesiones siguientes del
    /// pool templado nacían rotas sin que nada lo dijera. Atómico: el rellenado del
    /// pool abre flujos en paralelo.
    /// <para>
    /// Hay un contador por tipo. El límite del par cuenta la secuencia dentro de cada
    /// tipo (<c>id &gt;&gt; 2</c>), y quiche da por abiertos todos los números por debajo
    /// del más alto usado. Con un contador compartido, cada flujo uni dejaba un hueco en
    /// los bidi: alternando los dos tipos, se gastaba el doble de cupo del que se usaba.
    /// </para>
    /// </summary>
    private long _siguienteBidi;
    private long _siguienteUni;

    public Task<QuicheStream> CreateOutboundStreamAsync(Direction direction, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long secuencia = direction == Direction.Unidirectional
            ? Interlocked.Increment(ref _siguienteUni) - 1
            : Interlocked.Increment(ref _siguienteBidi) - 1;
        ulong streamId = ((ulong)secuencia << 2) | (ulong)direction | Convert.ToUInt64(IsServer);
        return Task.FromResult(GetStream(streamId));
    }

    public async Task<QuicheStream> AcceptInboundStreamAsync(CancellationToken cancellationToken = default)
    {
        return await streamChannel.Reader.ReadAsync(cancellationToken);
    }

    public bool TryReceiveDatagram([NotNullWhen(true)] out byte[]? dgramBuf)
    {
        if (ColaDeDatagramas().TryLeer(out var d))
        {
            dgramBuf = d.Soltar();
            return true;
        }
        dgramBuf = null;
        return false;
    }

    /// <summary>
    /// El siguiente datagrama recibido. Cuando la conexión se acaba y no queda ninguno por
    /// leer, lanza <see cref="QuicheException"/> con DONE.
    /// </summary>
    public Task<byte[]> ReceiveDatagramAsync(CancellationToken cancellationToken = default)
    {
        var cola = ColaDeDatagramas();
        return cola.TryLeer(out var listo)
            ? Task.FromResult(listo.Soltar())
            : EsperarDatagramaAsync(cola, cancellationToken).AsTask();
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<byte[]> EsperarDatagramaAsync(Cloudflare.QuicheNet.Internal.ColaAcotada<DatagramaQuic> cola,
                                                                 CancellationToken ct)
        => (await EsperarPrestadoAsync(cola, ct).ConfigureAwait(false)).Soltar();

    /// <summary>
    /// El siguiente datagrama, en su búfer prestado: quien lo lee lo devuelve con <see cref="DatagramaQuic.Devolver"/>. Como
    /// <see cref="ReceiveDatagramAsync"/>, lanza con DONE cuando la conexión se acaba.
    /// </summary>
    internal ValueTask<DatagramaQuic> RecibirDatagramaPrestadoAsync(CancellationToken ct = default)
    {
        var cola = ColaDeDatagramas();
        return cola.TryLeer(out var listo)
            ? new ValueTask<DatagramaQuic>(listo)
            : EsperarPrestadoAsync(cola, ct);
    }

    /// <summary>Hasta que llegue uno. La máquina de la espera sale del pool: era un objeto por cada datagrama esperado.</summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<DatagramaQuic> EsperarPrestadoAsync(Cloudflare.QuicheNet.Internal.ColaAcotada<DatagramaQuic> cola,
                                                                       CancellationToken ct)
    {
        while (true)
        {
            if (!await cola.EsperarAsync(ct).ConfigureAwait(false))
                throw new QuicheException(QuicheError.QUICHE_ERR_DONE, "La conexión se cerró.");
            if (cola.TryLeer(out var datagrama)) return datagrama;
        }
    }

    private Cloudflare.QuicheNet.Internal.ColaAcotada<DatagramaQuic> ColaDeDatagramas() => datagramasRecibidos
        ?? throw new QuicheException(QuicheError.QUICHE_ERR_INVALID_FRAME,
            "DATAGRAM frames are not enabled for this connection.");

    /// <summary>
    /// Mete un datagrama en quiche y avisa a la bomba de envío. No hay cola ni bomba propias,
    /// y nunca espera: si la cola está llena o el datagrama no cabe, lo dice el resultado.
    /// </summary>
    internal ResultadoDatagrama EnviarDatagrama(ReadOnlySpan<byte> datagrama) => EnviarDatagrama(datagrama, datagrama.Length);

    /// <summary>
    /// Lo mismo, haciendo en la cola de DATAGRAM sitio para <paramref name="sitio"/> bytes: el primer trozo de un mensaje
    /// partido pide el sitio del mensaje entero, y los trozos que siguen entran con 0, sin purgar.
    /// <para>
    /// La purga tira de lo viejo —lo que ya esperaba antes— y nunca de lo que entra: era una purga por trozo, y en cuanto la
    /// cola pasaba de su tope —32 KB, o lo que lleva el camino en un RTT— tiraba los primeros trozos del propio mensaje, que
    /// se perdía entero: los 60 KB de un UDP grande no llegaban nunca, ni con la conexión recién abierta. Así la cola puede
    /// pasarse de su tope en un mensaje, como mucho, y lo que tira sigue siendo lo que lleva un RTT esperando.
    /// </para>
    /// </summary>
    internal ResultadoDatagrama EnviarDatagrama(ReadOnlySpan<byte> datagrama, int sitio)
    {
        ColaDeDatagramas();   // lanza si la config no los activó

        // Lo que no cabe por el camino de ahora tampoco se admite, aunque quepa en lo que arma quiche: se quedaría en la cabeza
        // de su cola. Ver MaxDatagramSize. Quien no lo puede trocear lo dice aparte: ver PedirSitioPara.
        int limite = LimiteDeAhora();

        long resultado;
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null || disposedValue) return ResultadoDatagrama.Cerrada;

                if (limite > 0 && datagrama.Length > (long)NativePtr->DgramMaxWritableLen()
                                                     - Math.Max(0, (long)NativePtr->MaxSendUdpPayloadSize() - limite))
                    return ResultadoDatagrama.Grande;

                // La cola acotada por bytes: con ella por encima de lo que el camino lleva en un RTT, se tiran los más
                // VIEJOS hasta dejarla en tres cuartos con lo que entra, y lo nuevo entra. Ver TopeColaDgram.
                if (sitio > 0)
                {
                    long tope = TopeColaDgram();
                    long encolados = (long)NativePtr->DgramSendQueueByteSize();
                    if (encolados + sitio > tope)
                    {
                        _porPurgar = Math.Min(encolados, encolados + sitio - tope * 3 / 4);
                        NativePtr->DgramPurgeOutgoing(
                            (delegate* unmanaged[Cdecl]<byte*, nuint, bool>)(delegate* unmanaged[Cdecl]<byte*, nuint, byte>)&PurgarViejo);
                        _porPurgar = 0;
                    }
                }

                // quiche no admite un puntero nulo ni con largo 0, y el vacío es válido.
                byte relleno = 0;
                fixed (byte* p = datagrama)
                {
                    resultado = NativePtr->DgramSend(p is null ? &relleno : p, (nuint)datagrama.Length);
                }
            }
        }

        if (resultado >= 0)
        {
            SeñalizarEnvio();
            return ResultadoDatagrama.Encolado;
        }

        return (QuicheError)resultado switch
        {
            QuicheError.QUICHE_ERR_DONE => ResultadoDatagrama.ColaLlena,
            QuicheError.QUICHE_ERR_BUFFER_TOO_SHORT => ResultadoDatagrama.Grande,
            _ when IsClosed => ResultadoDatagrama.Cerrada,
            QuicheError.QUICHE_ERR_INVALID_STATE => ResultadoDatagrama.SinSoporte,
            _ => throw new QuicheException((QuicheError)resultado, "quiche no aceptó el datagrama."),
        };
    }

    /// <summary>
    /// Lo más que puede esperar en la cola de DATAGRAM de quiche, en bytes: la ventana de congestión del camino —lo que
    /// sale en un RTT—, y <see cref="TopeColaDgramMinimo"/> como poco. La cola de quiche se cuenta en datagramas (1024), y
    /// con el enlace lleno eran 1,2 MB delante de cada uno: a 20 Mbit/s, medio segundo de espera para el eco de quien
    /// llevara voz, un juego o DNS por el mismo UDP. Un datagrama no se reenvía: el que espera tanto ya no sirve, y es
    /// mejor tirarlo que entregarlo tarde.
    /// <para>
    /// La ventana se lee de las estadísticas del camino, que se copian enteras: se relee como mucho cada
    /// <see cref="RelecturaCwndMs"/> ms y no por datagrama. Llamar con <c>_candado</c> tomado.
    /// </para>
    /// </summary>
    private unsafe long TopeColaDgram()
    {
        long ahora = Environment.TickCount64;
        if (ahora - _cwndLeidaEn >= RelecturaCwndMs)
        {
            _cwndLeidaEn = ahora;
            if (DisposicionUnix32.LeerPathStats(NativePtr, 0, out quiche_path_stats stats) == 0) _cwnd = (long)stats.cwnd;
        }
        return Math.Max(_cwnd, TopeColaDgramMinimo);
    }

    /// <summary>
    /// Lo que aún hay que sacar de la cola de DATAGRAM en la purga en curso. quiche recorre la cola de la cabeza —lo más
    /// viejo— a la cola y tira lo que se le diga, así que se dice que sí hasta llegar a la cuenta. Es del hilo: la purga
    /// va con <c>_candado</c> tomado y la llamada de vuelta, en el mismo hilo.
    /// </summary>
    [ThreadStatic] private static long _porPurgar;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte PurgarViejo(byte* datos, nuint largo)
    {
        if (_porPurgar <= 0) return 0;
        _porPurgar -= (long)largo;
        return 1;
    }

    /// <summary>
    /// El tope de la cola por bytes, como poco. Al pasarlo se tiran los viejos y no el nuevo: un datagrama que ya esperó un
    /// RTT entero vale menos que el que acaba de llegar —el eco de un juego, una consulta DNS—, que es justo el que tiraría
    /// un tope que rechazara lo que llega.
    /// </summary>
    private const long TopeColaDgramMinimo = 32 * 1024;
    private const long RelecturaCwndMs = 10;
    private long _cwnd, _cwndLeidaEn = long.MinValue / 2;

    public bool TrySendDatagram(byte[] dgramBuf) => EnviarDatagrama(dgramBuf) == ResultadoDatagrama.Encolado;

    /// <summary>
    /// Como <see cref="EnviarDatagrama(ReadOnlySpan{byte})"/>, pero con la cola llena espera a que la bomba de
    /// envío la vacíe. Lanza si la conexión se cerró, si el par no admite datagramas o si
    /// el datagrama no cabe.
    /// </summary>
    public Task SendDatagramAsync(byte[] dgramBuf, CancellationToken cancellationToken = default)
        => SendDatagramAsync((ReadOnlyMemory<byte>)dgramBuf, cancellationToken);

    public async Task SendDatagramAsync(ReadOnlyMemory<byte> dgramBuf, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            // Se apunta a la espera ANTES de intentarlo: si la bomba vacía la cola entre el
            // intento y la espera, el aviso no se pierde.
            Task hueco = EsperarHuecoDgram();
            switch (EnviarDatagrama(dgramBuf.Span))
            {
                case ResultadoDatagrama.Encolado:
                    return;
                case ResultadoDatagrama.ColaLlena:
                    break;
                case ResultadoDatagrama.Grande:
                    throw new ArgumentException(
                        $"El datagrama no cabe; el máximo de ahora lo da {nameof(MaxDatagramSize)}.", nameof(dgramBuf));
                case ResultadoDatagrama.SinSoporte:
                    throw new QuicheException(QuicheError.QUICHE_ERR_INVALID_STATE, "El par no admite datagramas.");
                default:
                    throw new QuicheException(QuicheError.QUICHE_ERR_DONE, "La conexión se cerró.");
            }

            // El plazo solo cubre un aviso perdido; lo normal es despertar con el vaciado.
            try
            {
                await hueco.WaitAsync(TimeSpan.FromMilliseconds(30), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    private Task EsperarHuecoDgram()
    {
        TaskCompletionSource? actual = Volatile.Read(ref _huecoDgram);
        if (actual is not null) return actual.Task;

        var nueva = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (Interlocked.CompareExchange(ref _huecoDgram, nueva, null) ?? nueva).Task;
    }

    /// <summary>Espera a que la conexión se cierre. Sondeaba cada <c>SONDEO_MS</c>; ahora espera al aviso del cierre.</summary>
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
        => IsClosed ? Task.CompletedTask : Cerrada.WaitAsync(cancellationToken);

    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        Latido? latido;
        lock (_candado)
        {
            if (disposedValue) return;
            disposedValue = true;
            latido = _latido;
        }
        if (latido is not null) Cloudflare.QuicheNet.Internal.PlanificadorDeCitas.Compartido.Anular(latido);
        if (!IsServer) SalidasVivas.TryRemove(this, out _);

        // Su cita del despertador, fuera: no la retiene hasta su hora.
        DespertadorPreciso.Anular(_citaDelTemporizador);
        DespertadorPreciso.Anular(_citaDelAck);
        DespertadorPreciso.Anular(_citaDeLaSonda);

        if (disposing)
        {
            foreach (var (_, stream) in streamMap)
            {
                stream.Dispose();
            }

            // Lo que quede por mandar, con las bombas aún vivas: ver EsperarAQueSalga. Por un camino mudo, no: ver
            // CerrarSinEspera.
            if (!_sinGracia) EsperarAQueSalga();
            _sinRecepcion = true;

            // Las bombas, fuera antes de cerrar —la de envío suelta su lote al cortarse—, y se espera a TODAS antes de tocar
            // el motor. Lo que quede sale a mano: lo encolado en los flujos pasa a quiche, y lo que quiche ya aceptó sale antes
            // del cierre. Tras Close, quiche ya no mete marcos de flujo en ningún paquete: se cerraba primero, y lo aceptado y
            // aún sin salir se perdía —las cabeceras y el cuerpo de una respuesta h3, el rechazo de QSOCKS, el FIN de un flujo
            // con dueña—. Era el fallo suelto de «clientehttp h3 fijo».
            cts.Cancel();
            Cloudflare.QuicheNet.Internal.PlanificadorDeCitas.Compartido.Anular(_guardia);
            utilizableTcs.TrySetCanceled();
            bool soloEsta = EsperarBombas();

            try
            {
                // Con otra bomba aún dentro de quiche —colgada—, no se toca: el cierre de siempre y nada más.
                if (soloEsta && !_sinGracia) VaciarAntesDeCerrar();

                unsafe
                {
                    lock (_candado)
                    {
                        if (NativePtr is null) throw new QuicheException(QuicheError.QUICHE_ERR_DONE, "Sin motor.");
                        byte[] reasonBuf = Encoding.UTF8.GetBytes("Connection was implicitly closed for user initiated disposal.");
                        fixed (byte* reasonPtr = reasonBuf)
                        {
                            int errorResult = NativePtr->Close(true, 0x00, reasonPtr, (size_t)reasonBuf.Length);
                            AnotarSiCerrada();
                            QuicheException.ThrowIfError((QuicheError)errorResult, "Failed to close connection!");
                        }
                    }
                }

                // El cierre del quiche nuevo es asíncrono: Close solo lo anota y el marco
                // de cierre sale con el siguiente Send —y la conexión no se da por cerrada
                // hasta que su temporizador de drenaje expira, algo que no se puede
                // esperar aquí—. Se vacía a mano, acotado, para que el par vea el cierre. Con sitio para lo que cabe ahora
                // por el camino: lo que no, no se arma.
                byte[] buf = new byte[PaqueteQueCabeAhora() + _hueco];
                int hueco = _hueco;
                quiche_send_info sendInfo = default;
                for (int i = 0; i < 8; i++)
                {
                    long n;
                    unsafe
                    {
                        lock (_candado)
                        {
                            fixed (byte* pktPtr = buf)
                            {
                                n = (long)NativePtr->Send(
                                    pktPtr + hueco, (nuint)(buf.Length - hueco),
                                    (quiche_send_info*)Unsafe.AsPointer(ref sendInfo));
                            }
                            AnotarSiCerrada();
                        }
                    }

                    if (n <= 0) break;   // DONE o nada que mandar: el cierre ya salió

                    // El drenaje de cierre también va ofuscado como cualquier paquete.
                    int largo = hueco + (int)n;
                    _ofuscador?.Codificar(buf.AsSpan(0, largo));

                    try
                    {
                        canal.Enviar(buf.AsSpan(0, largo), remoteEndPoint);
                    }
                    catch
                    {
                        break;
                    }
                }

            }
            catch (QuicheException)
            {
                // Ya estaba cerrada, o quiche no aceptó el cierre: no queda nada que mandar, y
                // el motor se suelta igual.
            }
            finally
            {
                cts.Dispose();

                // Lo que quedó sin procesar vuelve al pool; las bombas ya salieron.
                while (Sacar(out var restante))
                {
                    Devolver(restante.Buf);
                }

                // Los lectores de sus flujos ven EOF. Las bombas salen por la cancelación y no pasan por
                // el cierre que lo hacía, así que sin esto un relevo con el destino callado se quedaba
                // leyendo un flujo muerto hasta su inactividad.
                CompletarFlujos();

                // Y quien espera el cierre se entera: el h3 de encima, WaitForShutdownAsync.
                _cerrada = true;
                cerradaTcs.TrySetResult();

                streamMap.Clear();
                streamChannel.Writer.TryComplete();

                datagramasRecibidos?.Completar();
                Interlocked.Exchange(ref _huecoDgram, null)?.TrySetResult();

                // Los semáforos no se disponen. Sin AvailableWaitHandle no guardan nada del
                // sistema, y un escritor que llegue tarde haría Release sobre uno dispuesto:
                // una ObjectDisposedException en su hilo.
            }
        }

        // Con una bomba que no salió, el motor y las direcciones se quedan sin soltar: una fuga acotada —y contada—
        // antes que liberar memoria nativa que esa bomba todavía puede estar usando.
        if (_bombasColgadas) return;

        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is not null)
                {
                    NativePtr->Free();
                    NativePtr = null;
                    _cerrada = true;   // sin motor es cerrada, como antes
                }
            }
        }

        // Las direcciones fijadas se sueltan aquí, con las bombas ya fuera.
        _sockaddrLocal.Dispose();
        _sockaddrRemoto.Dispose();
    }

    /// <summary>Lo que se espera, entre todas, a que salgan las bombas al disponer.</summary>
    private static readonly TimeSpan PlazoDeBombas = TimeSpan.FromSeconds(10);

    /// <summary>Alguna bomba no salió en <see cref="PlazoDeBombas"/>: el motor no se libera.</summary>
    private bool _bombasColgadas;

    /// <summary>
    /// Espera a que termine cada bomba, una a una. Acabar cancelada, o por el cierre de la
    /// conexión, es lo normal, y no impide esperar a las demás.
    /// <para>
    /// Con plazo y sin esperarse a sí misma. Antes era <c>Wait()</c> a secas: un Dispose que corriera en el hilo de
    /// una bomba se esperaba para siempre, y una bomba atascada dentro de quiche colgaba a quien cerraba. Ahora la
    /// propia se salta —sale en cuanto vuelva de aquí, por la cancelación— y al vencer el plazo se sigue sin liberar
    /// el motor.
    /// </para>
    /// </summary>
    /// <summary>Mientras dura un cierre con gracia: la bomba de envío mira entonces lo que le queda en vuelo. Ver <see cref="EsperarAQueSalga"/>.</summary>
    private volatile bool _enCierre;

    /// <summary>
    /// Durante un cierre, las vueltas de la bomba de envío que acabaron sin nada más que mandar y con lo mandado ya confirmado.
    /// Se mira con el cerrojo de la generación: entre el DONE y la cuenta no se cuela el ACK de nadie.
    /// </summary>
    private int _vueltasVacias;

    /// <summary>Durante un cierre, las vueltas de la bomba de envío que acabaron sin nada más que mandar, confirmado o no.</summary>
    private int _vueltasDeCierre;

    /// <summary>
    /// Cuándo salió lo último con datos, en el reloj de <see cref="System.Diagnostics.Stopwatch"/>: un lote cuyos paquetes pesan de media más de
    /// 64 bytes. Los de solo ACK pesan unos 45-50 y no cuentan. Ver <see cref="EsperarAQueSalga"/>.
    /// </summary>
    private long _ultimoEnvioDeDatos;

    /// <summary>Lo más que un cierre espera a que salga lo pendiente, con las bombas vivas.</summary>
    internal static TimeSpan PlazoDeGracia = TimeSpan.FromSeconds(1);

    /// <summary>Cerrar sin el cierre con gracia: ver <see cref="CerrarSinEspera"/>.</summary>
    private volatile bool _sinGracia;

    /// <summary>
    /// Cierra y suelta la conexión sin esperar a que salga lo pendiente: para la de un cliente cuyo camino enmudeció
    /// (<see cref="Cloudflare.QuicheNet.Internal.Quic.VigilanteDeCamino.DeEntrada"/>), donde nada va a tener respuesta. El cierre con
    /// gracia esperaba entonces su plazo entero con el hilo tomado —con muchos clientes callando a la vez, un hilo del pool
    /// por cada uno—, y vaciaba hacia nadie lo encolado en los flujos. El CONNECTION_CLOSE sale igual, por si el par aún
    /// escucha.
    /// </summary>
    internal void CerrarSinEspera()
    {
        _sinGracia = true;
        Dispose();
    }

    /// <summary>
    /// Cierra la conexión con un error de la aplicación —el de HTTP/3 que toque (RFC 9114 §8.1)— y su motivo, sin soltarla:
    /// el CONNECTION_CLOSE sale con la próxima vuelta de la bomba de envío, y desde ahí la conexión drena. La suelta quien
    /// la lleva, como siempre. Si ya estaba cerrándose, no hace nada.
    /// </summary>
    internal void CerrarConError(ulong codigo, string motivo)
    {
        unsafe
        {
            lock (_candado)
            {
                if (NativePtr is null) return;
                byte[] texto = Encoding.UTF8.GetBytes(motivo);
                fixed (byte* p = texto)
                {
                    NativePtr->Close(true, codigo, p, (size_t)texto.Length);
                }
                AnotarSiCerrada();
            }
        }
        SeñalizarEnvio();
    }

    /// <summary>
    /// Lo que se da por «nada en vuelo»: los paquetes de solo ACK del final, que el par no confirma nunca —un ACK no pide
    /// otro—. Uno de datos pesa del orden de 1 350 bytes, y la ventana de congestión más chica, dos de esos.
    /// </summary>
    private const long EnVueloDeSoloAcks = 512;

    /// <summary>
    /// El cierre con gracia: con algo por mandar —en los flujos, o en quiche sin confirmar—, las bombas siguen un rato para
    /// que salga. Las confirmaciones del par abren la ventana de congestión, y sin ellas quiche no manda nada, ni siquiera el
    /// CONNECTION_CLOSE: con la ventana llena, lo pendiente se perdía y el par se quedaba esperando a su inactividad. Acaba
    /// cuando, sin nada ya en los flujos, la bomba de envío da una vuelta que acaba sin nada más que mandar y con lo mandado
    /// confirmado —una pérdida se retransmite mientras—; o una sin nada que mandar cuando lo último con datos salió hace más de
    /// dos PTO. Lo segundo es para quien sobre todo recibe: sus paquetes de solo ACK no los confirma nadie, y contarían en
    /// vuelo para siempre. O al plazo. Sin nada pendiente, vuelve a la primera vuelta.
    /// </summary>
    private void EsperarAQueSalga()
    {
        if (IsClosed || !Establecido) return;
        long limite = Environment.TickCount64 + (long)PlazoDeGracia.TotalMilliseconds;
        _enCierre = true;
        SeñalizarFlujos();

        // Las vueltas cuentan desde que ya no queda nada en los flujos: una anterior pudo acabar antes de que lo último de ellos
        // entrara en quiche.
        long quieto = (long)(QuietudNecesariaMs() * (System.Diagnostics.Stopwatch.Frequency / 1000.0));
        int desdeVacias = -1, desdeVueltas = -1;
        while (!IsClosed && Environment.TickCount64 < limite)
        {
            bool pendiente = false;
            foreach (var (_, s) in streamMap)
                if (s.EnvioPendiente) { pendiente = true; break; }

            if (pendiente)
            {
                desdeVacias = desdeVueltas = -1;
                SeñalizarFlujos();
            }
            else if (desdeVacias < 0)
            {
                desdeVacias  = Volatile.Read(ref _vueltasVacias);
                desdeVueltas = Volatile.Read(ref _vueltasDeCierre);
            }
            else if (Volatile.Read(ref _vueltasVacias) > desdeVacias) return;
            else if (Volatile.Read(ref _vueltasDeCierre) > desdeVueltas
                     && System.Diagnostics.Stopwatch.GetTimestamp() - Volatile.Read(ref _ultimoEnvioDeDatos) >= quieto) return;

            SeñalizarEnvio();
            Thread.Sleep(1);
        }
    }

    /// <summary>Dos PTO (<see cref="MuestraDeCamino.PtoMs"/>), entre 20 y 500 ms: lo que tarda en salir una retransmisión.</summary>
    private long QuietudNecesariaMs()
    {
        if (!MuestraDelCamino(out var m)) return 100;
        return Math.Clamp((long)(2 * m.PtoMs), 20, 500);
    }

    /// <summary>Lo más que se vacía a mano al cerrar: unos 5 MB de paquetes llenos.</summary>
    private const int TopeDePaquetesAlCerrar = 4096;

    /// <summary>
    /// Lo que queda por salir, con las bombas ya fuera: lo encolado en los flujos pasa a quiche, y lo que quiche tenga sale
    /// ya —sin ritmo, y con tope—. Ver Dispose.
    /// </summary>
    private unsafe void VaciarAntesDeCerrar()
    {
        bool viva;
        lock (_candado)
            viva = NativePtr is not null && !NativePtr->IsClosed()
                   && (NativePtr->IsEstablished() || NativePtr->IsInEarlyData());
        if (!viva) return;

        // Una pasada como la de la bomba de flujos: cada flujo, su cola, hasta que ninguno avance.
        try
        {
            bool progreso = true;
            for (int vueltas = 0; progreso && vueltas < 64; vueltas++)
            {
                progreso = false;
                foreach (var (_, s) in streamMap)
                    while (s.IntentarEnviar()) progreso = true;
            }
        }
        catch (QuicheException) { /* lo que no entre, no sale */ }

        // Con sitio para lo que cabe ahora por el camino: lo que no, no se arma —ni una sonda del PMTUD—.
        byte[] buf = new byte[PaqueteQueCabeAhora() + _hueco];
        int hueco = _hueco;
        quiche_send_info info = default;
        for (int i = 0; i < TopeDePaquetesAlCerrar; i++)
        {
            long n;
            lock (_candado)
            {
                if (NativePtr is null) return;
                fixed (byte* p = buf)
                {
                    n = (long)NativePtr->Send(p + hueco, (nuint)(buf.Length - hueco),
                                              (quiche_send_info*)Unsafe.AsPointer(ref info));
                }
            }
            if (n <= 0) return;   // DONE: no queda nada
            int largo = hueco + (int)n;
            _ofuscador?.Codificar(buf.AsSpan(0, largo));
            try { canal.Enviar(buf.AsSpan(0, largo), remoteEndPoint); }
            catch { return; }
        }
    }

    /// <returns>Si no queda ninguna bomba dentro —salvo, quizá, la que llama—: entonces se puede tocar el motor.</returns>
    private bool EsperarBombas()
    {
        bool otraDentro = false;
        int? actual = Task.CurrentId;
        long limite = Environment.TickCount64 + (long)PlazoDeBombas.TotalMilliseconds;

        foreach (Task? bomba in new[] { recvTask, sendTask, recvStreamTask, sendStreamTask, listenTask })
        {
            if (bomba is null || bomba.IsCompleted) continue;

            if (actual is not null && bomba.Id == actual)
            {
                _bombasColgadas = true;   // sigue viva hasta que este Dispose vuelva
                continue;
            }

            long queda = Math.Max(0, limite - Environment.TickCount64);
            try
            {
                if (!bomba.Wait(TimeSpan.FromMilliseconds(queda)))
                {
                    _bombasColgadas = true;
                    otraDentro = true;
                }
            }
            catch (AggregateException) { }
        }

        if (_bombasColgadas) Interlocked.Increment(ref QuicheDiagnostics.StalledShutdownCount);
        return !otraDentro;
    }

    ~QuicheConnection()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
