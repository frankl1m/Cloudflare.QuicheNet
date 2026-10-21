using Foundation;
using UIKit;
using Cloudflare.QuicheNet;
using System.Runtime.InteropServices;

Console.WriteLine($"Managed {Environment.Version}; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; QUIC {QuicheLibrary.VersionCode}");
for (var i = 0; i < 100; i++)
{
    using var config = new QuicheConfig();
    config.ApplicationProtocols = ["h3"];
    using var h3 = new QuicheH3Config();
}
Console.WriteLine("QUIC/TLS-ALPN/H3 static imports: 100 cycles OK");
if (args.Contains("--native-smoke")) return;
UIApplication.Main(args, null, typeof(AppDelegate));

[Register("AppDelegate")]
public class AppDelegate : UIApplicationDelegate { }
