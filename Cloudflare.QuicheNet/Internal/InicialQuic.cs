using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;

using Cloudflare.QuicheNet.Internal.Quic;

namespace Cloudflare.QuicheNet.Internal.Relays
{
    /// <summary>
    /// Lo que el relay lee del primer vuelo de un cliente QUIC sin terminar su conexión: el SNI de su ClientHello, que va en
    /// los CRYPTO de sus paquetes Initial. Las claves de un Initial son públicas —salen de su Destination Connection ID y de la
    /// sal de la versión: RFC 9001 §5.2, y RFC 9369 para la v2—, así que cualquiera en el camino puede leerlo. Es lo mismo que
    /// el relay hace con el ClientHello de un TLS por TCP (<see cref="TlsClientHello"/>), un nivel más abajo; no descifra nada
    /// más, y lo que no cuadra se da por no leído.
    /// <para>
    /// El ClientHello puede no caber en un paquete —con la clave de ML-KEM pasa de 1200 bytes, en Chrome y en Go— y sus
    /// CRYPTO pueden venir troceados y desordenados, a propósito (la «chaos protection» de Chrome): se juntan en un
    /// <see cref="CryptoDelInicial"/> hasta tener el mensaje entero.
    /// </para>
    /// </summary>
    internal static class InicialQuic
    {
        /// <summary>AES-GCM is optional on older Apple systems; parsing packet headers is not.</summary>
#if NET7_0 || NET8_0
        [UnsupportedOSPlatformGuard("ios")]
        [UnsupportedOSPlatformGuard("maccatalyst")]
        [UnsupportedOSPlatformGuard("tvos")]
        internal static bool SoportaAesGcm =>
            !OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst() && !OperatingSystem.IsTvOS()
            && AesGcm.IsSupported;
#else
        [SupportedOSPlatformGuard("ios13.0")]
        [SupportedOSPlatformGuard("maccatalyst13.0")]
        [SupportedOSPlatformGuard("tvos13.0")]
        internal static bool SoportaAesGcm =>
            (!OperatingSystem.IsIOS() || OperatingSystem.IsIOSVersionAtLeast(13))
            && (!OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacCatalystVersionAtLeast(13))
            && (!OperatingSystem.IsTvOS() || OperatingSystem.IsTvOSVersionAtLeast(13))
            && AesGcm.IsSupported;
#endif

        /// <summary>QUIC v1 —la que habla quiche— y v2 (RFC 9369).</summary>
        public const uint Version1 = LimitesDeDatagrama.VersionQuic1, Version2 = 0x6b3343cf;

        /// <summary>El tamaño mínimo del datagrama de un Initial de cliente (RFC 9000 §14.1).</summary>
        public const int MinInicial = LimitesDeDatagrama.MinimoInicialQuic;

        /// <summary>Lo más que mide un Connection ID en QUIC v1 y v2 (RFC 9000 §17.2).</summary>
        public const int MaxCid = LimitesDeDatagrama.MaxConnectionIdQuic;

        private static ReadOnlySpan<byte> SalV1 => new byte[]
        {
            0x38, 0x76, 0x2c, 0xf7, 0xf5, 0x59, 0x34, 0xb3, 0x4d, 0x17, 0x9a, 0xe6, 0xa4, 0xc8, 0x0c, 0xad, 0xcc, 0xbb, 0x7f, 0x0a,
        };

        private static ReadOnlySpan<byte> SalV2 => new byte[]
        {
            0x0d, 0xed, 0xe3, 0xde, 0xf7, 0x00, 0xa6, 0xdb, 0x81, 0x93, 0x81, 0xbe, 0x6e, 0x26, 0x9d, 0xcb, 0xf9, 0xbd, 0x2e, 0xd9,
        };

        /// <summary>El filtro barato: si el datagrama empieza por un Initial de cliente de QUIC v1 o v2, con su tamaño mínimo.</summary>
        public static bool EsInicial(ReadOnlySpan<byte> d)
            => d.Length >= MinInicial && (d[0] & 0xC0) == 0xC0
               && EsTipoInicial(BinaryPrimitives.ReadUInt32BigEndian(d.Slice(1)), (d[0] >> 4) & 0x03);

