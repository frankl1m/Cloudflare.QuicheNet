using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Cloudflare.QuicheNet.Internal.Quic;

namespace Cloudflare.QuicheNet;

public class QuicheListener : IDisposable
{
    /// <summary>
    /// Las conexiones por su CID, con los 8 primeros bytes como clave. Cada conexión ocupa dos:
    /// el CID que le dio este servidor y el DCID original del cliente, que llevan su Initial y
    /// su 0-RTT mientras no conoce el otro.
    /// <para>
    /// Se reparte por CID y no por el extremo del cliente porque un extremo no es una conexión.
    /// Por extremo, un cliente que volvía a conectar desde el mismo puerto mandaba su Initial a
    /// la conexión anterior, que seguía drenando y lo tiraba: el saludo esperaba al PTO
    /// inicial, un segundo, y el 0-RTT no llegaba hasta terminarlo. Lo mismo con varias
    /// conexiones por un solo socket, como las abre quic-go.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<ulong, QuicheConnection> porCid = new();

    /// <summary>Las conexiones del mapa, una entrada por conexión, con las dos claves que ocupa.</summary>
    private readonly ConcurrentDictionary<QuicheConnection, (ulong Scid, ulong Odcid)> conexiones = new();

    private readonly Channel<QuicheConnection> connChannel;

    private readonly Socket socket;
    private readonly QuicheConfig config;

    /// <summary>Las conexiones nacen en modo h3 (sin bombas de flujos crudos).</summary>
    private readonly bool esH3;

