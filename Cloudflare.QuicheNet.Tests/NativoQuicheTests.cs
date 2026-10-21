using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cloudflare.Quiche;
using static Cloudflare.Quiche.UbicacionDelNativo;

namespace Cloudflare.QuicheNet.Tests
{
    /// <summary>
    /// El nativo de quiche fuera de Windows y de los Unix de 64 bits.
    /// <list type="bullet">
    /// <item>Dónde se busca la librería en cada plataforma (<see cref="UbicacionDelNativo"/>). Antes la puerta y el resolvedor
    /// no conocían ni Android —allí <c>IsLinux()</c> es falso— ni el ARM de 32 bits, y en los dos la capa quic quedaba «sin
    /// motor» aunque la librería estuviera.</item>
    /// <item>Cómo se leen sus estructuras en un Unix de 32 bits (<see cref="DisposicionUnix32"/>), donde las de
    /// NativeMethods.g.cs no casan con las del nativo.</item>
    /// </list>
    /// </summary>
    internal static class NativoQuicheTests
    {
        public static void RegisterUnidad()
        {
            Harness.Test("quic/nativo: cada plataforma con nativo da su RID y su librería, y Android carga por nombre", () =>
            {
                Comprobar(Sistema.Android, Architecture.Arm64, "android-arm64", "libquiche_bindgen.so", porNombre: true);
                Comprobar(Sistema.Android, Architecture.Arm,   "android-arm",   "libquiche_bindgen.so", porNombre: true);
                Comprobar(Sistema.Android, Architecture.X86,   "android-x86",   "libquiche_bindgen.so", porNombre: true);
                Comprobar(Sistema.Android, Architecture.X64,   "android-x64",   "libquiche_bindgen.so", porNombre: true);
                Comprobar(Sistema.Linux,   Architecture.Arm,   "linux-arm",     "libquiche_bindgen.so", porNombre: false);

                // Los de antes, igual que antes.
                Comprobar(Sistema.Windows, Architecture.X64,   "win-x64",       "quiche_bindgen.dll",   porNombre: false);
                Comprobar(Sistema.Windows, Architecture.X86,   "win-x86",       "quiche_bindgen.dll",   porNombre: false);
                Comprobar(Sistema.Linux,   Architecture.X64,   "linux-x64",     "libquiche_bindgen.so", porNombre: false);
                Comprobar(Sistema.Linux,   Architecture.Arm64, "linux-arm64",   "libquiche_bindgen.so", porNombre: false);
                Comprobar(Sistema.MacOS,   Architecture.Arm64, "osx-arm64",     "libquiche_bindgen.dylib", porNombre: false);
                Comprobar(Sistema.MacOS,   Architecture.X64, "osx-x64", "libquiche_bindgen.dylib", porNombre: false);
                foreach (var apple in new[] { Sistema.IOS, Sistema.MacCatalyst })
                {
                    Harness.True(Para(apple, Architecture.Arm64) is null, "Apple móvil requiere un nativo propio");
                    Harness.True(Motivo(apple, Architecture.Arm64).Contains(apple.ToString()), "el motivo identifica la plataforma Apple");
                }

                // Donde no hay motor, nada que buscar, y el motivo lo dice.
                Harness.True(Para(Sistema.Otro, Architecture.Arm64) is null, "iOS y los demás no tienen ubicación");
                Harness.True(Para(Sistema.Linux, Architecture.S390x) is null, "una arquitectura sin nativo tampoco");
                Harness.True(Motivo(Sistema.Otro, Architecture.Arm64).Contains("Android"),
                             $"el motivo nombra dónde sí hay motor: {Motivo(Sistema.Otro, Architecture.Arm64)}");
                Harness.True(Motivo(Sistema.Linux, Architecture.S390x).Contains("S390x"),
                             $"y el de la arquitectura, cuál es: {Motivo(Sistema.Linux, Architecture.S390x)}");

                // Este proceso: la puerta y el resolvedor usan la misma ubicación.
                Harness.True(Actual is not null, "en el banco siempre hay ubicación");
                Harness.Equal(Para(SistemaActual, RuntimeInformation.ProcessArchitecture)!.Rid, Actual!.Rid,
                              "la actual es la del sistema y la arquitectura del proceso");
            });

            Harness.Test("quic/nativo: en un Unix de 32 bits, send_info, path_stats y stats se leen con la disposición del nativo", () =>
            {
                // Los offsets de cada campo, tal como los calcula bindgen con clang y las cabeceras de cada destino (los
                // layout-*.rs de quiche-bindgen): ARM es android-arm y linux-arm, x86 es android-x86.
                foreach (bool arm in new[] { true, false })
                {
                    string destino = arm ? "ARM" : "x86";

                    // quiche_send_info: el «at», {i32, i32} en 264. La struct generada lo busca en otro sitio o con otro
                    // tamaño en cualquier proceso, y daba un ritmo de envío que no era el de quiche.
                    byte[] info = Bufer();
                    Escribir(info, 264, 1_234_567);
                    Escribir(info, 268, 890_123_456);
                    timespec at = DisposicionUnix32.At(info);
                    Harness.Equal(1_234_567L, at.tv_sec, $"{destino}: tv_sec del at");
                    Harness.Equal(890_123_456L, (long)at.tv_nsec.Value, $"{destino}: tv_nsec del at");
                    timespec generada = MemoryMarshal.Read<quiche_send_info>(info).at;
                    Harness.True(generada.tv_sec != 1_234_567 || (long)generada.tv_nsec.Value != 890_123_456,
                                 $"{destino}: la struct generada no lee bien el at del nativo de 32 bits");

                    // quiche_path_stats.
                    byte[] ps = Bufer();
                    Escribir(ps, 272, 11u);                                  // recv
                    Escribir(ps, 276, 22u);                                  // sent
                    Escribir(ps, 280, 33u);                                  // lost
                    Escribir(ps, 288, 44u);                                  // total_pto_count
                    Escribir(ps, arm ? 304 : 300, 55_000_000UL);             // rtt
                    Escribir(ps, arm ? 328 : 324, 66_000UL);                 // rttvar
                    Escribir(ps, arm ? 336 : 332, 77_777u);                  // cwnd
                    Escribir(ps, arm ? 344 : 336, 88_888_888UL);             // sent_bytes
                    Escribir(ps, arm ? 360 : 352, 99_999UL);                 // lost_bytes
                    Escribir(ps, arm ? 376 : 368, 1350u);                    // pmtu
                    quiche_path_stats s = DisposicionUnix32.PathStats(ps, arm);
                    Harness.Equal((nuint)11, s.recv, $"{destino}: recv");
                    Harness.Equal((nuint)22, s.sent, $"{destino}: sent");
                    Harness.Equal((nuint)33, s.lost, $"{destino}: lost");
                    Harness.Equal((nuint)44, s.total_pto_count, $"{destino}: total_pto_count");
                    Harness.Equal(55_000_000UL, s.rtt, $"{destino}: rtt");
                    Harness.Equal(66_000UL, s.rttvar, $"{destino}: rttvar");
                    Harness.Equal((nuint)77_777, s.cwnd, $"{destino}: cwnd");
                    Harness.Equal(88_888_888UL, s.sent_bytes, $"{destino}: sent_bytes");
                    Harness.Equal(99_999UL, s.lost_bytes, $"{destino}: lost_bytes");
                    Harness.Equal((nuint)1350, s.pmtu, $"{destino}: pmtu");
                    if (arm)
                    {
                        // En ARM la struct generada se desfasa (y en 64 bits, entera): recv, rtt o cwnd salen de otro campo.
                        var g = MemoryMarshal.Read<quiche_path_stats>(ps);
                        Harness.True(g.recv != 11 || g.rtt != 55_000_000UL || g.cwnd != 77_777,
                                     "ARM: la struct generada no lee bien el path_stats del nativo de 32 bits");
                    }

                    // quiche_stats: lo que el cierre con gracia resta para saber si queda algo en vuelo.
                    byte[] st = Bufer();
                    Escribir(st, 0, 5u);                                     // recv
                    Escribir(st, arm ? 24 : 20, 1_000_000UL);                // sent_bytes
                    Escribir(st, arm ? 40 : 36, 900_000UL);                  // acked_bytes
                    Escribir(st, arm ? 48 : 44, 1_000UL);                    // lost_bytes
                    Escribir(st, arm ? 72 : 68, 2u);                         // paths_count
                    Escribir(st, arm ? 168 : 160, 4_321UL);                  // bytes_in_flight_duration_msec
                    st[arm ? 176 : 168] = 1;                                 // tx_buffered_inconsistent
                    quiche_stats e = DisposicionUnix32.Stats(st, arm);
                    Harness.Equal((nuint)5, e.recv, $"{destino}: stats.recv");
                    Harness.Equal(1_000_000UL, e.sent_bytes, $"{destino}: stats.sent_bytes");
                    Harness.Equal(900_000UL, e.acked_bytes, $"{destino}: stats.acked_bytes");
                    Harness.Equal(1_000UL, e.lost_bytes, $"{destino}: stats.lost_bytes");
                    Harness.Equal((nuint)2, e.paths_count, $"{destino}: stats.paths_count");
                    Harness.Equal(4_321UL, e.bytes_in_flight_duration_msec, $"{destino}: stats.bytes_in_flight_duration_msec");
                    Harness.True(e.tx_buffered_inconsistent, $"{destino}: stats.tx_buffered_inconsistent");
                }

                // El nativo escribe la struct entera en lo que le da el wrapper: la de C# nunca puede ser más pequeña. En 64
                // bits, además, tiene que medir lo mismo que la nativa (bindgen: 288, 448 y 208).
                int sendInfo = Unsafe.SizeOf<quiche_send_info>();
                int pathStats = Unsafe.SizeOf<quiche_path_stats>();
                int stats = Unsafe.SizeOf<quiche_stats>();
                if (DisposicionUnix32.Activa)
                {
                    bool a8 = DisposicionUnix32.U64AlineadoA8;
                    Harness.True(sendInfo >= DisposicionUnix32.TamañoSendInfo, $"send_info de C# ({sendInfo}) cabe la nativa");
                    Harness.True(pathStats >= DisposicionUnix32.TamañoPathStats(a8), $"path_stats de C# ({pathStats}) cabe la nativa");
                    Harness.True(stats >= DisposicionUnix32.TamañoStats(a8), $"stats de C# ({stats}) cabe la nativa");
                }
                else if (IntPtr.Size == 8)
                {
                    Harness.Equal(288, sendInfo, "send_info de 64 bits");
                    Harness.Equal(448, pathStats, "path_stats de 64 bits");
                    Harness.Equal(208, stats, "stats de 64 bits");
                }
                Harness.True(DisposicionUnix32.TamañoDeBufer >= Math.Max(sendInfo, Math.Max(pathStats, stats)),
                             "el búfer de lectura cabe cualquiera de las tres");
            });
        }

