using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Cloudflare.Quiche;

namespace Cloudflare.QuicheNet;

/// <summary>
/// El tipo de suceso que <see cref="QuicheH3Conn.Poll"/> puede devolver.
/// <para>
/// Son los valores de <c>quiche_h3_event_type</c> del motor, tal cual: el HTTP/3 de
/// quiche vive en Rust —QPACK, las cabeceras y el enmarcado— y esto es lo que sale
/// de su bomba de sucesos.
/// </para>
/// </summary>
public enum QuicheH3EventType
{
    Headers       = 0,
    Data          = 1,
    Finished      = 2,
    GoAway        = 3,
    Reset         = 4,
    PriorityUpdate = 5,
}

/// <summary>
/// El ajuste del HTTP/3 de quiche, antes de colgarlo de una conexión QUIC.
/// </summary>
public sealed class QuicheH3Config : IDisposable
{
    internal unsafe quiche_h3_config* NativePtr;

    public unsafe QuicheH3Config()
    {
        NativePtr = NativeMethods.quiche_h3_config_new();
        if (NativePtr is null)
            throw new InvalidOperationException("quiche: no se pudo crear la configuración h3");
    }

    /// <summary>Tope de la sección de campos (cabeceras) que se acepta descodificar.</summary>
    public ulong MaxFieldSectionSize
    {
        set
        {
            unsafe { NativeMethods.quiche_h3_config_set_max_field_section_size(NativePtr, value); }
        }
    }

    /// <summary>Tope de la tabla dinámica de QPACK del par.</summary>
    public ulong QpackMaxTableCapacity
    {
        set
        {
            unsafe { NativeMethods.quiche_h3_config_set_qpack_max_table_capacity(NativePtr, value); }
        }
    }

    /// <summary>Flujos que se dejan bloqueados esperando entrada de QPACK.</summary>
    public ulong QpackBlockedStreams
    {
        set
        {
            unsafe { NativeMethods.quiche_h3_config_set_qpack_blocked_streams(NativePtr, value); }
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            unsafe
            {
                if (NativePtr is not null)
                {
                    NativeMethods.quiche_h3_config_free(NativePtr);
                    NativePtr = null;
                }
            }
        }
    }
}

/// <summary>
/// El HTTP/3 de quiche, colgado de una <see cref="QuicheConnection"/> ya establecida.
/// <para>
/// No posee la conexión QUIC —la toma prestada, como hace el propio motor—: los paquetes
/// los sigue bombeando <see cref="QuicheConnection"/> y aquí solo se sondean los sucesos
/// h3 que dejan. Las bombas de flujos crudos de la conexión tienen que estar apagadas
/// cuando esto vive, o le robarían los bytes de los flujos.
/// </para>
/// </summary>
public sealed class QuicheH3Conn : IDisposable
{
    private readonly QuicheConnection _quic;
    private unsafe quiche_h3_conn* _conn;
    private bool _disposed;

    private unsafe QuicheH3Conn(QuicheConnection quic, quiche_h3_conn* conn)
    {
        _quic = quic;
        _conn = conn;
    }

    /// <summary>
    /// Cuelga el HTTP/3 de una conexión QUIC. Devuelve <c>null</c> si el motor no pudo
    /// —por ejemplo, en el lado cliente, si la conexión aún no está establecida—.
    /// </summary>
    public static QuicheH3Conn? Crear(QuicheConnection quic, QuicheH3Config config)
    {
        unsafe
        {
            lock (quic.Candado)
            {
                quiche_h3_conn* conn = NativeMethods.quiche_h3_conn_new_with_transport(
                    quic.NativePtr, config.NativePtr);
                if (conn is null) return null;
                return new QuicheH3Conn(quic, conn);
            }
        }
    }

    /// <summary>
    /// Sondea el siguiente suceso. Devuelve el flujo al que pertenece, o un error
    /// (<see cref="QuicheError.QUICHE_ERR_DONE"/> cuando no queda nada que sondear).
    /// El suceso sale por <paramref name="evento"/> y hay que soltarlo con
    /// <see cref="Soltar"/>.
    /// </summary>
    public unsafe long Poll(out IntPtr evento)
    {
        lock (_quic.Candado)
        {
            // Sin h3 o sin conexión —ya soltados: la bomba despertó tarde, tras un Dispose que no la esperó— no hay nada que
            // sondear. Con punteros nulos, quiche_h3_conn_poll era una violación de acceso y el proceso entero se caía.
            if (_conn is null || _quic.NativePtr is null)
            {
                evento = IntPtr.Zero;
                return (long)QuicheError.QUICHE_ERR_DONE;
            }

            quiche_h3_event* ev = null;
            long id = NativeMethods.quiche_h3_conn_poll(_conn, _quic.NativePtr, &ev);
            evento = (IntPtr)ev;
            return id;
        }
    }

    /// <summary>El tipo del suceso.</summary>
    public static unsafe QuicheH3EventType Tipo(IntPtr evento)
        => (QuicheH3EventType)NativeMethods.quiche_h3_event_type((quiche_h3_event*)evento);

