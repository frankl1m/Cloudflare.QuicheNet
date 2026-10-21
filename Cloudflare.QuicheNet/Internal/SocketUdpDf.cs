using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// Las opciones DF de un socket UDP, compartidas por QUIC, KCP, TURN y las salidas UDP.
    /// <para>
    /// <b>DF.</b> El PMTUD de quiche (RFC 8899) sube el tamaño de los paquetes con sondas que el par confirma, y para eso la
    /// sonda que no cabe tiene que perderse, no trocearse: troceada llega, quiche da el tamaño por bueno, y desde entonces cada
    /// paquete va en trozos y se pierde entero si se pierde uno. Por eso el socket propio de una conexión, o de un puerto, lleva
    /// DF. Se pone según su familia: la propiedad de .NET solo sabe de IPv4, y en un socket IPv6 lanzaba —se quedaba sin DF,
    /// mientras su configuración se escribía como si lo llevara, porque se miraba una vez con un socket IPv4—. Lo que diga el
    /// sistema se anota por socket (<see cref="ConDf"/>), y con eso cada transporte decide su techo.
    /// </para>
    /// <para>
    /// El UDP de un puerto «auto» no lleva DF: por él salen también las respuestas del UDP de Shadowsocks, que pueden ser
    /// mayores que el camino —un WireGuard por dentro, una respuesta de DNS grande— y que con DF no saldrían. Su QUIC mide por
    /// lo que le llega del par.
    /// </para>
    /// </summary>
    internal static class SocketUdpDf
    {
        /// <summary>Los sockets a los que se les puso DF: ver <see cref="ConDf"/>.</summary>
        private static readonly ConditionalWeakTable<Socket, object> _conDf = new();
        private static readonly object Marca = new();

        /// <summary>¿Lleva DF este socket? Lo anota <see cref="SinFragmentar"/>.</summary>
        public static bool ConDf(Socket socket) => _conDf.TryGetValue(socket, out _);

        /// <summary>
        /// Pone DF en el socket, con la opción de su familia. Falso si el sistema no lo deja: el socket sigue sirviendo, sin DF.
        /// <list type="bullet">
        /// <item>IPv4: la propiedad <see cref="Socket.DontFragment"/> de .NET —<c>IP_DONTFRAGMENT</c> en Windows,
        /// <c>IP_MTU_DISCOVER</c> con <c>IP_PMTUDISC_DO</c> en Linux—.</item>
        /// <item>IPv6, que no fragmenta en el camino pero sí en el propio equipo: <c>IPV6_DONTFRAG</c> (14) en Windows,
        /// <c>IPV6_MTU_DISCOVER</c> con <c>IPV6_PMTUDISC_DO</c> en Linux y Android, e <c>IPV6_DONTFRAG</c> (62) en macOS e
        /// iOS. Con ella, lo que no cabe por la interfaz falla al mandarlo, como en IPv4.</item>
        /// </list>
        /// </summary>
        public static bool SinFragmentar(Socket socket)
        {
            bool puesto;
            try
            {
                if (socket.AddressFamily == AddressFamily.InterNetwork)
                {
                    socket.DontFragment = true;
                    puesto = true;
                }
                else if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    puesto = DfDeIPv6(socket, poner: true);
                    // Un socket de las dos familias manda también a IPv4: lo suyo, con la opción de IPv4, si la deja.
                    if (puesto && socket.DualMode)
                        puesto = OpcionDfDeIPv4() is { } o && OpcionEntera(socket, o.Nivel, o.Nombre, o.Valor);
                }
                else puesto = false;
            }
            catch (Exception ex) when (ex is SocketException or NotSupportedException or PlatformNotSupportedException
                                           or ObjectDisposedException)
            {
                puesto = false;
            }

            if (puesto) _conDf.AddOrUpdate(socket, Marca);
            return puesto;
        }

        /// <summary>
        /// Le quita el DF a un socket que lo lleva, porque el sistema rechaza por tamaño un datagrama que no pasa del mínimo de
        /// QUIC: la ruta cambió por debajo —una VPN que se enciende con la conexión abierta— y lo que el protocolo exige que
        /// quepa no cabe. Sin DF lo trocea el propio equipo; el socket sigue sirviendo, y sus conexiones nuevas miden por lo
        /// que llega del par. Falso si no lo llevaba o el sistema no lo deja.
        /// </summary>
        public static bool Fragmentar(Socket socket)
        {
            if (!ConDf(socket)) return false;
            bool quitado;
            try
            {
                if (socket.AddressFamily == AddressFamily.InterNetwork)
                {
                    socket.DontFragment = false;
                    quitado = true;
                }
                else
                {
                    // Apagada, la opción de cada sistema vale 0: IPV6_DONTFRAG e IP_DONTFRAG(MENT) en 0, y en Linux
                    // IPV6_PMTUDISC_DONT e IP_PMTUDISC_DONT.
                    quitado = DfDeIPv6(socket, poner: false);
                    if (quitado && socket.DualMode)
                        quitado = OpcionDfDeIPv4() is { } o && OpcionEntera(socket, o.Nivel, o.Nombre, 0);
                }
            }
            catch (Exception ex) when (ex is SocketException or NotSupportedException or PlatformNotSupportedException
                                           or ObjectDisposedException)
            {
                quitado = false;
            }

            if (quitado) _conDf.Remove(socket);
            return quitado;
        }

        /// <summary>
        /// Para las pruebas: el sistema rechaza por tamaño (<see cref="SocketError.MessageSize"/>) lo que mande con DF un socket
        /// de un datagrama de esa longitud, como por una ruta más estrecha que el bucle local.
        /// </summary>
        internal static Func<int, bool>? RechazoSimulado { get; set; }

        /// <summary>Lanza el rechazo de <see cref="RechazoSimulado"/> si toca: lo llaman los envíos de los sockets con DF.</summary>
        internal static void SimularRechazo(Socket socket, int largo)
        {
            if (RechazoSimulado is { } rechazo && ConDf(socket) && rechazo(largo))
                throw new SocketException((int)SocketError.MessageSize);
        }

        /// <summary>La opción de DF para lo que manda a IPv4 un socket de las dos familias, en este sistema: ver la de abajo.</summary>
        internal static (int Nivel, int Nombre, int Valor)? OpcionDfDeIPv4()
            => OpcionDfDeIPv4(OperatingSystem.IsWindows(), OperatingSystem.IsLinux() || OperatingSystem.IsAndroid(),
                              OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()
                              || OperatingSystem.IsTvOS());

        /// <summary>
        /// La opción de DF de IPv4, a pelo, en cada sistema: <c>IP_DONTFRAGMENT</c> (14) en Windows, <c>IP_MTU_DISCOVER</c> con
        /// <c>IP_PMTUDISC_DO</c> (10, 2) en Linux y Android, e <c>IP_DONTFRAG</c> (28) en los de Apple. En los demás, ninguna: el 10
        /// de <c>IPPROTO_IP</c> es otra cosa en cada uno —en macOS e iOS, <c>IP_MULTICAST_TTL</c>—, y se ponía en todo lo que no
        /// fuera Windows: la llamada salía bien, el socket se anotaba con DF sin llevarlo, y su conexión buscaba como si lo
        /// llevara.
        /// </summary>
        internal static (int Nivel, int Nombre, int Valor)? OpcionDfDeIPv4(bool windows, bool linux, bool apple)
            => windows ? (0, 14, 1) : linux ? (0, 10, 2) : apple ? (0, 28, 1) : null;

        private static bool DfDeIPv6(Socket socket, bool poner)
        {
            if (OperatingSystem.IsWindows()) return OpcionEntera(socket, 41, 14, poner ? 1 : 0);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) return OpcionEntera(socket, 41, 23, poner ? 2 : 0);
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()
                || OperatingSystem.IsTvOS()) return OpcionEntera(socket, 41, 62, poner ? 1 : 0);
            return false;
        }

        /// <summary>Una opción entera a pelo, con el nivel y el número del sistema. Falso si no la deja.</summary>
        private static bool OpcionEntera(Socket socket, int nivel, int nombre, int valor)
        {
            Span<byte> v = stackalloc byte[sizeof(int)];
            BitConverter.TryWriteBytes(v, valor);
            try
            {
                socket.SetRawSocketOption(nivel, nombre, v);
                return true;
            }
            catch (SocketException) { return false; }
        }

    }
}
