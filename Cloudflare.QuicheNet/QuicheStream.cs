using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cloudflare.QuicheNet;

/// <summary>
/// Flujo de una conexión quiche. La conexión bombea los datos recibidos con
/// <see cref="ReceiveDataAsync"/> y drena lo enviado leyendo <see cref="Flush"/>.
/// Reparado para uso de proxy: ReadAsync/WriteAsync esperan de verdad, y el FIN
/// de escritura se manda con <see cref="ShutdownWrite"/> sin cerrar la lectura.
/// <para>
/// <b>Con freno en los dos sentidos.</b> Antes escribir encolaba sin tope y volvía en el acto, y la bomba sacaba de
/// quiche todo lo recibido aunque nadie lo leyera: un cliente de LAN sobre un camino lento, o una descarga que el
/// cliente abandona, llenaban la memoria sin límite, y quiche reabría la ventana del par porque veía los datos ya
/// consumidos. Ahora la escritura espera con más de <see cref="TopeEnvio"/> en cola, y la bomba deja en quiche lo de
/// un flujo con más de <see cref="TopeRecepcion"/> sin leer: su ventana no se reabre y el par se frena.
/// </para>
/// </summary>
public class QuicheStream : Stream
{
    public enum Shutdown
    {
        Read = 0,
        Write = 1,
    }

    public enum Direction
    {
        Bidirectional = 0x0,
        Unidirectional = 0x2,
    }

    /// <summary>Lo que se encola para enviar antes de que escribir espere.</summary>
    internal const long TopeEnvio = 2L * 1024 * 1024;

    /// <summary>Lo recibido sin leer antes de que la bomba deje de sacar datos de este flujo.</summary>
    internal const long TopeRecepcion = 2L * 1024 * 1024;

    private readonly QuicheConnection conn;

    private readonly ulong streamId;

    // ---- recepción -------------------------------------------------------
    // La conexión llama a ReceiveDataAsync (un solo hilo); el consumidor lee.
    // Un Channel en vez de la cola+semaforo de antes: el despertar de los
    // lectores lo garantiza el canal, no una contabilidad de releases que se
    // podía descuadrar —un lector que despierta sin datos se comía el aviso
    // del siguiente y la sesión se quedaba muda para siempre—.

    /// <summary>
    /// Un trozo de datos en un búfer arrendado del almacén, con su largo: el búfer puede ser mayor. Se devuelve cuando
    /// quien lee lo agota, o cuando quiche acepta entero el de envío. Antes eran copias nuevas —una por cada lectura de
    /// la bomba y otra por cada escritura—, y eran el 90 % de lo que asignaba un flujo QUIC a plena carga.
    /// </summary>
    private readonly record struct Trozo(byte[] Buf, int Len);

    private static void Devolver(byte[] buf)
    {
        if (buf.Length > 0) ArrayPool<byte>.Shared.Return(buf);
    }

    private readonly Channel<Trozo>? colaRecv;
    private byte[]? pendienteRecv;
    private int posPendienteRecv;
    private int largoPendienteRecv;

    /// <summary>Llegó el FIN o el corte del par: quiche ya no dará más datos de este flujo.</summary>
    private volatile bool recvCompleto;

    /// <summary>
    /// Llegó el FIN del par: la recepción acabó en orden. Sin él, el fin es un corte —un RESET del par, el corte de aquí, o
    /// la conexión que se acabó—, y quien lee por encima lo dice: ver <c>FlujoQuic.ReadAsync</c>.
    /// </summary>
    private volatile bool finRecibido;

    /// <summary>Bytes que entraron en <see cref="colaRecv"/> y nadie ha leído aún.</summary>
    private long bytesSinLeer;

    /// <summary>1 si la bomba dejó de leer este flujo por lleno: el lector la avisa al vaciarlo.</summary>
    private int recepcionPausada;

    // ---- envío -----------------------------------------------------------
    // WriteAsync encola en la cola del propio flujo; el bucle de la conexión
    // la drena con IntentarEnviar. finPendiente marca que hay que mandar FIN
    // tras los datos. Cada flujo manda SU cola en orden: no hay una cola
    // compartida con la que puedan entremezclarse los restos de un envío
    // parcial y los datos recién llegados (que era justo el reordenamiento
    // que corrompía el flujo).

