using System.Net.Sockets;
namespace Cloudflare.QuicheNet.Internal;
internal static class SocketCompat
{
    public static void DisableUdpConnReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { socket.IOControl(-1744830452, new byte[4], null); } catch (SocketException) { }
        try { socket.IOControl(-1744830449, new byte[4], null); } catch (SocketException) { }
    }
}
