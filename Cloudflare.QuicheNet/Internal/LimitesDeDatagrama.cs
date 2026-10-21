namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>Límites de datagrama compartidos por el motor QUIC y los caminos que lo transportan.</summary>
    internal static class LimitesDeDatagrama
    {
        /// <summary>El mínimo del Initial QUIC en bytes (RFC 9000); una ruta menor no puede saludar.</summary>
        internal const int MinimoInicialQuic = 1200;

        /// <summary>Versión original de QUIC (RFC 9000), compartida con el lector de Initial y quiche.</summary>
        internal const uint VersionQuic1 = 0x00000001;

        /// <summary>Longitud máxima de un Connection ID de QUIC v1/v2 (RFC 9000).</summary>
        internal const int MaxConnectionIdQuic = 20;

        /// <summary>Reserva conservadora por datagrama al transportar KCP sobre un salto QUIC.</summary>
        internal const int MargenSaltoQuic = 64;

        /// <summary>Máxima carga UDP admitida para lectura y anuncio QUIC (IPv6).</summary>
        internal const int MaxDatagramaUdp = 65_527;
    }
}