    private readonly Queue<Trozo>? colaEnv;
    private readonly object candadoEnv = new();
    private byte[]? parcialEnv;
    private int posParcialEnv;
    private int largoParcialEnv;
    private volatile bool finPendiente;
    private volatile bool finEnviado;

    /// <summary>Bytes encolados que quiche todavía no ha aceptado.</summary>
    private long bytesEnCola;

    /// <summary>
    /// 1 mientras está en la lista de pendientes de su conexión, la que recorre la bomba de flujos: ver
    /// <see cref="QuicheConnection.AnotarPendiente"/>. Entra lo que no sale en línea —antes del saludo, lo que la ventana no
    /// admite, la escritura síncrona— y sale cuando ya no le queda nada.
    /// </summary>
    private int enPendientes;

    /// <summary>Lo marca como pendiente: verdadero si no lo estaba, y entonces hay que meterlo en la lista.</summary>
    internal bool MarcarPendiente() => Interlocked.CompareExchange(ref enPendientes, 1, 0) == 0;

    /// <summary>Si está en la lista de pendientes, o en la vuelta en curso de la bomba de flujos.</summary>
    internal bool EstaPendiente => Volatile.Read(ref enPendientes) == 1;

    /// <summary>Si mandó algo en la vuelta en curso de la bomba de flujos: ver su reparto por turnos. Solo lo toca ella.</summary>
    internal bool TuvoTurno;

    /// <summary>Le quita la marca, con barrera: quien mira después si le queda algo ve lo encolado antes.</summary>
    internal void DesmarcarPendiente() => Interlocked.Exchange(ref enPendientes, 0);

    /// <summary>Lo que espera quien escribe con la cola llena; se completa al bajar del tope.</summary>
    private TaskCompletionSource? hueco;

    /// <summary>
    /// El par dejó de leer (STOP_SENDING) o quiche dio el flujo por roto: lo encolado se tiró y escribir más lanza.
    /// </summary>
    private volatile bool envioAbortado;

    private bool disposedValue;

    public override bool CanRead => colaRecv is not null;

    public override bool CanWrite => colaEnv is not null;

    public override bool CanSeek => false;

    public override long Position
    {
        get => throw new NotSupportedException("This stream cannot be seeked.");
        set => throw new NotSupportedException("This stream cannot be seeked.");
    }

    public override long Length => throw new NotSupportedException("This stream cannot have its length set or changed.");

    internal bool IsShuttingDown => finPendiente;

    /// <summary>La conexión dueña del flujo (para la sonda y los cortes).</summary>
    internal QuicheConnection Conexion => conn;

    /// <summary>El identificador QUIC del flujo: el bit 0 dice quién lo abrió y el 1, si es uni.</summary>
    internal ulong Id => streamId;

    /// <summary>
    /// ¿Hay bytes recibidos que nadie ha leído? Sin esperar ni consumir nada. Solo tiene sentido
    /// preguntarlo desde quien lee, entre dos lecturas.
    /// </summary>
    internal bool HayDatosSinLeer => pendienteRecv is not null || (colaRecv?.Reader.TryPeek(out _) ?? false);

    /// <summary>
    /// ¿Ya no queda nada que hacer con él en quiche? Recepción acabada y FIN enviado (o abortado), en los sentidos que
    /// tenga. Entonces la conexión lo suelta de su mapa: lo que no se haya leído sigue en su cola para quien lo tenga.
    /// </summary>
    internal bool Acabado => (colaRecv is null || recvCompleto) && (colaEnv is null || finEnviado);

    /// <summary>
    /// ¿Tiene ya bastante sin leer? Lo pregunta la bomba antes de sacar más de quiche. Si dice que sí, queda anotado que
    /// el lector tiene que avisar al vaciarlo. La marca va antes de mirar la cuenta: al revés, un lector que vaciara
    /// justo entre medias no avisaría a nadie, y el flujo se quedaría parado hasta el siguiente paquete.
    /// </summary>
    internal bool PausarSiLlena()
    {
        Volatile.Write(ref recepcionPausada, 1);
        if (Interlocked.Read(ref bytesSinLeer) >= TopeRecepcion) return true;
        Volatile.Write(ref recepcionPausada, 0);
        return false;
    }

