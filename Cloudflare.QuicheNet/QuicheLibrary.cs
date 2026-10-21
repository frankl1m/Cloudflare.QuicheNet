using static Cloudflare.Quiche.NativeMethods;

namespace Cloudflare.QuicheNet;

public static class QuicheLibrary
{
    [System.Obsolete("Use MaxDatagramaUdp for receive buffers; this legacy constant retains its original value.")]
    public const int MAX_DATAGRAM_LEN = 6115;

    [System.Obsolete("Buffer allocation is managed internally; this legacy constant retains its original value.")]
    public const int MAX_BUFFER_LEN = 1024 * 1024;

    public const uint PROTOCOL_VERSION = Cloudflare.QuicheNet.Internal.LimitesDeDatagrama.VersionQuic1;

    public const int MAX_CONN_ID_LEN = Cloudflare.QuicheNet.Internal.LimitesDeDatagrama.MaxConnectionIdQuic;

    public const int MIN_CLIENT_INITIAL_LEN = Cloudflare.QuicheNet.Internal.LimitesDeDatagrama.MinimoInicialQuic;

    /// <summary>
    /// Lo más largo que puede traer un datagrama UDP: 65 527 bytes de carga en IPv6 (65 507 en IPv4), y lo más que admite el
    /// parámetro <c>max_udp_payload_size</c> de QUIC. Es lo que se lee y lo que se anuncia que se recibe: se leía con 6115
    /// mientras se anunciaba lo de quiche, 65 527, y lo de un par que se lo creyera llegaba cortado.
    /// </summary>
    public const int MaxDatagramaUdp = Cloudflare.QuicheNet.Internal.LimitesDeDatagrama.MaxDatagramaUdp;

    /// <summary>
    /// El sondeo de los bucles del wrapper. Los datos viajan por colas, así que este
    /// intervalo es la latencia añadida por salto: con 75 ms una sesión de proxy pagaba
    /// un segundo o más por cada ida y vuelta. No es 0 porque un bucle en caliente sin
    /// pausa quemaría CPU en conexiones ociosas.
    /// </summary>
    public const int SONDEO_MS = 5;

    public static string VersionCode
    {
        get
        {
            unsafe
            {
                sbyte* versionCodePtr = (sbyte*)quiche_version();
                return new string(versionCodePtr);
            }
        }
    }
}
