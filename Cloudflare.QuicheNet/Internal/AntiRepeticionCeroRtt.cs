using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;
using Cloudflare.QuicheNet.Internal.Relays;

namespace Cloudflare.QuicheNet.Internal.Quic
{
    /// <summary>
    /// La defensa del servidor contra la repetición del 0-RTT (RFC 8446 §8, RFC 9001 §9.2): el early data de un
    /// ClientHello solo se atiende una vez.
    /// <para>
    /// <b>El ataque:</b> quien capture el primer vuelo de una conexión reanudada —el Initial con el ClientHello y los
    /// paquetes 0-RTT— lo puede repetir tal cual cuando la de verdad ya se cerró. El ticket sigue valiendo, el servidor
    /// acepta el early data y entrega lo que trae antes de acabar el saludo, que el atacante nunca acaba: la orden se
    /// ejecuta dos veces. Mientras la conexión de verdad sigue en el mapa del listener, la repetición le llega a ella, y
    /// quiche la tira por duplicada; el hueco es después.
    /// </para>
    /// <para>
    /// <b>La defensa</b> es la del registro de ClientHello de RFC 8446 §8.2: cada conexión nueva de un puerto con early
    /// data cuyo ClientHello lo pide apunta su aleatorio —los 32 bytes que el cliente sortea en cada saludo, y que una
    /// repetición trae iguales— al crearse. Si ya estaba, o no se pudo leer, o el registro está lleno, a esa conexión no le
    /// llega ningún 0-RTT: quiche no ve el early data y no lo entrega. Se apunta al crearla y no al ver su primer 0-RTT:
    /// así se hacía, y si el 0-RTT de la de verdad se perdía o llegaba antes que su Initial —quiche lo reenvía luego en
    /// 1-RTT—, el aleatorio no quedaba apuntado y la repetición del atacante sí se atendía. Lo vio la prueba con la
    /// máquina cargada. Un cliente de verdad no pierde nada
    /// —quiche reenvía lo de su 0-RTT tras el saludo, en 1-RTT—, y una repetición se queda sin nada que atender: no tiene
    /// las claves para acabar el saludo.
    /// </para>
    /// <para>
    /// Cambiar el DCID no la cuela: las claves de los Initial salen del DCID y el atacante podría volver a cifrar el
    /// ClientHello, pero el DCID va en la cabecera de los paquetes 0-RTT, que el AEAD autentica con claves que no tiene.
    /// </para>
    /// <para>
    /// <b>La ventana:</b> un aleatorio se recuerda <see cref="Ventana"/>. Más allá, quien rechaza es el propio BoringSSL:
    /// compara la edad del ticket que dice el cliente con la que le consta, y con más de 60 s de diferencia no acepta el
    /// early data (su <c>ssl_early_data_ticket_age_skew</c>); una repetición tardía trae la edad de cuando se capturó.
    /// Medido sin el registro, con la prueba «quic/0-rtt: el early data que repite un atacante…»: repetido a los 5 y a
    /// los 30 s se atendía otra vez; a los 70, BoringSSL ya lo rechazaba. La ventana cubre esos 60 s con holgura de sobra. El registro es del proceso, para todos los puertos: dos que compartan
    /// clave de tickets se cubren igual.
    /// </para>
    /// </summary>
    internal static class AntiRepeticionCeroRtt
    {
        /// <summary>Cuánto se recuerda un aleatorio: ver la nota de la clase.</summary>
        internal static TimeSpan Ventana { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Lo más que se recuerda a la vez: unos 100 bytes por entrada, unos 10 MB. Lleno, el 0-RTT de las conexiones nuevas
        /// se rechaza —falla hacia lo seguro: cuesta un RTT, no una repetición— hasta que caduquen entradas.
        /// </summary>
        internal static int Tope { get; set; } = 100_000;

        /// <summary>Si se aplica. Solo para medir qué pasa sin él; en uso va siempre.</summary>
        internal static bool Activa { get; set; } = true;

        /// <summary>El reloj, en milisegundos.</summary>
        internal static Func<long> Reloj { get; set; } = () => Environment.TickCount64;

        /// <summary>Los 0-RTT rechazados: por repetidos, por no leerse el ClientHello o por el tope. Para medir.</summary>
        internal static long Rechazados;

        private readonly record struct Aleatorio(long A, long B, long C, long D);

        private static readonly ConcurrentDictionary<Aleatorio, long> _vistos = new();
        private static long _proximoBarrido;

        /// <summary>Desplazamiento del aleatorio en el mensaje ClientHello: tipo (1), largo (3) y legacy_version (2).</summary>
        private const int DesdeAleatorio = 6;

        /// <summary>Lo que hace falta del ClientHello para tener su aleatorio.</summary>
        private const int HastaAleatorio = DesdeAleatorio + 32;

        /// <summary>
        /// El aleatorio del ClientHello que trae el Initial de <paramref name="datagrama"/> —null si no se deja leer: no se
        /// abre con las claves de su DCID, o su primer CRYPTO no empieza el ClientHello— y si pide early data. Eso último
        /// solo se sabe con el ClientHello entero en el datagrama; si no, se da por pedido, que es lo seguro.
        /// </summary>
        internal static (byte[]? Aleatorio, bool PideEarlyData) LeerSaludo(ReadOnlySpan<byte> datagrama)
        {
            // .NET uses CryptoKit for AES-GCM on Apple (iOS/tvOS 13+). Without it,
            // reject only early data; the listener preserves Initial/Handshake
            // packets and the native QUIC engine still completes a 1-RTT handshake.
            if (!InicialQuic.SoportaAesGcm) return (null, true);
            try
            {
                if (!InicialQuic.TryLeerDcid(datagrama, out uint version, out var dcid)) return (null, true);
                using var claves = InicialQuic.Derivar(version, dcid);
                var crypto = new CryptoDelInicial();
                if (!InicialQuic.TryAbrir(datagrama, claves, crypto)
                    || !crypto.TryPrincipio(HastaAleatorio, out var principio)) return (null, true);
                byte[] aleatorio = principio.Slice(DesdeAleatorio, 32).ToArray();
                return (aleatorio, !crypto.TryClientHello(out var hola) || PideEarlyData(hola));
            }
            catch { return (null, true); }
        }

        /// <summary>
        /// Si el ClientHello entero lleva la extensión early_data (42, RFC 8446 §4.2.10). Lo que no se deja leer, sí: es lo
        /// seguro.
        /// </summary>
        internal static bool PideEarlyData(ReadOnlySpan<byte> hola)
        {
            int i = HastaAleatorio;
            if (hola.Length < i + 1) return true;
            i += 1 + hola[i];                                                   // session id
            if (hola.Length < i + 2) return true;
            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(hola[i..]);           // cipher suites
            if (hola.Length < i + 1) return true;
            i += 1 + hola[i];                                                   // compresión
            if (hola.Length < i + 2) return true;
            int fin = i + 2 + BinaryPrimitives.ReadUInt16BigEndian(hola[i..]);
            if (fin > hola.Length) return true;
            for (i += 2; i + 4 <= fin; )
            {
                ushort tipo = BinaryPrimitives.ReadUInt16BigEndian(hola[i..]);
                int largo = BinaryPrimitives.ReadUInt16BigEndian(hola[(i + 2)..]);
                if (tipo == 42) return true;
                i += 4 + largo;
            }
            return i != fin;
        }

        /// <summary>
        /// Si se admite el 0-RTT del ClientHello con este aleatorio: la primera vez dentro de la ventana, sí. Una
        /// repetición, un aleatorio que no se leyó o el registro lleno, no.
        /// </summary>
        internal static bool Admitir(byte[]? aleatorio)
        {
            if (!Activa) return true;
            if (aleatorio is not { Length: 32 })
            {
                Interlocked.Increment(ref Rechazados);
                return false;
            }

            long ahora = Reloj();
            Barrer(ahora);
            var clave = new Aleatorio(
                BinaryPrimitives.ReadInt64LittleEndian(aleatorio),
                BinaryPrimitives.ReadInt64LittleEndian(aleatorio.AsSpan(8)),
                BinaryPrimitives.ReadInt64LittleEndian(aleatorio.AsSpan(16)),
                BinaryPrimitives.ReadInt64LittleEndian(aleatorio.AsSpan(24)));

            // Uno ya visto, aunque haya caducado y no se haya barrido aún, se rechaza: solo puede ser una repetición.
            if (_vistos.Count >= Tope || !_vistos.TryAdd(clave, ahora + (long)Ventana.TotalMilliseconds))
            {
                Interlocked.Increment(ref Rechazados);
                return false;
            }
            return true;
        }

        /// <summary>Tira lo caducado, como mucho una vez por segundo.</summary>
        private static void Barrer(long ahora)
        {
            long toca = Volatile.Read(ref _proximoBarrido);
            if (ahora < toca || Interlocked.CompareExchange(ref _proximoBarrido, ahora + 1000, toca) != toca) return;
            foreach (var e in _vistos)
                if (e.Value <= ahora) _vistos.TryRemove(e.Key, out _);
        }

        /// <summary>Si el datagrama lleva algún paquete 0-RTT (RFC 9000 §17.2.3; RFC 9369 §3.2 en la v2).</summary>
        internal static bool LlevaCeroRtt(ReadOnlySpan<byte> d)
        {
            while (Siguiente(d, out bool cero, out int fin))
            {
                if (cero) return true;
                d = d[fin..];
            }
            return false;
        }

        /// <summary>
        /// Quita del datagrama, en el sitio, sus paquetes 0-RTT, y devuelve lo que mide sin ellos. Lo demás —el Initial,
        /// un Handshake, el corto del final— queda tal cual y en su orden.
        /// </summary>
        internal static int QuitarCeroRtt(Span<byte> d)
        {
            int leido = 0, escrito = 0;
            while (Siguiente(d[leido..], out bool cero, out int fin))
            {
                if (!cero)
                {
                    if (escrito != leido) d.Slice(leido, fin).CopyTo(d[escrito..]);
                    escrito += fin;
                }
                leido += fin;
            }
            int resto = d.Length - leido;
            if (resto > 0 && escrito != leido) d.Slice(leido, resto).CopyTo(d[escrito..]);
            return escrito + resto;
        }

        /// <summary>
        /// El paquete largo que empieza <paramref name="d"/>: si es un 0-RTT y dónde acaba. Falso si no es uno largo de la
        /// v1 o la v2 que se deje medir: lo que queda se deja como está.
        /// </summary>
        private static bool Siguiente(ReadOnlySpan<byte> d, out bool cero, out int fin)
        {
            cero = false; fin = 0;
            if (!InicialQuic.TryLeerCabecera(d, out var c)) return false;
            if (c.Version != InicialQuic.Version1 && c.Version != InicialQuic.Version2) return false;
            int i = c.Fin;
            if (c.EsInicial)
            {
                if (!VarintQuic.IntentarLeer(d[i..], out ulong token, out int n) || (ulong)(d.Length - i - n) < token) return false;
                i += n + (int)token;
            }
            if (!VarintQuic.IntentarLeer(d[i..], out ulong largo, out int m) || (ulong)(d.Length - i - m) < largo) return false;
            fin = i + m + (int)largo;
            cero = c.Version == InicialQuic.Version1 ? c.Tipo == 1 : c.Tipo == 2;
            return true;
        }
    }
}
