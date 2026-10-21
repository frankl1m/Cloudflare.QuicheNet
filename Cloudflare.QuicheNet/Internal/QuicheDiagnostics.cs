using System.Threading;
namespace Cloudflare.QuicheNet;

/// <summary>Process-wide counters for connection diagnostics.</summary>
public static class QuicheDiagnostics
{
    internal static long EarlyDataCount, StalledShutdownCount;
    public static long EarlyDataConnections => Interlocked.Read(ref EarlyDataCount);
    public static long StalledShutdowns => Interlocked.Read(ref StalledShutdownCount);
}
