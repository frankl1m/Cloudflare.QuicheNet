using System.Runtime.InteropServices;
using Cloudflare.Quiche;
using Cloudflare.QuicheNet.Internal;
using Cloudflare.QuicheNet.Internal.Quic;

namespace Cloudflare.QuicheNet.Tests;
internal static class RegressionTests
{
    public static void Register()
    {
        Harness.Test("public ABI retains existing connection and listener signatures", () =>
        {
            Type[] connect = [typeof(System.Net.Sockets.Socket), typeof(System.Net.EndPoint), typeof(QuicheConfig),
                typeof(string), typeof(byte[]), typeof(CancellationToken)];
            Harness.True(typeof(QuicheConnection).GetMethod("ConnectAsync", connect) is not null, "original connect overload");
            Harness.True(typeof(QuicheListener).GetConstructor([typeof(System.Net.Sockets.Socket), typeof(QuicheConfig)]) is not null,
                "original listener constructor");
            Harness.Equal(typeof(Task<byte[]>), typeof(QuicheConnection).GetMethod("ReceiveDatagramAsync")!.ReturnType,
                "datagram Task return type");
            Harness.True(typeof(QuicheConfig).GetProperty("CcAlgorithm")!.CanRead, "congestion getter");
            Harness.True(typeof(QuicheConfig).GetProperty("MaxConnectionWindow") is not null, "connection flow-control window");
            Harness.True(typeof(QuicheConfig).GetProperty("MaxStreamWindow") is not null, "stream flow-control window");
        });
        Harness.Test("64-bit QUIC FFI layout matches Windows and Apple/Linux ABI", () =>
        {
            if (IntPtr.Size != 8) return;
            Harness.Equal(128, Marshal.SizeOf<sockaddr_storage>(), "sockaddr_storage");
            Harness.Equal(16, Marshal.SizeOf<timespec>(), "timespec");
            Harness.Equal(272, Marshal.OffsetOf<quiche_send_info>(nameof(quiche_send_info.at)).ToInt32(), "send timestamp offset");
            Harness.Equal(288, Marshal.SizeOf<quiche_send_info>(), "send info");
        });
        Harness.Test("Darwin ARM32 uses clang-verified four-byte QUIC counter alignment", () =>
        {
            bool aligned = DisposicionUnix32.UsaAlineacionA8(Architecture.Arm, darwin: true);
            Harness.True(!aligned, "Darwin ARMv7 differs from AAPCS");
            Harness.True(DisposicionUnix32.UsaAlineacionA8(Architecture.Arm, darwin: false), "Linux ARM AAPCS retained");
            Harness.Equal(396, DisposicionUnix32.TamañoPathStats(aligned), "Apple ARM32 path_stats sizeof");
            Harness.Equal(172, DisposicionUnix32.TamañoStats(aligned), "Apple ARM32 stats sizeof");
            byte[] path = new byte[DisposicionUnix32.TamañoDeBufer];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(path.AsSpan(300), 123456789UL);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(path.AsSpan(372), 987654321UL);
            var decodedPath = DisposicionUnix32.PathStats(path, aligned);
            Harness.Equal(123456789UL, decodedPath.rtt, "clang Apple rtt offset300");
            Harness.Equal(987654321UL, decodedPath.delivery_rate, "clang Apple delivery_rate offset372");
            byte[] stats = new byte[DisposicionUnix32.TamañoDeBufer];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(stats.AsSpan(20), 87654321UL);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(stats.AsSpan(160), 1234UL);
            var decodedStats = DisposicionUnix32.Stats(stats, aligned);
            Harness.Equal(87654321UL, decodedStats.sent_bytes, "clang Apple sent_bytes offset20");
            Harness.Equal(1234UL, decodedStats.bytes_in_flight_duration_msec, "clang Apple duration offset160");
        });
        Harness.Test("HTTP/3 header bytes survive Latin1 and Unicode round trips", () =>
        {
            byte[] raw = [0x41, 0x80, 0xE9, 0xFF];
            string text = BytesDeCabecera.Leer(raw);
            byte[] encoded = new byte[BytesDeCabecera.Contar(text)];
            BytesDeCabecera.Escribir(text, encoded);
            Harness.True(raw.AsSpan().SequenceEqual(encoded), "raw headers preserved");
            byte[] unicode = new byte[BytesDeCabecera.Contar("č")];
            BytesDeCabecera.Escribir("č", unicode);
            Harness.True(unicode.AsSpan().SequenceEqual(new byte[] { 0xC4, 0x8D }), "Unicode is UTF8, never a truncated CR");
        });
        Harness.Test("datagram queue drops oldest and wakes pending consumer on completion", async () =>
        {
            var queue = new ColaAcotada<int>(2, QueuePolicy.DescartarViejos);
            queue.TryEscribir(1); queue.TryEscribir(2); queue.TryEscribir(3);
            Harness.True(queue.TryLeer(out var first) && first == 2, "oldest dropped");
            Harness.True(queue.TryLeer(out var second) && second == 3, "newest preserved");
            var pending = queue.EsperarAsync(default);
            queue.Completar();
            Harness.True(!await pending, "completion wakes reader");
        });
        Harness.Test("pump notifications retain signals and expire waits", async () =>
        {
            var signal = new AvisoDeBomba();
            signal.Avisar();
            Harness.True(await signal.EsperarAsync(0, default), "retained notification");
            var wait = signal.EsperarAsync(1, default);
            signal.Vigilar(Environment.TickCount64 + 10);
            Harness.True(!await wait, "deadline notification");
        });
        Harness.Test("rejecting unavailable or replayed 0-RTT preserves Initial, Handshake and 1-RTT packets", () =>
        {
            byte[] initial = [0xC0, 0, 0, 0, 1, 1, 7, 0, 0, 2, 0, 0x31];
            byte[] early = [0xD0, 0, 0, 0, 1, 1, 7, 0, 2, 0, 0x32];
            byte[] handshake = [0xE0, 0, 0, 0, 1, 1, 7, 0, 2, 0, 0x33];
            byte[] oneRtt = [0x40, 0x71, 0x72];
            byte[] coalesced = initial.Concat(early).Concat(handshake).Concat(oneRtt).ToArray();
            Harness.True(AntiRepeticionCeroRtt.LlevaCeroRtt(coalesced), "fixture includes early data");
            int remaining = AntiRepeticionCeroRtt.QuitarCeroRtt(coalesced);
            byte[] expected = initial.Concat(handshake).Concat(oneRtt).ToArray();
            Harness.True(coalesced.AsSpan(0, remaining).SequenceEqual(expected), "only 0-RTT was discarded");
            Harness.True(!AntiRepeticionCeroRtt.LlevaCeroRtt(coalesced.AsSpan(0, remaining)), "no early data remains");
            Harness.Equal(initial.Length, AntiRepeticionCeroRtt.QuitarCeroRtt(initial), "ordinary Initial is preserved");
        });
        Harness.Test("0-RTT replay cache rejects repeated ClientHello randomness", () =>
        {
            byte[] random = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            Harness.True(AntiRepeticionCeroRtt.Admitir(random), "first admission");
            Harness.True(!AntiRepeticionCeroRtt.Admitir(random), "replay rejected");
            Harness.True(!AntiRepeticionCeroRtt.Admitir(null), "unreadable randomness rejected");
        });
    }
}