        /// <summary>Si el tipo de una cabecera larga es el de un Initial: el 0 en la v1, y el 1 en la v2 (RFC 9369 §3.2).</summary>
        public static bool EsTipoInicial(uint version, int tipo) => version == Version1 ? tipo == 0 : version == Version2 && tipo == 1;

        /// <summary>Si el datagrama empieza por un paquete largo de QUIC v1 o v2, del tipo que sea: un Initial, un 0-RTT, un Handshake.</summary>
        public static bool EsLargo(ReadOnlySpan<byte> d)
            => d.Length >= 7 && (d[0] & 0xC0) == 0xC0 && BinaryPrimitives.ReadUInt32BigEndian(d.Slice(1)) is Version1 or Version2;

        /// <summary>
        /// Lo fijo de la cabecera larga de QUIC (RFC 9000 §17.2): la versión, el tipo y dónde van los dos Connection ID. Detrás
        /// viene lo de cada tipo —el token de un Initial, el largo—. Es el único sitio donde se lee: se leía en cuatro, cada uno
        /// con sus cuentas.
        /// </summary>
        internal readonly record struct CabeceraLarga(uint Version, int Tipo, int Dcid, int LargoDcid, int Scid, int LargoScid)
        {
            /// <summary>Donde acaba lo fijo: el Source Connection ID.</summary>
            public int Fin => Scid + LargoScid;

            /// <summary>Si es un Initial: ver <see cref="EsTipoInicial"/>.</summary>
            public bool EsInicial => EsTipoInicial(Version, Tipo);
        }

        /// <summary>Lee lo fijo de la cabecera larga que empieza <paramref name="d"/>: falso si no es una, o no cabe entera.</summary>
        public static bool TryLeerCabecera(ReadOnlySpan<byte> d, out CabeceraLarga cabecera)
        {
            cabecera = default;
            if (d.Length < 7 || (d[0] & 0x80) == 0) return false;
            int largoDcid = d[5];
            if (largoDcid > MaxCid || d.Length < 6 + largoDcid + 1) return false;
            int scid = 6 + largoDcid + 1, largoScid = d[6 + largoDcid];
            if (largoScid > MaxCid || d.Length < scid + largoScid) return false;
            cabecera = new CabeceraLarga(BinaryPrimitives.ReadUInt32BigEndian(d.Slice(1)), (d[0] >> 4) & 0x03,
                                         6, largoDcid, scid, largoScid);
            return true;
        }

        /// <summary>La versión y el Destination Connection ID de un paquete largo: de este salen las claves de su Initial.</summary>
        public static bool TryLeerDcid(ReadOnlySpan<byte> d, out uint version, out ReadOnlySpan<byte> dcid)
        {
            version = 0;
            dcid    = default;
            if (!TryLeerCabecera(d, out var c)) return false;
            version = c.Version;
            dcid    = d.Slice(c.Dcid, c.LargoDcid);
            return true;
        }

        /// <summary>
        /// El Source Connection ID de un paquete largo: en lo que manda el nodo, el que eligió él, por el que el cliente le
        /// escribe desde entonces en sus paquetes cortos.
        /// </summary>
        public static bool TryLeerScid(ReadOnlySpan<byte> d, out ReadOnlySpan<byte> scid)
        {
            scid = default;
            if (!TryLeerCabecera(d, out var c)) return false;
            scid = d.Slice(c.Scid, c.LargoScid);
            return true;
        }

        /// <summary>Las claves del cliente para los Initial de un DCID: el AEAD, su IV y la de la protección de cabecera.</summary>
        internal sealed class Claves : IDisposable
        {
            public Claves(uint version, byte[] clave, byte[] iv, byte[] hp)
            {
                if (!SoportaAesGcm) throw new PlatformNotSupportedException("Reading a QUIC Initial requires AES-GCM support.");
                Version = version;
                Iv      = iv;
#if NET8_0_OR_GREATER
                Aead    = new AesGcm(clave, 16);
#else
                Aead    = new AesGcm(clave);
#endif
                Hp      = Aes.Create();
                Hp.Key  = hp;
                CryptographicOperations.ZeroMemory(clave);
            }

            public uint Version { get; }
            public byte[] Iv { get; }
            public AesGcm Aead { get; }
            public Aes Hp { get; }

            public void Dispose()
            {
                if (SoportaAesGcm) Aead.Dispose();
                Hp.Dispose();
            }
        }