    /// <summary>Suelta un suceso devuelto por <see cref="Poll"/>.</summary>
    public static unsafe void Soltar(IntPtr evento)
        => NativeMethods.quiche_h3_event_free((quiche_h3_event*)evento);

    // ── Cabeceras ────────────────────────────────────────────────────────────
    // El descodificador de QPACK llama por cada campo; aquí se copian a la lista
    // que viaja en argp —nada compartido entre conexiones—. El callback va como
    // delegado inmovilizado con su convención, para no depender de la
    // [UnmanagedCallersOnly], que no casa con el puntero generado.

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int Recogedor(byte* name, nuint nameLen, byte* value, nuint valueLen, void* argp);

    private static readonly Recogedor _recogedor;
    private static readonly IntPtr _recogedorPtr;

    static unsafe QuicheH3Conn()
    {
        _recogedor = RecogerImpl;
        _recogedorPtr = Marshal.GetFunctionPointerForDelegate(_recogedor);
    }

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl,
               EntryPoint = "_quiche_h3_event_for_each_header")]
    private static unsafe extern int ParaCadaCabecera(quiche_h3_event* ev, IntPtr cb, void* argp);

#if IOS || MACCATALYST || TVOS
    [ObjCRuntime.MonoPInvokeCallback(typeof(Recogedor))]