    /// <summary>
    /// Plazo del saludo desde el primer Initial. La conexión que no se establece en este
    /// tiempo se tira. Si no, un cliente que manda su Initial y desaparece retenía una
    /// conexión con sus bombas hasta que venciera la inactividad.
    /// </summary>
    internal TimeSpan PlazoSaludo { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Cuántas conexiones hay en el mapa, vivas o a medio saludo. Para las pruebas.</summary>
    internal int ConexionesEnMapa => conexiones.Count;

    /// <summary>
    /// Si solo abre conexiones con la configuración que se le dé en <see cref="Repartir"/>: la del MITM por QUIC, que elige la
    /// hoja de cada host por su SNI antes de aceptar. Un Initial sin ella no es de aquí.
    /// </summary>
    internal bool SoloConConfigDada { get; init; }

    /// <summary>
    /// Lo que se hace con una conexión cuyo saludo falló o caducó, antes de soltarla —aún dice quién cerró y con qué—: el MITM
    /// por QUIC lo cuenta como el de TCP. Sin él, se suelta callada.
    /// </summary>
    internal Action<QuicheConnection, Exception>? AlFallarSaludo { get; init; }

    /// <summary>
    /// Lo más con que nace una conexión de este listener según de dónde llega, diga lo que diga el sistema de su camino: lo
    /// que sabe quien lo monta de un tramo que el sistema no ve. En el bucle local de un nodo, donde el sistema dice lo que
    /// cabe en un datagrama, es lo del flujo del relay por el que llega cada cliente. null, nada.
    /// </summary>
    internal Func<EndPoint, int?>? TechoMaximo { get; set; }

    /// <summary>
    /// Quien lee para su bucle: en Linux, varios datagramas por llamada, y los orígenes sin reservar. Ver
    /// <see cref="Cloudflare.QuicheNet.Internal.LectorDeDatagramas"/>. En «auto» no lee: lo hace el reparto del puerto.
    /// </summary>
    private readonly Cloudflare.QuicheNet.Internal.LectorDeDatagramas _lector;

    public QuicheListener(Socket socket, QuicheConfig config) : this(socket, config, false) { }

    public QuicheListener(Socket socket, QuicheConfig config, bool esH3)
    {
        this.socket = socket;
        this.config = config;
        this.esH3   = esH3;
        _lector     = new Cloudflare.QuicheNet.Internal.LectorDeDatagramas(socket, QuicheLibrary.MaxDatagramaUdp);

        // Sin esto, en Windows un ICMP de «puerto inalcanzable» —un cliente que se fue— hace
        // fallar la siguiente recepción del puerto. El bucle lo aguanta, pero es una
        // excepción por cada uno.
        Cloudflare.QuicheNet.Internal.SocketCompat.DisableUdpConnReset(socket);

        connChannel = Channel.CreateUnbounded<QuicheConnection>();
    }

    /// <summary>El token del reparto: el de <see cref="ListenAsync(CancellationToken)"/>, o el de quien reparte desde su bucle.</summary>
    private CancellationToken ctReparto;

    /// <summary>
    /// Para cuando el socket lo lee otro y reparte aquí lo que sea de QUIC con <see cref="Repartir"/>: el puerto
    /// «auto», con un solo socket para varios formatos. Arranca lo que en <see cref="ListenAsync(CancellationToken)"/>
    /// arranca su bucle —el barrido del mapa— y fija el token con que se esperan los saludos.
    /// <para>
    /// Sin ofuscador: desenvolver es en el sitio, y un datagrama que no fuera de QUIC llegaría cambiado al formato de
    /// detrás.
    /// </para>
    /// </summary>
    internal void IniciarSinBucle(CancellationToken ct)
    {
        if (config.Ofuscador is not null)
            throw new InvalidOperationException(
                "un listener QUIC que reparte desde el bucle de otro no puede desenvolver: va sin ofuscador");

        ctReparto = ct;
    }

    public async Task ListenAsync(CancellationToken cancellationToken)
    {
        // Uno para todo el bucle, de lo más largo que puede traer UDP: lo que se queda va a la cola de su conexión en uno
        // prestado de su medida (ver Repartir). Era de 6115 bytes, y lo de más llegaba cortado: una sonda del PMTUD de un
        // cliente por el bucle local, que se perdía, mientras se le anunciaba que se recibían 65 527.
        byte[] recvBuffer = new byte[QuicheLibrary.MaxDatagramaUdp];
        ctReparto = cancellationToken;
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SocketReceiveFromResult recvResult;
            try
            {
                recvResult = await _lector.RecibirAsync(recvBuffer, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                // Un ICMP suelto —un cliente que se fue— no mata el puerto: si la
                // interfaz sigue viva, el bucle continúa. Mismo criterio que el
                // puerto UDP compartido y que las bombas de la conexión.
                if (ex.SocketErrorCode is SocketError.NotSocket or SocketError.Shutdown
                    or SocketError.OperationAborted or SocketError.NetworkDown
                    or SocketError.NetworkReset or SocketError.Interrupted)
                {
                    break;
                }
                continue;
            }

            // El datagrama se desenvuelve UNA vez, aquí, antes de repartirlo: el ofuscador
            // no guarda estado, y así el reparto por CID ve ya el paquete de quiche. Lo que no
            // se desenvuelve se tira sin más.
            int desde = 0;
            int largo = recvResult.ReceivedBytes;
            if (config.Ofuscador is { } ofuscador)
            {
                desde = ofuscador.Decodificar(recvBuffer.AsSpan(0, largo));
                if (desde < 0) continue;
                largo -= desde;
            }

            // El span va en una función aparte porque un método async no puede declarar
            // locales de tipo Span.
            RepartirDelBucle(recvResult.RemoteEndPoint, recvBuffer, desde, largo);
        }
    }

    private void RepartirDelBucle(EndPoint remoto, byte[] buf, int desde, int largo)
        => Repartir(remoto, buf.AsSpan(desde, largo));

    /// <summary>
    /// Reparte un paquete de QUIC ya desenvuelto: a la conexión de su CID, o a una conexión nueva si es el Initial de un
    /// cliente. Devuelve si era de este listener; lo que devuelve <c>false</c> no se ha copiado ni tocado.
    /// <para>
    /// El CID se mira sobre el datagrama tal cual, y solo se copia lo que se queda. Antes se copiaba todo lo que
    /// llegaba, y el ruido pagaba su copia antes de tirarse. Y es lo que deja repartir aquí desde el bucle de otro —el
    /// puerto «auto», que lee un socket para varios formatos—: preguntar cuesta leer el CID y buscarlo, sin tocar nada
    /// que el formato siguiente vaya a leer.
    /// </para>
    /// </summary>
    /// <param name="configDelSaludo">
    /// Con la que se acepta, si es el Initial de una conexión nueva; si no, la del listener. Ver <see cref="SoloConConfigDada"/>.
    /// </param>
    internal bool Repartir(EndPoint remoto, ReadOnlySpan<byte> paquete, QuicheConfig? configDelSaludo = null)
    {
        if (!LeerDcid(paquete, out int inicioDcid, out int largoDcid)) return false;
        ReadOnlySpan<byte> dcid = paquete.Slice(inicioDcid, largoDcid);
        ulong clave = Clave(dcid);

        if (porCid.TryGetValue(clave, out QuicheConnection? connection)
            && !connection.IsClosed
            && connection.EsSuDcid(dcid))
        {
            // Un short-header suyo desde otro extremo es un rebinding de NAT del cliente:
            // se repunta el destino del envío —el mundo de quiche no se mueve, ver
            // Repuntar—. Los paquetes del saludo no mueven la conexión.
            // Se repunta cuando quiche lo acepta, no antes: ver la bomba de recepción de la conexión.
            EndPoint? nuevo = (paquete[0] & 0x80) == 0 && !remoto.Equals(connection.RemoteEndPoint) ? remoto : null;

            // La cola de la conexión necesita una copia propia —el búfer se reusa en
            // la siguiente lectura—, del tamaño del paquete y arrendada.
            byte[] copia = ArrayPool<byte>.Shared.Rent(paquete.Length);
            paquete.CopyTo(copia);
            int largo = (paquete[0] & 0x80) != 0 ? FiltrarCeroRtt(connection, copia.AsSpan(0, paquete.Length)) : paquete.Length;
            connection.EncolarRecibido(copia, largo, nuevo);
            return true;
        }

        // Solo un Initial de cliente abre conexión. Lo demás —un paquete de una
        // conexión que ya no está, un escaneo, un salamander con otra contraseña—
        // se tira aquí, sin montar para nada una conexión con sus bombas.
        if (!EsInicialDeCliente(paquete)) return false;
        var configNueva = configDelSaludo ?? config;
        if (configDelSaludo is null && SoloConConfigDada) return false;

        // Hasta que acabe su saludo: ver EntregarAlEstablecerseAsync. Con el tope lleno, el Initial se tira.
        if (Interlocked.Increment(ref _aMedioSaludo) > TopeAMedioSaludo)
        {
            Interlocked.Decrement(ref _aMedioSaludo);
            Interlocked.Increment(ref InicialesTirados);
            return true;
        }

        // El DCID original se copia antes de soltar el datagrama: con él se reconoce el
        // resto del Initial y el 0-RTT de esta conexión.
        byte[] odcid  = dcid.ToArray();
        byte[] saludo = ArrayPool<byte>.Shared.Rent(paquete.Length);
        paquete.CopyTo(saludo);
        QuicheConnection conn;
        try
        {
            // Con early data, el registro contra la repetición decide aquí por toda la conexión, con el aleatorio de su
            // ClientHello si lo pide; el 0-RTT que venga pegado al Initial ya pasa por él. Ver FiltrarCeroRtt. Sin el
            // registro —solo al medir—, se admite sin que nadie responda de él: lo que pida no se atiende antes del saludo.
            int largo = paquete.Length;
            int decidido = 0;
            if (configNueva.IsEarlyDataEnabled)
            {
                var (aleatorio, pide) = AntiRepeticionCeroRtt.LeerSaludo(paquete);
                decidido = !pide ? 1
                         : !AntiRepeticionCeroRtt.Activa ? 3
                         : AntiRepeticionCeroRtt.Admitir(aleatorio) ? 1 : 2;
                if (decidido == 2) largo = AntiRepeticionCeroRtt.QuitarCeroRtt(saludo.AsSpan(0, largo));
            }
            conn = QuicheConnection.Accept(socket, remoto, saludo.AsMemory(0, largo), configNueva, esH3: esH3,
                                           techoMaximo: TechoMaximo?.Invoke(remoto));
            conn.CeroRttDecidido = decidido;
        }
        catch
        {
            // Un saludo que quiche no acepta no tumba el puerto: se tira y a por
            // el siguiente. Y no era de QUIC, o no de uno que este puerto hable.
            Interlocked.Decrement(ref _aMedioSaludo);
            return false;
        }
        finally
        {
            // Accept ya copió el saludo a su propio búfer: se devuelve el arrendado.
            ArrayPool<byte>.Shared.Return(saludo);
        }

        conn.DcidOriginal = odcid;
        ulong claveScid = Clave(conn.Scid);
        if (!porCid.TryAdd(claveScid, conn))
        {
            // Otro CID aleatorio con los mismos 8 primeros bytes: no pasa en la práctica,
            // y si pasa, el cliente repite su Initial y le toca otro.
            Interlocked.Decrement(ref _aMedioSaludo);
            try { conn.Dispose(); } catch { }
            return true;
        }

        // La clave del DCID original sí se pisa: si ya estaba, era de una conexión cerrada.
        porCid[clave] = conn;
        conexiones[conn] = (claveScid, clave);
        _ = QuitarAlCerrarseAsync(conn);

        // Cada saludo se espera aparte, y la cola solo recibe conexiones que ya se pueden
        // usar. Antes AcceptAsync esperaba los saludos en fila: uno que no terminaba nunca
        // dejaba el puerto sin aceptar a nadie más.
        _ = EntregarAlEstablecerseAsync(conn, ctReparto);
        return true;
    }

    /// <summary>
    /// Si <paramref name="paquete"/> es de una conexión de este listener, por su Connection ID, sin tocar nada: quien reparte
    /// desde su bucle —el MITM por QUIC— pregunta antes de desviarlo aquí.
    /// </summary>
    internal bool EsSuyo(ReadOnlySpan<byte> paquete)
    {
        if (!LeerDcid(paquete, out int inicioDcid, out int largoDcid)) return false;
        ReadOnlySpan<byte> dcid = paquete.Slice(inicioDcid, largoDcid);
        return porCid.TryGetValue(Clave(dcid), out var conexion) && !conexion.IsClosed && conexion.EsSuDcid(dcid);
    }

    /// <summary>
    /// El 0-RTT de un datagrama de cabecera larga de <paramref name="conn"/>: si el registro contra la repetición
    /// (<see cref="AntiRepeticionCeroRtt"/>) no lo admitió al crearla, se quita. Devuelve lo que mide el datagrama tras
    /// quitarlo. Lo admitido sin registro (3) se deja: quien lo atiende espera al saludo.
    /// </summary>
    private static int FiltrarCeroRtt(QuicheConnection conn, Span<byte> datagrama)
        => conn.CeroRttDecidido == 2 ? AntiRepeticionCeroRtt.QuitarCeroRtt(datagrama) : datagrama.Length;

    /// <summary>
    /// Dónde va el DCID del paquete. En el short-header mide lo que miden los CID de este
    /// servidor (MAX_CONN_ID_LEN), que es quien los dio; en el long-header lo dice su byte de
    /// largo. Con menos de 8 bytes no se reparte: ningún CID de este servidor mide eso, y el
    /// DCID original de un cliente tampoco puede (RFC 9000 §7.2).
    /// </summary>
    private static bool LeerDcid(ReadOnlySpan<byte> paquete, out int inicio, out int largo)
    {
        if (paquete.Length > 0 && (paquete[0] & 0x80) == 0)
        {
            inicio = 1;
            largo = (int)QuicheLibrary.MAX_CONN_ID_LEN;
            return paquete.Length >= inicio + largo;
        }

        // Long header: flags (1), versión (4), largo del DCID (1) y el DCID.
        inicio = 6;
        largo = paquete.Length > 5 ? paquete[5] : 0;
        return largo is >= 8 and <= 20 && paquete.Length >= inicio + largo;
    }

    /// <summary>La clave del mapa: los 8 primeros bytes del CID, que son aleatorios.</summary>
    private static ulong Clave(ReadOnlySpan<byte> cid) => BinaryPrimitives.ReadUInt64LittleEndian(cid);

    /// <summary>
    /// Lo más de conexiones a medio saludo que admite el puerto. Cada Initial de un cliente nuevo monta una conexión con sus
    /// bombas y su estado en quiche, que vive hasta acabar el saludo o su plazo (<see cref="PlazoSaludo"/>): sin tope, un
    /// chorro de Initials —uno por datagrama, sin nada detrás— hacía crecer la memoria sin freno. Por encima, el Initial se
    /// tira, como haría un servidor sin sitio: el cliente de verdad lo repite.
    /// </summary>
    internal int TopeAMedioSaludo { get; set; } = 1024;

    private int _aMedioSaludo;

    /// <summary>Las conexiones a medio saludo ahora mismo.</summary>
    internal int AMedioSaludo => Volatile.Read(ref _aMedioSaludo);

    /// <summary>Los Initials tirados por el tope. Solo para medir.</summary>
    internal long InicialesTirados;

    /// <summary>
    /// Espera a que una conexión nueva se pueda usar, con su plazo, y la pone en la cola de
    /// aceptadas. Si el saludo falla o caduca, o el puerto se cierra, la conexión sale del
    /// mapa y se suelta sin llegar a nadie.
    /// </summary>
    private async Task EntregarAlEstablecerseAsync(QuicheConnection conn, CancellationToken ct)
    {
        try
        {
            // Con early data se entrega en cuanto se acepta el 0-RTT: esperar al Finished del
            // cliente se comería justo el RTT que el 0-RTT ahorra.
            Task lista = config.IsEarlyDataEnabled ? conn.UtilizableTask : conn.ConnectionEstablishedTask;
            try { await lista.WaitAsync(PlazoSaludo, ct).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _aMedioSaludo); }   // ya no está a medio saludo: saludada, fallida o caducada
            if (connChannel.Writer.TryWrite(conn)) return;
        }
        catch (Exception ex)
        {
            // El saludo falló o caducó, o el puerto se cierra.
            if (!ct.IsCancellationRequested && AlFallarSaludo is { } alFallar)
            {
                try { alFallar(conn, ex); } catch { }
            }
        }

        Quitar(conn);
        try { conn.Dispose(); } catch { }
    }

