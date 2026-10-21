using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Cloudflare.Quiche;

namespace Cloudflare.QuicheNet.Tests;
internal static class NativeTests
{
    public static void Register()
    {
        Harness.Test("native version and HTTP/3 config load", () =>
        {
            NativeMethods.LibraryDirPath = Environment.GetEnvironmentVariable("QUICHE_NATIVE_DIR")
                ?? Path.GetFullPath("Cloudflare.QuicheNet.NativeAssets.macOS/runtimes/osx-x64/native");
            Harness.True(!string.IsNullOrEmpty(QuicheLibrary.VersionCode), "native version");
            using var h3 = new QuicheH3Config { MaxFieldSectionSize = 65536 };
        });
        Harness.Test("QUIC loopback: streams, FIN, datagrams and cancellation", RoundTripAsync);
    }

    private static QuicheConfig Config() => new()
    {
        ApplicationProtocols = ["quichenet-test"],
        ShouldVerifyPeer = false,
        MaxIdleTimeout = 15000,
        MaxInitialBidiStreams = 16,
        MaxInitialUniStreams = 16,
        MaxInitialDataSize = 8 * 1024 * 1024,
        MaxInitialLocalBidiStreamDataSize = 2 * 1024 * 1024,
        MaxInitialRemoteBidiStreamDataSize = 2 * 1024 * 1024,
        MaxInitialUniStreamDataSize = 2 * 1024 * 1024,
        DatagramOptions = new() { Enabled = true, ReceiveQueueLength = 128, SendQueueLength = 128 }
    };

    private static async Task RoundTripAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        string temp = Path.Combine(Path.GetTempPath(), "quichenet-" + Guid.NewGuid());
        Directory.CreateDirectory(temp);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // quiche consumes separate PEM files; attaching the private key to a
        // macOS keychain certificate is unnecessary and fails on old runtimes.
        using var cert = request.Create(request.SubjectName,
            X509SignatureGenerator.CreateForRSA(key, RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1),
            RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(temp, "cert.pem"), cert.ExportCertificatePem());
        File.WriteAllText(Path.Combine(temp, "key.pem"), key.ExportPkcs8PrivateKeyPem());
        try
        {
            using var serverConfig = Config();
            using var clientConfig = Config();
            serverConfig.LoadCertificateChainFromPemFile(Path.Combine(temp, "cert.pem"));
            serverConfig.LoadPrivateKeyFromPemFile(Path.Combine(temp, "key.pem"));
            using var serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            serverSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            clientSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var listener = new QuicheListener(serverSocket, serverConfig);
            using var listening = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var listenTask = listener.ListenAsync(listening.Token);
            try
            {
                var accept = listener.AcceptAsync(ct);
                using var client = await QuicheConnection.ConnectAsync(clientSocket, serverSocket.LocalEndPoint!, clientConfig,
                    hostname: "localhost", cancellationToken: ct);
                using var server = await accept;
                Harness.True(client.IsEstablished && server.IsEstablished, "handshake completed");
                byte[] label = System.Text.Encoding.ASCII.GetBytes("quichenet regression");
                byte[] clientKey = client.ExportKeyingMaterial(label, ReadOnlySpan<byte>.Empty, 32);
                byte[] serverKey = server.ExportKeyingMaterial(label, ReadOnlySpan<byte>.Empty, 32);
                Harness.True(clientKey.AsSpan().SequenceEqual(serverKey), "TLS exporter matches peer");
                byte[] expected = RandomNumberGenerator.GetBytes(512 * 1024);
                using var outgoing = await client.CreateOutboundStreamAsync(QuicheStream.Direction.Bidirectional, ct);
                var send = outgoing.WriteAsync(expected, ct).AsTask();
                using var incoming = await server.AcceptInboundStreamAsync(ct);
                var receive = new MemoryStream();
                var copy = incoming.CopyToAsync(receive, ct);
                await send;
                outgoing.ShutdownWrite();
                await copy;
                Harness.True(expected.AsSpan().SequenceEqual(receive.ToArray()), "stream payload and FIN");
                await incoming.WriteAsync(new byte[] { 4, 2 }, ct);
                incoming.ShutdownWrite();
                byte[] reply = new byte[2];
                await outgoing.ReadExactlyAsync(reply, ct);
                Harness.True(reply.AsSpan().SequenceEqual(new byte[] { 4, 2 }), "half-close preserves reply");
                Harness.Equal(0, await outgoing.ReadAsync(reply, ct), "reply FIN");
                byte[] datagram = RandomNumberGenerator.GetBytes(512);
                for (int i = 0; i < 16; i++)
                {
                    await client.SendDatagramAsync(datagram, ct);
                    var read = await server.ReceiveDatagramAsync(ct);
                    Harness.True(datagram.AsSpan().SequenceEqual(read), "client datagram");
                    await server.SendDatagramAsync(read, ct);
                    var echoed = await client.ReceiveDatagramAsync(ct);
                    Harness.True(datagram.AsSpan().SequenceEqual(echoed), "server datagram");
                }
                using var cancel = new CancellationTokenSource(50);
                try { await client.ReceiveDatagramAsync(cancel.Token); throw new Exception("read did not cancel"); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            }
            finally
            {
                listening.Cancel();
                try { await listenTask; } catch (OperationCanceledException) when (listening.IsCancellationRequested) { }
            }
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}