        /// <summary>Las claves del cliente para los Initial con este DCID, en la versión dada (RFC 9001 §5.2, RFC 9369 §3.3).</summary>
        public static Claves Derivar(uint version, ReadOnlySpan<byte> dcid)
        {
            var (clave, iv, hp) = DerivarBytes(version, dcid);
            try { return new Claves(version, clave, iv, hp); }
            finally { CryptographicOperations.ZeroMemory(hp); }
        }

        /// <summary>Lo mismo en bytes: la clave del AEAD, su IV y la de la protección de cabecera. Las pruebas miran los del RFC.</summary>
        internal static (byte[] Clave, byte[] Iv, byte[] Hp) DerivarBytes(uint version, ReadOnlySpan<byte> dcid)
        {
            Span<byte> inicial = stackalloc byte[32];
            HKDF.Extract(HashAlgorithmName.SHA256, dcid, version == Version2 ? SalV2 : SalV1, inicial);
            Span<byte> cliente = stackalloc byte[32];
            ExpandirEtiqueta(inicial, "client in", cliente);

            string prefijo = version == Version2 ? "quicv2 " : "quic ";
            var clave = new byte[16];
            var iv    = new byte[12];
            var hp    = new byte[16];
            ExpandirEtiqueta(cliente, prefijo + "key", clave);
            ExpandirEtiqueta(cliente, prefijo + "iv", iv);
            ExpandirEtiqueta(cliente, prefijo + "hp", hp);
            return (clave, iv, hp);
        }

        /// <summary>HKDF-Expand-Label de TLS 1.3 (RFC 8446 §7.1), sin contexto.</summary>
        internal static void ExpandirEtiqueta(ReadOnlySpan<byte> secreto, string etiqueta, Span<byte> salida)
        {
            int largo = 6 + etiqueta.Length;
            Span<byte> info = stackalloc byte[2 + 1 + largo + 1];
            BinaryPrimitives.WriteUInt16BigEndian(info, (ushort)salida.Length);
            info[2] = (byte)largo;
            Encoding.ASCII.GetBytes("tls13 " + etiqueta, info.Slice(3));
            info[3 + largo] = 0;
            HKDF.Expand(HashAlgorithmName.SHA256, secreto, salida, info);
        }

        /// <summary>
        /// Abre los Initial del datagrama con <paramref name="claves"/> y pasa sus CRYPTO a <paramref name="crypto"/>. Lo que
        /// va pegado detrás y no es un Initial —un 0-RTT— se salta. Falso si no hay un Initial que se deje abrir: no es de
        /// estas claves, o está roto. No toca <paramref name="datagrama"/>.
        /// </summary>
        public static bool TryAbrir(ReadOnlySpan<byte> datagrama, Claves claves, CryptoDelInicial crypto)
        {
            var d = datagrama;
            bool alguno = false;
            while (d.Length > 0 && (d[0] & 0x80) != 0)
            {
                if (d.Length < 7 || (d[0] & 0x40) == 0) return alguno;
                if (!TryLeerCabecera(d, out var c)) return false;
                if (c.Version != claves.Version) return alguno;
                bool esInicial = c.EsInicial;

                int i = c.Fin;
                if (esInicial)
                {
                    if (!Varint(d, ref i, out ulong token) || (ulong)(d.Length - i) < token) return false;
                    i += (int)token;
                }
                if (!Varint(d, ref i, out ulong largo) || (ulong)(d.Length - i) < largo) return false;
                int fin = i + (int)largo;

                if (esInicial)
                {
                    if (!AbrirPaquete(d[..fin], i, claves, crypto)) return false;
                    alguno = true;
                }
                d = d[fin..];
            }
            return alguno;
        }

