using System;
using System.Text;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// Cómo pasan los valores de cabecera entre bytes y cadenas en TODOS los caminos: h1.1,
    /// HPACK de h2 y QPACK de h3.
    /// <para>
    /// Una cabecera HTTP son bytes, y los servidores mandan UTF-8 crudo en muchas —un
    /// <c>Content-Disposition</c> con «résumé.txt», una <c>Location</c> con acentos, una
    /// cookie—. Cada camino lo convertía a su manera: h1.1 y el HPACK sin Huffman como ASCII
    /// —todo lo que no lo fuera, «?»—, el HPACK con Huffman como Latin-1 y h3 como UTF-8. Por
    /// el MITM, «ñandú č» llegaba al navegador como «??and?? ??», y peor: el codificador de h2
    /// escribía <c>(byte)carácter</c>, así que una «č» (U+010D) decodificada por h3 salía como
    /// 0x0D, un CR dentro de una cabecera.
    /// </para>
    /// <para>
    /// El modelo ahora es uno: <b>se lee en Latin-1</b> —un carácter por byte, ida y vuelta sin
    /// pérdida— y <b>se escribe en Latin-1 si cabe</b>, que devuelve exactamente los bytes
    /// leídos. Una cadena con caracteres por encima de U+00FF, que solo puede venir de fuera de
    /// este modelo, se escribe en UTF-8: nunca se trunca, así que nunca fabrica un CR o un LF.
    /// </para>
    /// </summary>
    internal static class BytesDeCabecera
    {
        public static string Leer(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

        public static bool CabeEnLatin1(ReadOnlySpan<char> s)
        {
            foreach (char c in s)
                if (c > 'ÿ') return false;
            return true;
        }

        public static int Contar(ReadOnlySpan<char> s)
            => CabeEnLatin1(s) ? s.Length : Encoding.UTF8.GetByteCount(s);

        public static int Escribir(ReadOnlySpan<char> s, Span<byte> destino)
            => CabeEnLatin1(s) ? Encoding.Latin1.GetBytes(s, destino) : Encoding.UTF8.GetBytes(s, destino);
    }
}