#endif
    private static unsafe int RecogerImpl(byte* name, nuint nameLen, byte* value, nuint valueLen, void* argp)
    {
        var lista = (List<(string, string)>)GCHandle.FromIntPtr((IntPtr)argp).Target!;
        lista.Add((
            // Latin-1, como h1.1 y h2: los bytes del par, uno a uno. En UTF-8 salían caracteres
            // por encima de U+00FF que ningún otro camino sabía volver a escribir.
            Cloudflare.QuicheNet.Internal.BytesDeCabecera.Leer(new ReadOnlySpan<byte>(name, (int)nameLen)),
            Cloudflare.QuicheNet.Internal.BytesDeCabecera.Leer(new ReadOnlySpan<byte>(value, (int)valueLen))));
        return 0;
    }

    /// <summary>Las cabeceras de un suceso HEADERS, ya descodificadas por QPACK.</summary>
    public static unsafe List<(string Nombre, string Valor)> Cabeceras(IntPtr evento)
    {
        var lista = new List<(string, string)>();
        var manejador = GCHandle.Alloc(lista);
        try
        {
            ParaCadaCabecera(
                (quiche_h3_event*)evento, _recogedorPtr, (void*)GCHandle.ToIntPtr(manejador));
        }
        finally
        {
            manejador.Free();
        }
        return lista;
    }

    // ── Cliente ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Manda una petición. Devuelve el flujo que la lleva, o un error negativo.
    /// Las cabeceras se codifican con QPACK dentro del motor.
    /// </summary>
    public long SendRequest(IReadOnlyList<(string Nombre, string Valor)> headers, bool fin)
        => EnviarCabeceras(0, headers, fin, esRespuesta: false);

    /// <summary>Manda el cuerpo de una petición (o su FIN vacío) y devuelve lo aceptado.</summary>
    public unsafe long SendBody(ulong streamId, ReadOnlySpan<byte> cuerpo, bool fin)
    {
        lock (_quic.Candado)
        {
            // La comprobación va DENTRO del cerrojo —el mismo que toma el Dispose antes
            // de soltar el h3 nativo—: fuera habría una ventana entre comprobar y llamar
            // con el puntero ya liberado, y el motor corrompería memoria nativa.
            if (_conn is null)
                throw new IOException("h3: la conexión se acabó");

            if (cuerpo.IsEmpty)
            {
                // El FIN vacío va con puntero válido: el motor hace slice::from_raw_parts
                // y un nulo de cero bytes es UB en Rust aunque la longitud sea 0.
                byte centinela = 0;
                byte* ptr = &centinela;
                return NativeMethods.quiche_h3_send_body(
                    _conn, _quic.NativePtr, streamId, ptr, 0, fin);
            }

            fixed (byte* ptr = cuerpo)
            {
                return NativeMethods.quiche_h3_send_body(
                    _conn, _quic.NativePtr, streamId, ptr, (nuint)cuerpo.Length, fin);
            }
        }
    }

    /// <summary>Lee lo que haya del cuerpo de un suceso DATA. Positivo = bytes; negativo = error.</summary>
    public unsafe long RecvBody(ulong streamId, Span<byte> destino)
    {
        lock (_quic.Candado)
        {
            if (_conn is null) return (long)QuicheError.QUICHE_ERR_DONE;

            fixed (byte* ptr = destino)
            {
                return NativeMethods.quiche_h3_recv_body(
                    _conn, _quic.NativePtr, streamId, ptr, (nuint)destino.Length);
            }
        }
    }

    // ── Servidor ─────────────────────────────────────────────────────────────

    /// <summary>Contesta una petición con sus cabeceras. 0 = bien; negativo = error.</summary>
    /// <summary>
    /// Manda la cabecera de una respuesta, <b>incremental</b>: las respuestas de la conexión se turnan. quiche, con su
    /// <c>send_response</c>, les pone lo que RFC 9218 da a una respuesta cualquiera —urgencia 3, no incremental—, y las no
    /// incrementales de la misma urgencia salen de una en una por número de flujo: la bajada de un túnel, que no acaba,
    /// dejaba sin turno a todas las de detrás —otras bajadas, y las sesiones nuevas de xhttp o los CONNECT, que vencían su
    /// plazo esperando la cabecera—. Las de este servidor son túneles y cuerpos que van a trozos: turnarse es lo suyo.
    /// </summary>
    public long SendResponse(ulong streamId, IReadOnlyList<(string Nombre, string Valor)> headers, bool fin)
        => EnviarCabeceras(streamId, headers, fin, esRespuesta: true);

    /// <summary>La prioridad de las respuestas: la urgencia por omisión de RFC 9218, incremental. Ver <see cref="SendResponse"/>.</summary>
    private const byte UrgenciaDeRespuesta = 3;

    /// <summary>Manda unas cabeceras finales —trailers— tras el cuerpo, y el FIN si <paramref name="fin"/>. 0 = bien; negativo = error.</summary>
    public long SendTrailers(ulong streamId, IReadOnlyList<(string Nombre, string Valor)> headers, bool fin)
        => EnviarCabeceras(streamId, headers, fin, esRespuesta: false, esTrailer: true);

    /// <summary>Avisa del fin de la conexión h3 a quien aún esté abriendo peticiones.</summary>
    public unsafe long SendGoAway(ulong id)
    {
        lock (_quic.Candado)
        {
            if (_conn is null) return (long)QuicheError.QUICHE_ERR_DONE;
            return NativeMethods.quiche_h3_send_goaway(_conn, _quic.NativePtr, id);
        }
    }

    // ── Común ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Codifica cabeceras y las manda. Todo el material se inmoviliza UNA vez —nombres
    /// y valores concatenados en un solo búfer— y el motor lo codifica síncrono en la
    /// llamada, así que los pines no sobreviven a ella.
    /// </summary>
    private unsafe long EnviarCabeceras(
        ulong streamId, IReadOnlyList<(string Nombre, string Valor)> headers, bool fin, bool esRespuesta, bool esTrailer = false)
    {
        // Un solo búfer: nombre, valor, nombre, valor… con sus tamaños en las
        // estructuras. Así basta un `fixed` por llamada, con el número de cabeceras
        // que sea.
        int total = 0;
        foreach (var (n, v) in headers)
            total += Cloudflare.QuicheNet.Internal.BytesDeCabecera.Contar(n) + Cloudflare.QuicheNet.Internal.BytesDeCabecera.Contar(v);

        byte[] plano = new byte[total];
        var tabla = new quiche_h3_header[headers.Count];

        int pos = 0;
        for (int i = 0; i < headers.Count; i++)
        {
            var (n, v) = headers[i];
            int nLen = Cloudflare.QuicheNet.Internal.BytesDeCabecera.Escribir(n, plano.AsSpan(pos));
            int vLen = Cloudflare.QuicheNet.Internal.BytesDeCabecera.Escribir(v, plano.AsSpan(pos + nLen));
            tabla[i] = new quiche_h3_header
            {
                name      = null,   // se rellena bajo el fixed, que da la dirección real
                name_len  = (nuint)nLen,
                value     = null,
                value_len = (nuint)vLen,
            };
            pos += nLen + vLen;
        }

        lock (_quic.Candado)
        {
            // Dentro del cerrojo, como en SendBody: fuera habría una ventana entre
            // comprobar y llamar con el puntero ya liberado.
            if (_conn is null)
                throw new IOException("h3: la conexión se acabó");

            fixed (byte* pPlano = plano)
            fixed (quiche_h3_header* pTabla = tabla)
            {
                int p = 0;
                for (int i = 0; i < tabla.Length; i++)
                {
                    pTabla[i].name  = pPlano + p;
                    pTabla[i].value = pPlano + p + (int)tabla[i].name_len;
                    p += (int)(tabla[i].name_len + tabla[i].value_len);
                }

                if (esTrailer)
                    return NativeMethods.quiche_h3_send_additional_headers(
                        _conn, _quic.NativePtr, streamId, pTabla, (nuint)tabla.Length, true, fin);

                if (esRespuesta)
                {
                    var prioridad = new quiche_h3_priority { urgency = UrgenciaDeRespuesta, incremental = true };
                    return NativeMethods.quiche_h3_send_response_with_priority(
                        _conn, _quic.NativePtr, streamId, pTabla, (nuint)tabla.Length, &prioridad, fin);
                }
                return NativeMethods.quiche_h3_send_request(
                    _conn, _quic.NativePtr, pTabla, (nuint)tabla.Length, fin);
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            unsafe
            {
                lock (_quic.Candado)
                {
                    if (_conn is not null)
                    {
                        NativeMethods.quiche_h3_conn_free(_conn);
                        _conn = null;
                    }
                }
            }
        }
    }
}