        private static byte[] Bufer()
        {
            // Relleno que no es cero: un campo que se lea de donde no se escribió no puede pasar por bueno.
            var b = new byte[DisposicionUnix32.TamañoDeBufer];
            b.AsSpan().Fill(0xA5);
            return b;
        }

        private static void Escribir(byte[] b, int offset, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), v);
        private static void Escribir(byte[] b, int offset, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(offset), v);
        private static void Escribir(byte[] b, int offset, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(offset), v);

        private static void Comprobar(Sistema sistema, Architecture arquitectura, string rid, string libreria, bool porNombre)
        {
            UbicacionDelNativo? u = Para(sistema, arquitectura);
            Harness.True(u is not null, $"{sistema}/{arquitectura} tiene que tener ubicación");
            Harness.Equal(rid, u!.Rid, $"el RID de {sistema}/{arquitectura}");
            Harness.Equal(libreria, u.Libreria, $"la librería de {sistema}/{arquitectura}");
            Harness.Equal(porNombre, u.PorNombre, $"si {sistema}/{arquitectura} carga por nombre");
            Harness.Equal(System.IO.Path.Combine("base", "runtimes", rid, "native", libreria),
                          u.RutaJuntoAlBinario("base"), $"la ruta junto al binario de {sistema}/{arquitectura}");
        }
    }
}
