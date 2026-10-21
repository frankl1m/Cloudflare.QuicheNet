namespace Cloudflare.QuicheNet;

/// <summary>Qué pasó al meter un datagrama en quiche.</summary>
internal enum ResultadoDatagrama
{
    /// <summary>Encolado, y la bomba de envío ya está avisada.</summary>
    Encolado,

    /// <summary>La cola de envío está llena: como en UDP, se pierde, o se espera sitio.</summary>
    ColaLlena,

    /// <summary>No cabe en un paquete con el tamaño de ahora (<c>MaxDatagramSize</c>).</summary>
    Grande,

    /// <summary>El par no admite datagramas, o aún no se sabe porque el saludo no acabó.</summary>
    SinSoporte,

    /// <summary>La conexión está cerrada o liberada.</summary>
    Cerrada,
}
