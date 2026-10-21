using System;
using System.Net;
using System.Net.Sockets;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>Datos crudos del camino UDP; el transporte decide cómo usarlos para su PMTUD.</summary>
    internal readonly struct PistaCaminoUdp
    {
        internal enum Clase { Desconocido, Socket, Medido, SinTrozos }

        internal Clase Tipo { get; }
        internal Socket? Socket { get; }
        internal EndPoint? Remoto { get; }
        internal int? Carga { get; }
        internal Func<int?>? Ahora { get; }

        private PistaCaminoUdp(Clase tipo, Socket? socket, EndPoint? remoto, int? carga, Func<int?>? ahora)
        {
            Tipo = tipo;
            Socket = socket;
            Remoto = remoto;
            Carga = carga;
            Ahora = ahora;
        }

        internal static PistaCaminoUdp DeSocket(Socket socket, EndPoint remoto)
            => new(Clase.Socket, socket, remoto, null, null);

        internal static PistaCaminoUdp Medido(Func<int?> ahora)
            => new(Clase.Medido, null, null, null, ahora);

        internal static PistaCaminoUdp SinTrozos(int? carga)
            => new(Clase.SinTrozos, null, null, carga, null);
    }
}
