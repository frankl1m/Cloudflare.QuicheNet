using System;
using System.Net;
using Cloudflare.QuicheNet.Internal;
using Cloudflare.QuicheNet.Internal.Quic;

namespace Cloudflare.QuicheNet
{
    /// <summary>Interpreta la pista UDP del canal solo en el motor QUIC.</summary>
    internal static class TechoDeCanales
    {
        internal static TechoDeQuic TechoHacia(this ICanalDatagramas canal, EndPoint remoto)
            => TechoDeQuic.DePista(canal.PistaHacia(remoto));

        internal static TechoDeQuic TechoHacia(this CanalSocket canal, EndPoint remoto)
            => TechoDeQuic.DePista(canal.PistaHacia(remoto));
    }
}