        /// <summary>Quita la protección de cabecera, descifra y lee las tramas de un Initial (RFC 9001 §5.3-5.4).</summary>
        private static bool AbrirPaquete(ReadOnlySpan<byte> p, int pnOffset, Claves claves, CryptoDelInicial crypto)
        {
            if (!SoportaAesGcm) return false;
            if (p.Length < pnOffset + 4 + 16) return false;

            Span<byte> mascara = stackalloc byte[16];
            claves.Hp.EncryptEcb(p.Slice(pnOffset + 4, 16), mascara, PaddingMode.None);

            byte primero = (byte)(p[0] ^ (mascara[0] & 0x0F));
            int pnLen    = (primero & 0x03) + 1;
            int cabecera = pnOffset + pnLen;
            int cuerpo   = p.Length - cabecera - 16;
            if (cuerpo < 0) return false;

            var buf = ArrayPool<byte>.Shared.Rent(cabecera + cuerpo);
            try
            {
                var aad = buf.AsSpan(0, cabecera);
                p[..cabecera].CopyTo(aad);
                aad[0] = primero;
                ulong pn = 0;
                for (int k = 0; k < pnLen; k++)
                {
                    aad[pnOffset + k] ^= mascara[1 + k];
                    pn = (pn << 8) | aad[pnOffset + k];
                }

                Span<byte> nonce = stackalloc byte[12];
                claves.Iv.CopyTo(nonce);
                for (int k = 0; k < 8; k++) nonce[12 - 1 - k] ^= (byte)(pn >> (8 * k));

                var claro = buf.AsSpan(cabecera, cuerpo);
                try
                {
                    claves.Aead.Decrypt(nonce, p.Slice(cabecera, cuerpo), p.Slice(cabecera + cuerpo, 16), claro, aad);
                }
                catch (CryptographicException) { return false; }
                return LeerTramas(claro, crypto);
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }

        /// <summary>
        /// Las tramas de un Initial: los CRYPTO van a <paramref name="crypto"/>; PADDING, PING y ACK se saltan. Otra no cabe en
        /// un Initial (RFC 9000 §12.4), y lo que la traiga no se lee.
        /// </summary>
        private static bool LeerTramas(ReadOnlySpan<byte> f, CryptoDelInicial crypto)
        {
            int i = 0;
            while (i < f.Length)
            {
                byte tipo = f[i++];
                switch (tipo)
                {
                    case 0x00:   // PADDING
                    case 0x01:   // PING
                        break;

                    case 0x02:   // ACK
                    case 0x03:   // ACK con ECN
                        if (!Varint(f, ref i, out _) || !Varint(f, ref i, out _) || !Varint(f, ref i, out ulong rangos)
                            || rangos > 256 || !Varint(f, ref i, out _)) return false;
                        for (ulong r = 0; r < rangos; r++)
                            if (!Varint(f, ref i, out _) || !Varint(f, ref i, out _)) return false;
                        if (tipo == 0x03)
                            for (int k = 0; k < 3; k++)
                                if (!Varint(f, ref i, out _)) return false;
                        break;

                    case 0x06:   // CRYPTO
                        if (!Varint(f, ref i, out ulong desde) || !Varint(f, ref i, out ulong largo)
                            || (ulong)(f.Length - i) < largo) return false;
                        if (!crypto.Anadir(desde, f.Slice(i, (int)largo))) return false;
                        i += (int)largo;
                        break;

                    case 0x1c:   // CONNECTION_CLOSE: nada más que leer
                        return true;

                    default:
                        return false;
                }
            }
            return true;
        }

        private static bool Varint(ReadOnlySpan<byte> d, ref int i, out ulong valor)
        {
            valor = 0;
            if (i >= d.Length || !VarintQuic.IntentarLeer(d[i..], out valor, out int leidos)) return false;
            i += leidos;
            return true;
        }

        /// <summary>
        /// El SNI de un ClientHello entero, como mensaje de handshake —así va en QUIC, sin registro de TLS—, o null si no
        /// trae. Lo lee <see cref="TlsClientHello"/>, el mismo que lee el del TLS por TCP: se le ponía delante un registro
        /// inventado, copiado en un búfer, para que lo leyera como uno de TCP.
        /// </summary>

    }

    /// <summary>
    /// El flujo CRYPTO del nivel Initial de un cliente, juntado de sus tramas —troceadas y en cualquier orden— hasta tener
    /// el ClientHello entero. Con tope: lo que no cabe no es un ClientHello de verdad.
    /// </summary>
    internal sealed class CryptoDelInicial
    {
        /// <summary>Lo más que se junta: un ClientHello con ML-KEM y todas sus extensiones cabe de sobra.</summary>
        public const int Max = 16 * 1024;