    /// <summary>Saca una conexión del mapa: su entrada y sus dos claves, si aún son suyas.</summary>
    private void Quitar(QuicheConnection conn)
    {
        if (!conexiones.TryRemove(conn, out var claves)) return;
        porCid.TryRemove(new KeyValuePair<ulong, QuicheConnection>(claves.Scid, conn));
        porCid.TryRemove(new KeyValuePair<ulong, QuicheConnection>(claves.Odcid, conn));
    }

    /// <summary>
    /// Saca la conexión del mapa en cuanto se cierra. Al principio nadie lo hacía —una conexión
    /// muerta solo salía cuando su mismo extremo volvía a escribir, así que un puerto de larga
    /// vida acumulaba una entrada por cliente servido—, y después un barrido cada 10 s por puerto,
    /// que despertaba también sin nadie y dejaba cada cerrada hasta 10 s en el mapa.
    /// </summary>
    private async Task QuitarAlCerrarseAsync(QuicheConnection conn)
    {
        await conn.Cerrada.ConfigureAwait(false);
        Quitar(conn);
    }

    /// <summary>
    /// ¿Abre conexión este paquete? Tiene que ser un Initial de QUIC v1 —cabecera larga,
    /// bit fijo y tipo 0— en un datagrama de al menos 1200 bytes, lo mínimo que manda un
    /// cliente (RFC 9000 §14.1). quiche no habla otra versión.
    /// </summary>
    private static bool EsInicialDeCliente(ReadOnlySpan<byte> paquete)
    {
        if (paquete.Length < QuicheLibrary.MIN_CLIENT_INITIAL_LEN) return false;
        if ((paquete[0] & 0xF0) != 0xC0) return false;
        return BinaryPrimitives.ReadUInt32BigEndian(paquete[1..]) == QuicheLibrary.PROTOCOL_VERSION;
    }

    public Task ListenAsync()
    {
        return ListenAsync(CancellationToken.None);
    }

    /// <summary>
    /// La siguiente conexión que ya se puede usar: con el saludo completo o, con early data,
    /// con el 0-RTT aceptado. Los saludos se esperan en paralelo.
    /// </summary>
    public async Task<QuicheConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        return await connChannel.Reader.ReadAsync(cancellationToken);
    }


    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                // Las establecidas que nadie llegó a aceptar son de aquí: se sueltan, o sus
                // bombas seguirían vivas sin dueño. Con la cola cerrada, un saludo que acabe
                // después tampoco entra y se suelta solo.
                connChannel.Writer.TryComplete();
                while (connChannel.Reader.TryRead(out QuicheConnection? pendiente))
                {
                    try { pendiente.Dispose(); } catch { }
                }
                porCid.Clear();
                conexiones.Clear();
            }

            disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