    /// <summary>El par abortó el flujo, o la conexión se acabó: el lector ve EOF y quien escribe deja de esperar.</summary>
    /// <remarks>
    /// No suelta el flujo del mapa, a diferencia del FIN: tras un RESET, quiche podría volver a darlo por legible, y un
    /// flujo del par que ya no está en el mapa se tomaría por uno NUEVO y se entregaría a quien acepta. Los cortes son
    /// raros; el FIN, que es lo de cada sesión, sí lo suelta.
    /// </remarks>
    internal void AbortRecv()
    {
        recvCompleto = true;
        colaRecv?.Writer.TryComplete();
        LiberarHueco();
    }

    /// <summary>El FIN ya salió (lo mantiene el bucle de envío).</summary>
    internal bool FinEnviado => finEnviado;

    /// <summary>Si la recepción acabó con el FIN del par, en orden, y no con un corte. Ver <see cref="finRecibido"/>.</summary>
    internal bool FinRecibido => finRecibido;

    /// <summary>
    /// Si le queda algo por pasar a quiche: bytes encolados, o su FIN pedido y aún no puesto. Lo mira el cierre con gracia de
    /// la conexión.
    /// </summary>
    internal bool EnvioPendiente => colaEnv is not null && !finEnviado && (finPendiente || Interlocked.Read(ref bytesEnCola) > 0);

    internal QuicheStream(QuicheConnection conn, ulong streamId)
    {
        this.conn = conn;
        this.streamId = streamId;

        // El bit 0 del id vale 0 si lo abrió el cliente. Un uni solo lo escribe quien lo
        // abrió y solo lo lee el otro lado.
        bool isLocal = ((streamId & 1) == 0) ^ conn.IsServer;
        bool isBidirectional = (streamId & 2) == 0;

        if (!isLocal || isBidirectional)
        {
            // Sin SingleReader a propósito: el lector puede ser uno distinto en cada
            // fase de la sesión (el saludo lo lee el pool, el resto el relevo), y el
            // canal sin opciones es seguro para varios.
            colaRecv = Channel.CreateUnbounded<Trozo>();
        }

        if (isLocal || isBidirectional)
        {
            colaEnv = new();
        }
    }

