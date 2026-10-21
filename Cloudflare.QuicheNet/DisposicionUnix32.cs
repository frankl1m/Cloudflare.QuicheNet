using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Cloudflare.Quiche
{
    /// <summary>
    /// Las estructuras de quiche que el wrapper lee campo a campo, con la disposición del nativo en los 32 bits fuera de
    /// Windows: android-arm, android-x86 y linux-arm.
    /// <para>
    /// <see cref="NativeMethods"/> las declara tal como las genera csbindgen en Windows y en los Unix de 64 bits, y ahí
    /// casan. En un Unix de 32 bits, no:
    /// <list type="bullet">
    /// <item><c>sockaddr_storage</c> se alinea a 4 (su campo de alineación es un <c>size_t</c>), y la de C# a 8, por su
    /// <c>long</c>.</item>
    /// <item><c>timespec</c> es <c>{i32, i32}</c>, y la de C# <c>{long, CLong}</c>.</item>
    /// <item>En x86, <c>u64</c> se alinea a 4 dentro de una estructura (System V i386), y en ARM a 8 (AAPCS).</item>
    /// </list>
    /// Así el «at» de <c>quiche_send_info</c>, que marca el ritmo de envío, salía de bytes que no eran suyos. En ARM,
    /// además, los contadores de <c>quiche_path_stats</c> salían desplazados 4 bytes.
    /// </para>
    /// <para>
    /// Los offsets son los que bindgen calcula con clang y las cabeceras de cada destino (los <c>layout-*.rs</c> de
    /// <c>scripts/android/build.sh</c> y <c>scripts/debian12/build.sh</c> de quiche-bindgen). Se leen sin alinear, de un
    /// búfer que el nativo llena entero. La struct de C# nunca es más pequeña que la nativa, así que el nativo no escribe
    /// fuera de ella. <c>quiche_transport_params</c> no hace falta: lo que se lee de ella está antes del primer desfase.
    /// </para>
    /// </summary>
    internal static unsafe class DisposicionUnix32
    {
        /// <summary>Si este proceso es un Unix de 32 bits, donde se lee con estas disposiciones.</summary>
        internal static readonly bool Activa = IntPtr.Size == 4 && !OperatingSystem.IsWindows();

        /// <summary>Si <c>u64</c> se alinea a 8 en este destino: en ARM AAPCS sí; en x86 y Darwin ARM de 32 bits no.</summary>
        internal static readonly bool U64AlineadoA8 = UsaAlineacionA8(RuntimeInformation.ProcessArchitecture,
            OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()
            || OperatingSystem.IsMacOS() || OperatingSystem.IsTvOS());

        internal static bool UsaAlineacionA8(Architecture arquitectura, bool darwin)
            => arquitectura != Architecture.X86 && !darwin;

        /// <summary>Un búfer donde cabe cualquiera de las tres, en cualquier destino (la mayor, <c>quiche_path_stats</c> de 64 bits: 448).</summary>
        internal const int TamañoDeBufer = 512;

        /// <summary>El tamaño de <c>quiche_send_info</c> en un Unix de 32 bits (ARM y x86).</summary>
        internal const int TamañoSendInfo = 272;

        /// <summary>El tamaño de <c>quiche_path_stats</c>: 408 en ARM, 396 en x86.</summary>
        internal static int TamañoPathStats(bool u64A8) => u64A8 ? 408 : 396;

        /// <summary>El tamaño de <c>quiche_stats</c>: 184 en ARM, 172 en x86.</summary>
        internal static int TamañoStats(bool u64A8) => u64A8 ? 184 : 172;

        /// <summary>El «at» de un <c>quiche_send_info</c> escrito por el nativo: <c>{i32, i32}</c> en el offset 264.</summary>
        internal static timespec At(byte* info) => new timespec
        {
            tv_sec = Unsafe.ReadUnaligned<int>(info + 264),
            tv_nsec = new CLong(Unsafe.ReadUnaligned<int>(info + 268)),
        };

        /// <summary>Un <c>quiche_path_stats</c> escrito por el nativo, pasado a la struct de C#.</summary>
        internal static quiche_path_stats PathStats(byte* p, bool u64A8)
        {
            var s = default(quiche_path_stats);
            Unsafe.CopyBlockUnaligned(&s.local_addr, p, 128);
            s.local_addr_len = Unsafe.ReadUnaligned<int>(p + 128);
            Unsafe.CopyBlockUnaligned(&s.peer_addr, p + 132, 128);
            s.peer_addr_len = Unsafe.ReadUnaligned<int>(p + 260);
            s.validation_state = Unsafe.ReadUnaligned<int>(p + 264);
            s.active = p[268] != 0;
            s.recv = Unsafe.ReadUnaligned<uint>(p + 272);
            s.sent = Unsafe.ReadUnaligned<uint>(p + 276);
            s.lost = Unsafe.ReadUnaligned<uint>(p + 280);
            s.retrans = Unsafe.ReadUnaligned<uint>(p + 284);
            s.total_pto_count = Unsafe.ReadUnaligned<uint>(p + 288);
            s.dgram_recv = Unsafe.ReadUnaligned<uint>(p + 292);
            s.dgram_sent = Unsafe.ReadUnaligned<uint>(p + 296);

            // Lo que sigue empieza en 300 en x86 y en 304 en ARM, y en ARM cada u64 que va tras un usize se realinea a 8.
            int b = u64A8 ? 304 : 300;
            s.rtt = Unsafe.ReadUnaligned<ulong>(p + b);
            s.min_rtt = Unsafe.ReadUnaligned<ulong>(p + b + 8);
            s.max_rtt = Unsafe.ReadUnaligned<ulong>(p + b + 16);
            s.rttvar = Unsafe.ReadUnaligned<ulong>(p + b + 24);
            s.cwnd = Unsafe.ReadUnaligned<uint>(p + b + 32);
            int c = u64A8 ? b + 40 : b + 36;
            s.sent_bytes = Unsafe.ReadUnaligned<ulong>(p + c);
            s.recv_bytes = Unsafe.ReadUnaligned<ulong>(p + c + 8);
            s.lost_bytes = Unsafe.ReadUnaligned<ulong>(p + c + 16);
            s.stream_retrans_bytes = Unsafe.ReadUnaligned<ulong>(p + c + 24);
            s.pmtu = Unsafe.ReadUnaligned<uint>(p + c + 32);
            int d = u64A8 ? c + 40 : c + 36;
            s.delivery_rate = Unsafe.ReadUnaligned<ulong>(p + d);
            s.max_bandwidth = Unsafe.ReadUnaligned<ulong>(p + d + 8);
            s.startup_exit_cwnd = Unsafe.ReadUnaligned<ulong>(p + d + 16);
            return s;
        }

        /// <summary>Un <c>quiche_stats</c> escrito por el nativo, pasado a la struct de C#.</summary>
        internal static quiche_stats Stats(byte* p, bool u64A8)
        {
            var s = default(quiche_stats);
            s.recv = Unsafe.ReadUnaligned<uint>(p);
            s.sent = Unsafe.ReadUnaligned<uint>(p + 4);
            s.lost = Unsafe.ReadUnaligned<uint>(p + 8);
            s.spurious_lost = Unsafe.ReadUnaligned<uint>(p + 12);
            s.retrans = Unsafe.ReadUnaligned<uint>(p + 16);

            int b = u64A8 ? 24 : 20;
            s.sent_bytes = Unsafe.ReadUnaligned<ulong>(p + b);
            s.recv_bytes = Unsafe.ReadUnaligned<ulong>(p + b + 8);
            s.acked_bytes = Unsafe.ReadUnaligned<ulong>(p + b + 16);
            s.lost_bytes = Unsafe.ReadUnaligned<ulong>(p + b + 24);
            s.stream_retrans_bytes = Unsafe.ReadUnaligned<ulong>(p + b + 32);
            s.dgram_recv = Unsafe.ReadUnaligned<uint>(p + b + 40);
            s.dgram_sent = Unsafe.ReadUnaligned<uint>(p + b + 44);
            s.paths_count = Unsafe.ReadUnaligned<uint>(p + b + 48);

            int c = u64A8 ? 80 : 72;
            s.reset_stream_count_local = Unsafe.ReadUnaligned<ulong>(p + c);
            s.stopped_stream_count_local = Unsafe.ReadUnaligned<ulong>(p + c + 8);
            s.reset_stream_count_remote = Unsafe.ReadUnaligned<ulong>(p + c + 16);
            s.stopped_stream_count_remote = Unsafe.ReadUnaligned<ulong>(p + c + 24);
            s.data_blocked_sent_count = Unsafe.ReadUnaligned<ulong>(p + c + 32);
            s.stream_data_blocked_sent_count = Unsafe.ReadUnaligned<ulong>(p + c + 40);
            s.data_blocked_recv_count = Unsafe.ReadUnaligned<ulong>(p + c + 48);
            s.stream_data_blocked_recv_count = Unsafe.ReadUnaligned<ulong>(p + c + 56);
            s.streams_blocked_bidi_recv_count = Unsafe.ReadUnaligned<ulong>(p + c + 64);
            s.streams_blocked_uni_recv_count = Unsafe.ReadUnaligned<ulong>(p + c + 72);
            s.path_challenge_rx_count = Unsafe.ReadUnaligned<ulong>(p + c + 80);
            s.bytes_in_flight_duration_msec = Unsafe.ReadUnaligned<ulong>(p + c + 88);
            s.tx_buffered_inconsistent = p[c + 96] != 0;
            return s;
        }

        // Lo mismo sobre un búfer administrado, para las pruebas: tiene que medir al menos TamañoDeBufer.
        internal static timespec At(ReadOnlySpan<byte> b) { fixed (byte* p = Entero(b)) return At(p); }
        internal static quiche_path_stats PathStats(ReadOnlySpan<byte> b, bool u64A8) { fixed (byte* p = Entero(b)) return PathStats(p, u64A8); }
        internal static quiche_stats Stats(ReadOnlySpan<byte> b, bool u64A8) { fixed (byte* p = Entero(b)) return Stats(p, u64A8); }

        private static ReadOnlySpan<byte> Entero(ReadOnlySpan<byte> b) =>
            b.Length >= TamañoDeBufer ? b : throw new ArgumentException($"el búfer tiene que medir al menos {TamañoDeBufer} bytes");

        /// <summary>
        /// <c>quiche_conn_path_stats</c>, leída con la disposición del nativo donde hace falta. Devuelve lo de la función:
        /// 0 si el camino existe.
        /// </summary>
        internal static int LeerPathStats(quiche_conn* conn, nuint indice, out quiche_path_stats s)
        {
            s = default;
            if (!Activa)
            {
                fixed (quiche_path_stats* ps = &s)
                    return NativeMethods.quiche_conn_path_stats(conn, indice, ps);
            }

            byte* buf = stackalloc byte[TamañoDeBufer];
            int r = NativeMethods.quiche_conn_path_stats(conn, indice, (quiche_path_stats*)buf);
            if (r == 0) s = PathStats(buf, U64AlineadoA8);
            return r;
        }

        /// <summary><c>quiche_conn_stats</c>, leída con la disposición del nativo donde hace falta.</summary>
        internal static void LeerStats(quiche_conn* conn, out quiche_stats s)
        {
            s = default;
            if (!Activa)
            {
                fixed (quiche_stats* ps = &s)
                    NativeMethods.quiche_conn_stats(conn, ps);
                return;
            }

            byte* buf = stackalloc byte[TamañoDeBufer];
            NativeMethods.quiche_conn_stats(conn, (quiche_stats*)buf);
            s = Stats(buf, U64AlineadoA8);
        }
    }
}
