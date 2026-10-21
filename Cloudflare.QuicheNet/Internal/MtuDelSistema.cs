using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// Lo que el sistema deja mandar con DF hacia un destino, en carga UDP: el MTU que sabe de su ruta —el de la interfaz por
    /// la que sale, o el que le dijo un ICMP de esa ruta— menos las cabeceras de IP y de UDP. Es el techo de lo que se busca
    /// por un camino con DF: por encima, el envío falla en el acto, sin gastar red.
    /// <para>
    /// Se pregunta sin mandar nada: un socket de la familia del destino, «conectado» a él —en UDP eso solo elige la ruta—, y
    /// su <c>IP_MTU</c> (<c>IPV6_MTU</c> en IPv6), que Windows y Linux dan en un socket conectado. Donde no —macOS, iOS—, el
    /// MTU de la interfaz que tiene la dirección de origen que eligió la ruta. Por el WARP de esta máquina (MTU 1300) da 1272
    /// en IPv4 y 1252 en IPv6; por una Ethernet, 1472 y 1452; por el bucle local, 65 507 y 65 527.
    /// </para>
    /// <para>
    /// Se guarda un rato por dirección: un servidor lo pregunta por cada cliente que abre, y una ruta no cambia de un saludo
    /// al siguiente. Un cambio de red lo olvida.
    /// </para>
    /// </summary>
    internal static class MtuDelSistema
    {
        /// <summary>La carga UDP más larga de un datagrama IPv4 (65 535 menos 20 de IP y 8 de UDP).</summary>
        public const int MaxIPv4 = 65_507;

        /// <summary>La de IPv6 sin jumbogramas (65 535 de carga IP menos los 8 de UDP).</summary>
        public const int MaxIPv6 = 65_527;

        /// <summary>Lo que se guarda lo preguntado por cada dirección.</summary>
        private static readonly TimeSpan Recuerdo = TimeSpan.FromSeconds(30);

        /// <summary>Direcciones que se recuerdan a la vez: al llenarse se olvidan todas, y cada una se vuelve a preguntar.</summary>
        private const int MaxRecordadas = 4096;

        private static readonly ConcurrentDictionary<IPAddress, (int Carga, long HastaMs)> _sabidas = new();
        private static IDisposable? _alCambiar;

        /// <summary>Para las pruebas: lo que diría el sistema de cada destino, en vez de preguntarle.</summary>
        internal static Func<IPEndPoint, int?>? Simulado { get; set; }

        /// <summary>
        /// La carga UDP más larga que el sistema deja mandar con DF hacia <paramref name="destino"/>; null si no se sabe —sin
        /// ruta hacia él, o un sistema que no lo dice—.
        /// </summary>
        public static int? CargaHacia(IPEndPoint destino)
        {
            if (Simulado is { } simulado) return simulado(destino);

            var ip = destino.Address.IsIPv4MappedToIPv6 ? destino.Address.MapToIPv4() : destino.Address;
            long ahora = Environment.TickCount64;
            if (_sabidas.TryGetValue(ip, out var sabida) && ahora < sabida.HastaMs) return sabida.Carga;

            int? carga = Preguntar(new IPEndPoint(ip, destino.Port == 0 ? 9 : destino.Port));
            if (carga is int c)
            {
                if (_alCambiar is null) Apuntarse();
                if (_sabidas.Count >= MaxRecordadas) _sabidas.Clear();
                _sabidas[ip] = (c, ahora + (long)Recuerdo.TotalMilliseconds);
            }
            return carga;
        }

        /// <summary>
        /// Direcciones de documentación (RFC 5737 y RFC 3849), que no lleva nunca ningún paquete: preguntar hacia ellas da la
        /// ruta de salida del equipo, la que llevaría a cualquier sitio de fuera.
        /// </summary>
        internal static readonly IPEndPoint SalidaV4 = new(IPAddress.Parse("192.0.2.1"), 9);
        internal static readonly IPEndPoint SalidaV6 = new(IPAddress.Parse("2001:db8::1"), 9);

        /// <summary>
        /// La carga UDP más larga que el sistema deja mandar con DF por su ruta de salida (ver <see cref="SalidaV4"/>), sin mandar
        /// nada: lo que se sabe del camino cuando el siguiente salto está en el propio equipo —un SOCKS5 local, un relay en el
        /// mismo equipo— o no hay socket propio al que preguntar, y lo de verdad sale por la red. En IPv6 sin ruta de salida, la
        /// de IPv4. null si no se sabe.
        /// </summary>
        public static int? CargaPorLaSalida(AddressFamily familia)
            => (familia == AddressFamily.InterNetworkV6 ? CargaHacia(SalidaV6) : null) ?? CargaHacia(SalidaV4);

        /// <summary>Un cambio de red: lo sabido de cada ruta ya no vale.</summary>
        private static void Apuntarse()
        {
            lock (_sabidas)
                _alCambiar ??= CambioDeRed.Suscribir(static () => _sabidas.Clear());
        }

        private static int? Preguntar(IPEndPoint destino)
        {
            bool v4 = destino.AddressFamily == AddressFamily.InterNetwork;
            int cabeceras = v4 ? 28 : 48, maximo = v4 ? MaxIPv4 : MaxIPv6;
            try
            {
                using var s = new Socket(destino.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                s.Connect(destino);

                int? mtu = MtuDeLaRuta(s, v4) ?? MtuDeLaInterfaz((s.LocalEndPoint as IPEndPoint)?.Address, v4);
                if (mtu is not int m) return null;

                // Un MTU sin límite —el bucle local de Windows dice 4 294 967 295, que en un int es -1— o mayor que lo que cabe
                // en un datagrama es lo que cabe en un datagrama.
                return m <= cabeceras ? maximo : Math.Min(m - cabeceras, maximo);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException
                                           or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// <c>IP_MTU</c> o <c>IPV6_MTU</c> del socket conectado: el MTU de la ruta, con lo que el sistema ya sepa por ICMP. Los
        /// números son los de Windows (ws2ipdef.h) y Linux (in.h): Android es Linux. null donde no hay.
        /// </summary>
        private static int? MtuDeLaRuta(Socket s, bool v4)
        {
            int nivel = v4 ? 0 : 41, nombre;
            if (OperatingSystem.IsWindows()) nombre = v4 ? 73 : 72;
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) nombre = v4 ? 14 : 24;
            else return null;

            Span<byte> valor = stackalloc byte[sizeof(int)];
            try
            {
                return s.GetRawSocketOption(nivel, nombre, valor) >= sizeof(int) ? BitConverter.ToInt32(valor) : null;
            }
            catch (SocketException) { return null; }
        }

        /// <summary>El MTU de la interfaz que tiene <paramref name="origen"/>: la que eligió la ruta.</summary>
        private static int? MtuDeLaInterfaz(IPAddress? origen, bool v4)
        {
            if (origen is null) return null;
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var props = nic.GetIPProperties();
                    foreach (var u in props.UnicastAddresses)
                    {
                        if (!u.Address.Equals(origen)) continue;
                        return v4 ? props.GetIPv4Properties()?.Mtu : props.GetIPv6Properties()?.Mtu;
                    }
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException
                                           or NotSupportedException or NotImplementedException)
            {
            }
            return null;
        }
    }
}