    internal Task ReceiveDataAsync(ReadOnlyMemory<byte> bufIn, bool finished, CancellationToken cancellationToken)
    {
        if (colaRecv is null)
        {
            throw new NotSupportedException();
        }

        if (!bufIn.IsEmpty)
        {
            // El canal no tiene tope y el escritor es uno solo, así que la escritura
            // nunca espera; si el lector ya se fue, el dato sobra. El freno es la bomba,
            // que no llama aquí con el flujo lleno.
            byte[] copia = ArrayPool<byte>.Shared.Rent(bufIn.Length);
            bufIn.Span.CopyTo(copia);
            if (colaRecv.Writer.TryWrite(new Trozo(copia, bufIn.Length)))
            {
                Interlocked.Add(ref bytesSinLeer, bufIn.Length);
            }
            else
            {
                Devolver(copia);
            }
        }
        if (finished)
        {
            finRecibido = true;
            recvCompleto = true;
            colaRecv.Writer.TryComplete();
            conn.OlvidarSiAcabado(this);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Manda lo que quepa de lo encolado —como mucho <paramref name="turno"/> bytes—, en orden, y el FIN si toca.
    /// Devuelve <c>true</c> si hubo progreso. Lo llama el bucle de la conexión, que reparte por turnos.
    /// <para>
    /// Un error de ESTE flujo —el par mandó STOP_SENDING, o quiche lo da por roto— se queda en el flujo: tira lo
    /// encolado y lo da por cerrado. Antes subía hasta la bomba de envío, que lo tomaba por un error de conexión y
    /// dejaba de enviar para todos los flujos, con la conexión todavía repartiéndose.
    /// </para>
    /// </summary>
    internal bool IntentarEnviar(int turno = int.MaxValue)
    {
        if (colaEnv is null)
        {
            return false;
        }

        bool progreso = false;
        lock (candadoEnv)
        {
            if (finEnviado)
            {
                return false;
            }

            try
            {
                int quedan = turno;
                while (quedan > 0)
                {
                    if (parcialEnv is null)
                    {
                        if (colaEnv.Count == 0) break;
                        (parcialEnv, largoParcialEnv) = colaEnv.Dequeue();
                        posParcialEnv = 0;
                    }

                    int hasta = (int)Math.Min(largoParcialEnv, (long)posParcialEnv + quedan);
                    long resultado = conn.EnviarParte(streamId, parcialEnv, posParcialEnv, hasta);
                    if (resultado <= 0)
                    {
                        // Ventana llena, tope de flujos del par (o DONE): lo que quede
                        // espera al siguiente despertar. Los errores de verdad los lanza
                        // EnviarParte.
                        break;
                    }

                    posParcialEnv += (int)resultado;
                    quedan        -= (int)resultado;
                    Interlocked.Add(ref bytesEnCola, -resultado);
                    progreso = true;
                    if (posParcialEnv >= largoParcialEnv)
                    {
                        // quiche ya se copió el trozo entero: el búfer vuelve al almacén.
                        Devolver(parcialEnv);
                        parcialEnv = null;
                    }
                }

                // El FIN del medio cierre va tras el último byte (o vacío si no queda nada).
                if (finPendiente && parcialEnv is null && colaEnv.Count == 0 && conn.SendStreamFin(streamId))
                {
                    finEnviado = true;
                    progreso = true;
                }
            }
            catch (QuicheException ex) when (QuicheConnection.EsErrorDeFlujo(ex.ErrorCode))
            {
                envioAbortado = true;
                while (colaEnv.Count > 0) Devolver(colaEnv.Dequeue().Buf);
                if (parcialEnv is not null) Devolver(parcialEnv);
                parcialEnv = null;
                Interlocked.Exchange(ref bytesEnCola, 0);
                finEnviado = true;
                progreso = false;
            }
        }

        if (finEnviado || Interlocked.Read(ref bytesEnCola) <= TopeEnvio)
        {
            LiberarHueco();
        }
        if (finEnviado)
        {
            conn.OlvidarSiAcabado(this);
        }
        else if (EnvioPendiente)
        {
            // Lo que no salió —ventana llena, o el tope de flujos del par— espera en la lista de pendientes sin despertar a
            // la bomba: la despierta el paquete que traiga la ventana nueva.
            conn.AnotarPendiente(this, avisar: false);
        }
        return progreso;
    }

    private void LiberarHueco() => Interlocked.Exchange(ref hueco, null)?.TrySetResult();

    /// <summary>Vuelca lo encolado: despierta a la bomba de envío de la conexión.</summary>
    public override void Flush()
    {
        // Con la conexión establecida el envío va en línea desde WriteAsync, así que
        // aquí no suele quedar nada; el aviso igualmente despierta a la bomba para que
        // reintente lo que la ventana no dejó salir.
        conn.SeñalizarFlujos();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        if (colaRecv is null)
        {
            throw new NotSupportedException("This stream is not readable.");
        }
        if (count == 0)
        {
            return 0;
        }

        // Espera a que haya algo, como cualquier Stream: devolvía lo que hubiera sin esperar, y sin nada devolvía 0, que es
        // el fin del flujo.
        int n = CopiarSinEspera(buffer.AsSpan(offset, count));
        if (n > 0) return n;
        return ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (colaRecv is null)
        {
            throw new NotSupportedException("This stream is not readable.");
        }
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            int n = CopiarSinEspera(buffer.Span);
            if (n > 0)
            {
                return n;
            }

            // El canal completado es el EOF; lo que quede encolado se lee primero.
            if (colaRecv.Reader.Completion.IsCompleted)
            {
                return 0;
            }

            if (!await colaRecv.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
        }
    }

    /// <summary>Copia lo que haya; devuelve 0 si no hay nada.</summary>
    private int CopiarSinEspera(Span<byte> destino)
    {
        int copiado = 0;
        while (copiado < destino.Length)
        {
            if (pendienteRecv is null)
            {
                if (!colaRecv!.Reader.TryRead(out Trozo trozo))
                {
                    break;
                }
                (pendienteRecv, largoPendienteRecv) = trozo;
                posPendienteRecv = 0;
            }

            int n = Math.Min(destino.Length - copiado, largoPendienteRecv - posPendienteRecv);
            pendienteRecv.AsSpan(posPendienteRecv, n).CopyTo(destino[copiado..]);
            posPendienteRecv += n;
            copiado += n;
            if (posPendienteRecv >= largoPendienteRecv)
            {
                // Agotado: el búfer vuelve al almacén.
                Devolver(pendienteRecv);
                pendienteRecv = null;
            }
        }

        if (copiado > 0)
        {
            // Con la mitad libre, la bomba vuelve a sacar datos de este flujo si lo tenía parado.
            long quedan = Interlocked.Add(ref bytesSinLeer, -copiado);
            if (quedan < TopeRecepcion / 2 && Interlocked.Exchange(ref recepcionPausada, 0) == 1)
            {
                conn.ReanudarRecepcion();
            }
        }
        return copiado;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (colaEnv is null)
        {
            throw new NotSupportedException("This stream is not writable.");
        }

        // Nada que escribir: un trozo vacío en la cola no lo acepta nunca quiche, y se quedaba delante de todo lo demás.
        if (buffer.IsEmpty)
        {
            return ValueTask.CompletedTask;
        }

        byte[] copia = ArrayPool<byte>.Shared.Rent(buffer.Length);
        buffer.Span.CopyTo(copia);
        Encolar(copia, buffer.Length);

        // Enviar en línea, sin esperar el despertar de la bomba: si la ventana de
        // quiche lo acepta, el dato entra directo en el motor y solo se despierta a
        // la bomba de paquetes —un salto de tarea menos en cada escritura—. Lo que la
        // ventana no deje salir lo reintenta la bomba cuando los ACK la abran. Antes
        // del saludo no se puede mandar nada: la bomba recoge la cola al establecerse.
        // Con otros flujos esperando ventana, tampoco: se va a la cola de la bomba,
        // que reparte por turnos —ver QuicheConnection.HayOtrosEsperando—.
        if (conn.Establecido && !conn.HayOtrosEsperando(this))
        {
            IntentarEnviar();
        }
        else
        {
            conn.AnotarPendiente(this, avisar: true);
        }

        // Lo normal es que quiche lo haya aceptado ya y la cola esté vacía. Si no, con la cola
        // por encima del tope se espera a que baje: es lo que frena a quien escribe más deprisa
        // de lo que el camino deja salir.
        return Interlocked.Read(ref bytesEnCola) > TopeEnvio
            ? new ValueTask(EsperarHuecoAsync(cancellationToken))
            : ValueTask.CompletedTask;
    }

    /// <summary>Espera a que la cola baje del tope, o a que ya no tenga sentido esperar.</summary>
    private async Task EsperarHuecoAsync(CancellationToken ct)
    {
        while (Interlocked.Read(ref bytesEnCola) > TopeEnvio && !envioAbortado)
        {
            // Se apunta a la espera ANTES de volver a mirar: un vaciado entre la mirada y la
            // espera no se pierde.
            var nueva = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var espera = (Interlocked.CompareExchange(ref hueco, nueva, null) ?? nueva).Task;

            if (Interlocked.Read(ref bytesEnCola) <= TopeEnvio || envioAbortado) break;
            if (conn.CerradaODrenando) throw new IOException("la conexión quic se acabó con datos por enviar");

            try
            {
                // El plazo solo cubre una conexión que muere sin avisar a este flujo.
                await espera.WaitAsync(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (colaEnv is null)
        {
            throw new NotSupportedException("This stream is not writable.");
        }

        if (count == 0)
        {
            return;   // ver WriteAsync: un trozo vacío atascaría la cola
        }

        byte[] copia = ArrayPool<byte>.Shared.Rent(count);
        Array.Copy(buffer, offset, copia, 0, count);
        Encolar(copia, count);

        // Como antes, sale con el Flush o en la vuelta siguiente de la bomba de flujos.
        conn.AnotarPendiente(this, avisar: false);
    }

    /// <summary>Encola <paramref name="largo"/> bytes de un búfer arrendado; si no se puede, el búfer vuelve al almacén.</summary>
    private void Encolar(byte[] copia, int largo)
    {
        lock (candadoEnv)
        {
            if (envioAbortado)
            {
                Devolver(copia);
                throw new IOException("el par dejó de leer el flujo quic");
            }
            if (finPendiente)
            {
                Devolver(copia);
                throw new InvalidOperationException("Cannot write after ShutdownWrite.");
            }
            colaEnv!.Enqueue(new Trozo(copia, largo));
            Interlocked.Add(ref bytesEnCola, largo);
        }
    }

    /// <summary>
    /// Medio cierre de escritura: manda FIN al otro lado sin cerrar la lectura.
    /// </summary>
    public void ShutdownWrite()
    {
        if (colaEnv is null)
        {
            throw new NotSupportedException("This stream is not writable.");
        }

        lock (candadoEnv)
        {
            if (finPendiente)
            {
                return;
            }
            finPendiente = true;
        }

        // El FIN sale ya, detrás de lo encolado, sin esperar a la bomba; si la conexión
        // aún no está establecida lo recogerá la bomba al establecerse.
        if (conn.Establecido)
        {
            IntentarEnviar();
        }
        else
        {
            conn.AnotarPendiente(this, avisar: true);
        }
    }

    /// <summary>Marca que el FIN ya salió; lo llama el bucle de envío de la conexión.</summary>
    internal void MarcarFinEnviado()
    {
        lock (candadoEnv)
        {
            finEnviado = true;
        }
        conn.OlvidarSiAcabado(this);
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("This stream cannot be seeked.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("This stream cannot have its length set or changed.");

    /// <summary>
    /// Corta el flujo en los dos sentidos (RFC 9000 §19.4 y §19.5): lo encolado se tira, el par recibe RESET_STREAM y
    /// STOP_SENDING, y quien lea aquí ve el fin sin el FIN del par. Es lo que hace el relevo con un flujo que no acabó en
    /// orden: ver <c>FlujoQuic.Cortar</c>.
    /// </summary>
    internal void Cortar(ulong codigo = 0)
    {
        if (colaEnv is not null)
        {
            lock (candadoEnv)
            {
                envioAbortado = true;
                finPendiente = true;
                finEnviado = true;
                while (colaEnv.Count > 0) Devolver(colaEnv.Dequeue().Buf);
                if (parcialEnv is not null) Devolver(parcialEnv);
                parcialEnv = null;
                Interlocked.Exchange(ref bytesEnCola, 0);
            }
        }

        try { conn.ResetStream(streamId, codigo); } catch (QuicheException) { }
        AbortRecv();
        DescartarRecibido();

        // Con la lectura cerrada, quiche no vuelve a darlo por legible: ver DejarDeLeer.
        conn.OlvidarSiAcabado(this);
    }

    /// <summary>
    /// Nadie va a leer más. Si el par no había acabado, quiche deja de guardar lo que llegue de este flujo —lo que tenía se
    /// tira y su crédito vuelve a la conexión— y el par recibe STOP_SENDING (RFC 9000 §19.5). Sin esto, un flujo soltado con
    /// el par aún mandando llenaba su cola —<see cref="TopeRecepcion"/>— y lo demás se quedaba en quiche hasta la ventana del
    /// flujo, contando contra la de la conexión: con unos pocos así, la conexión compartida ya no recibía nada para nadie.
    /// </summary>
    private void DejarDeLeer()
    {
        if (colaRecv is null) return;

        // Con la conexión cerrándose no se toca: sus bombas siguen un rato —el cierre con gracia—, y sin la lectura cerrada en
        // quiche un paquete tardío de este flujo, fuera ya del mapa, lo haría pasar por uno nuevo del par. El fin a quien lea
        // lo pone el cierre.
        if (!recvCompleto && conn.DejarDeLeer(streamId))
        {
            recvCompleto = true;
            colaRecv.Writer.TryComplete();

            // Tras cerrar la lectura, quiche no vuelve a dar el flujo por legible —lo que llegue, incluido un RESET, no se
            // guarda—: puede salir del mapa sin que un paquete tardío lo haga pasar por uno nuevo del par.
            conn.OlvidarSiAcabado(this);
        }
        if (recvCompleto) DescartarRecibido();
    }

    /// <summary>Lo recibido y sin leer vuelve al almacén: ya no lo leerá nadie.</summary>
    private void DescartarRecibido()
    {
        if (colaRecv is null) return;
        while (colaRecv.Reader.TryRead(out Trozo trozo)) Devolver(trozo.Buf);
        Interlocked.Exchange(ref bytesSinLeer, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            disposedValue = true;

            if (disposing)
            {
                try
                {
                    ShutdownWrite();
                }
                catch (NotSupportedException)
                {
                    // Flujo solo de lectura: no hay lado de envío que cerrar.
                }

                DejarDeLeer();

                // El semáforo NO se dispone: la conexión puede seguir viva y llamar a
                // AbortRecv sobre este flujo —y un lector que aún espera debe despertar—.
            }
        }

        base.Dispose(disposing);
    }
}
