using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Cloudflare.QuicheNet.Internal.Quic
{
    /// <summary>
    /// Los enteros de longitud variable de QUIC (RFC 9000 §16). Los dos bits altos del primer
    /// byte dicen si ocupan 1, 2, 4 u 8 bytes, y el resto es el número en big-endian. Hysteria 2
    /// los usa en las cabeceras de sus flujos y datagramas, QSOCKS en su saludo y HTTP/3 en sus
    /// marcos. Estaba con Hysteria 2, que fue el primero en usarlo.
    /// <para>
    /// Se escriben siempre con el largo mínimo. Al leer se acepta cualquier largo, como pide el
    /// RFC: <c>40 25</c> es un 37 tan válido como <c>25</c>.
    /// </para>
    /// </summary>
    internal static class VarintQuic
    {
        /// <summary>El mayor que cabe: 2^62 − 1.</summary>
        public const ulong Maximo = (1UL << 62) - 1;

        /// <summary>Lo más que ocupa uno.</summary>
        public const int LargoMaximo = 8;

        /// <summary>Cuántos bytes ocupa <paramref name="valor"/> escrito con el largo mínimo.</summary>
        public static int Largo(ulong valor) => valor switch
        {
            <= 63         => 1,
            <= 16_383     => 2,
            <= 1_073_741_823 => 4,
            <= Maximo     => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(valor), valor,
                                                       "un varint de QUIC no pasa de 2^62 − 1"),
        };

        /// <summary>Cuántos bytes ocupa el varint que empieza por <paramref name="primero"/>.</summary>
        public static int LargoPorPrimerByte(byte primero) => 1 << (primero >> 6);

        /// <summary>Escribe <paramref name="valor"/> con el largo mínimo y devuelve los bytes usados.</summary>
        public static int Escribir(Span<byte> destino, ulong valor)
        {
            int largo = Largo(valor);
            if (destino.Length < largo)
                throw new ArgumentException($"el varint ocupa {largo} bytes y caben {destino.Length}", nameof(destino));

            switch (largo)
            {
                case 1: destino[0] = (byte)valor; break;
                case 2: BinaryPrimitives.WriteUInt16BigEndian(destino, (ushort)(valor | 0x4000)); break;
                case 4: BinaryPrimitives.WriteUInt32BigEndian(destino, (uint)(valor | 0x8000_0000)); break;
                default: BinaryPrimitives.WriteUInt64BigEndian(destino, valor | 0xC000_0000_0000_0000); break;
            }
            return largo;
        }

        /// <summary>
        /// Lee el varint del principio de <paramref name="origen"/>. Falso si todavía no están
        /// todos sus bytes: quien lee de un flujo espera a que lleguen más y lo vuelve a intentar.
        /// </summary>
        public static bool IntentarLeer(ReadOnlySpan<byte> origen, out ulong valor, out int leidos)
        {
            valor  = 0;
            leidos = 0;
            if (origen.IsEmpty) return false;

            int largo = LargoPorPrimerByte(origen[0]);
            if (origen.Length < largo) return false;

            valor = largo switch
            {
                1 => origen[0],
                2 => (ulong)(BinaryPrimitives.ReadUInt16BigEndian(origen) & 0x3FFF),
                4 => BinaryPrimitives.ReadUInt32BigEndian(origen) & 0x3FFF_FFFFu,
                _ => BinaryPrimitives.ReadUInt64BigEndian(origen) & 0x3FFF_FFFF_FFFF_FFFFul,
            };
            leidos = largo;
            return true;
        }

        /// <summary>
        /// Lee uno de un flujo: un byte, y los que ese byte diga. Nulo si el flujo acaba limpio antes
        /// de empezarlo; si acaba a medias, <see cref="EndOfStreamException"/> con
        /// <paramref name="siAcaba"/>.
        /// <para>
        /// Sin asignar: con <paramref name="scratch"/> —de <see cref="LargoMaximo"/> bytes o más— si
        /// quien lee tiene uno suyo, y si no, con uno del pool. Estaba cuatro veces —el h3 mínimo, su
        /// petición del lado servidor, Hysteria 2 y QSOCKS—, dos con un arreglo nuevo por varint y
        /// cada una con su forma de decir el corte: a medias salía el mensaje genérico de .NET.
        /// </para>
        /// </summary>
        public static async ValueTask<ulong?> LeerAsync(Stream flujo, string siAcaba, CancellationToken ct,
                                                       byte[]? scratch = null)
        {
            byte[] buf = scratch ?? ArrayPool<byte>.Shared.Rent(LargoMaximo);
            try
            {
                if (await flujo.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false) == 0) return null;

                int largo = LargoPorPrimerByte(buf[0]);
                for (int hecho = 1; hecho < largo; )
                {
                    int n = await flujo.ReadAsync(buf.AsMemory(hecho, largo - hecho), ct).ConfigureAwait(false);
                    if (n == 0) throw new EndOfStreamException(siAcaba);
                    hecho += n;
                }
                IntentarLeer(buf.AsSpan(0, largo), out ulong valor, out _);
                return valor;
            }
            finally
            {
                if (scratch is null) ArrayPool<byte>.Shared.Return(buf);
            }
        }

        /// <summary>
        /// Como <see cref="LeerAsync"/>, para cuando el varint tiene que estar: acabar antes de
        /// empezarlo también es un corte, con <paramref name="siAcaba"/>.
        /// </summary>
        public static async ValueTask<ulong> LeerObligatorioAsync(Stream flujo, string siAcaba, CancellationToken ct,
                                                                 byte[]? scratch = null)
            => await LeerAsync(flujo, siAcaba, ct, scratch).ConfigureAwait(false)
               ?? throw new EndOfStreamException(siAcaba);
    }
}