        /// <summary>
        /// Lo más de trozos sueltos, con huecos entre ellos, que se guardan. Un ClientHello de verdad llega en unos pocos —la
        /// «chaos protection» de Chrome lo parte en una decena—; con más, no es un ClientHello. Sin tope, cada CRYPTO volvía a
        /// ordenar la lista entera, y unos miles de un byte con huecos ocupaban el único hilo de recepción del puerto.
        /// </summary>
        public const int MaxTrozos = 64;

        private byte[] _datos = new byte[2048];

        /// <summary>Lo que hay, en trozos ordenados que no se tocan: el orden de su principio es también el de su final.</summary>
        private readonly List<(int Desde, int Hasta)> _hay = new();

        /// <summary>Llegaron más trozos sueltos que <see cref="MaxTrozos"/>.</summary>
        private bool _demasiados;

        /// <summary>Añade un trozo, en su sitio. Falso si se pasa del tope, o de trozos sueltos.</summary>
        public bool Anadir(ulong desde, ReadOnlySpan<byte> trozo)
        {
            if (_demasiados || desde > Max || desde + (ulong)trozo.Length > Max) return false;
            if (trozo.Length == 0) return true;
            int d = (int)desde, h = d + trozo.Length;
            if (h > _datos.Length) Array.Resize(ref _datos, Math.Min(Max, Math.Max(h, _datos.Length * 2)));
            trozo.CopyTo(_datos.AsSpan(d));

            // Desde el primero que acaba en d o después —los de antes no lo tocan—, los que empiezan en h o antes se funden
            // con él en uno solo.
            int i = PrimeroQueAcabaDesde(d), j = i;
            while (j < _hay.Count && _hay[j].Desde <= h)
            {
                d = Math.Min(d, _hay[j].Desde);
                h = Math.Max(h, _hay[j].Hasta);
                j++;
            }

            if (j > i)
            {
                _hay[i] = (d, h);
                if (j > i + 1) _hay.RemoveRange(i + 1, j - i - 1);
                return true;
            }
            if (_hay.Count >= MaxTrozos)
            {
                _demasiados = true;
                return false;
            }
            _hay.Insert(i, (d, h));
            return true;
        }

        /// <summary>El primer trozo que acaba en <paramref name="desde"/> o después, por búsqueda binaria; o el final.</summary>
        private int PrimeroQueAcabaDesde(int desde)
        {
            int bajo = 0, alto = _hay.Count;
            while (bajo < alto)
            {
                int medio = (bajo + alto) / 2;
                if (_hay[medio].Hasta < desde) bajo = medio + 1;
                else alto = medio;
            }
            return bajo;
        }

        /// <summary>Lo que hay seguido desde el principio.</summary>
        private int Seguido => _hay.Count > 0 && _hay[0].Desde == 0 ? _hay[0].Hasta : 0;

        /// <summary>
        /// Si lo que empieza no es un ClientHello, o dice medir más que el tope, o llegó en demasiados trozos sueltos: no hay
        /// nada que esperar.
        /// </summary>
        public bool NoEsClientHello
        {
            get
            {
                if (_demasiados) return true;
                int n = Seguido;
                if (n >= 1 && _datos[0] != 0x01) return true;
                return n >= 4 && 4 + ((_datos[1] << 16) | (_datos[2] << 8) | _datos[3]) > Max;
            }
        }

        /// <summary>El ClientHello entero, como mensaje de handshake, si ya está.</summary>
        public bool TryClientHello(out ReadOnlySpan<byte> mensaje)
        {
            mensaje = default;
            int n = Seguido;
            if (n < 4 || _datos[0] != 0x01) return false;
            int largo = 4 + ((_datos[1] << 16) | (_datos[2] << 8) | _datos[3]);
            if (largo > Max || n < largo) return false;
            mensaje = _datos.AsSpan(0, largo);
            return true;
        }

        /// <summary>
        /// Los <paramref name="largo"/> primeros bytes del ClientHello, si ya están seguidos aunque falte el resto: el
        /// aleatorio del cliente va en los 38 primeros, y el registro contra la repetición del 0-RTT no espera a lo demás.
        /// </summary>
        public bool TryPrincipio(int largo, out ReadOnlySpan<byte> principio)
        {
            principio = default;
            if (Seguido < largo || _datos[0] != 0x01) return false;
            principio = _datos.AsSpan(0, largo);
            return true;
        }
    }
}
