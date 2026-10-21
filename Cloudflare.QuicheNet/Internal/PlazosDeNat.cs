using System;
using System.Net;
using System.Threading;
namespace Cloudflare.QuicheNet.Internal {
    internal sealed class PlazosDeNat
    {
        /// <summary>El de todo el proceso.</summary>
        public static PlazosDeNat Proceso { get; } = new(seguirLaRed: true);

        /// <summary>
        /// El techo de la cadencia prudente: 15 s, la mitad de los 30 s de los NAT que antes olvidan (RFC 9308 §3.2). Con la
        /// cadencia en un tercio de la inactividad —100 s con los 5 min de quic, QSOCKS o KCP—, una conexión guardada para
        /// reutilizarla se quedaba sorda mucho antes de que la poda de portadores la soltara.
        /// </summary>
        public const long TechoMs = 15_000;

        /// <summary>La cadencia más corta: por debajo de un segundo no hay NAT que valga, y sería un chorro de latidos.</summary>
        public const long MinimoMs = 1_000;

        /// <summary>
        /// El silencio más corto que se toma por un NAT que olvida: un camino que muere con menos no murió por callar —con
        /// tráfico cada segundo no hay NAT que olvide—, sino por otra cosa.
        /// </summary>
        public const long SilencioMinimoMs = 2_000;

        /// <summary>Destinos que se recuerdan como mucho.</summary>
        internal const int MaxDestinos = 1024;

        private readonly BoundedCache<string, Nat> _destinos =
            new(MaxDestinos, StringComparer.Ordinal, isExpired: static n => n.Caducado);

        private readonly IDisposable? _cambioDeRed;

        /// <param name="seguirLaRed">Si se olvida al cambiar de red. El del proceso, sí; los de las pruebas, a su gusto.</param>
        internal PlazosDeNat(bool seguirLaRed = false)
        {
            if (seguirLaRed) _cambioDeRed = CambioDeRed.Suscribir(Olvidar);
        }

        /// <summary>
        /// La cadencia prudente para un camino con esa inactividad: un tercio, entre <see cref="MinimoMs"/> y
        /// <see cref="TechoMs"/>. Sin inactividad —cero es «sin plazo»—, el techo, porque el NAT sí lo tiene.
        /// </summary>
        public static long CadenciaPrudenteMs(long inactividadMs)
            => inactividadMs <= 0 ? TechoMs : Math.Clamp(inactividadMs / 3, MinimoMs, TechoMs);

        /// <summary>
        /// Cada cuánto tiene que latir un camino callado hacia <paramref name="destino"/>: la prudente, o la mitad del silencio
        /// más corto que mató un camino hacia él, si es menos.
        /// </summary>
        public long CadenciaMs(EndPoint? destino, long prudenteMs)
            => CadenciaMs(destino is null ? null : ClaveDe(destino), prudenteMs);

        /// <inheritdoc cref="CadenciaMs(EndPoint?, long)"/>
        /// <param name="clave">La del destino, de <see cref="ClaveDe"/>: quien pregunta a menudo la guarda.</param>
        public long CadenciaMs(string? clave, long prudenteMs)
        {
            if (clave is null || !_destinos.TryGetValue(clave, out var nat)) return prudenteMs;
            long mortal = Volatile.Read(ref nat.MortalMs);
            return mortal <= 0 ? prudenteMs : Math.Min(prudenteMs, Math.Max(MinimoMs, mortal / 2));
        }

        /// <summary>
        /// Un camino hacia <paramref name="destino"/> murió tras callar <paramref name="silencioMs"/>: lo último que salió tras
        /// el silencio no tuvo respuesta. Se queda el silencio más corto que haya matado.
        /// </summary>
        /// <returns>Si se anotó: un silencio por debajo de <see cref="SilencioMinimoMs"/> no es de un NAT.</returns>
        public bool AnotarMuerte(EndPoint? destino, long silencioMs)
            => AnotarMuerte(destino is null ? null : ClaveDe(destino), silencioMs);

        /// <inheritdoc cref="AnotarMuerte(EndPoint?, long)"/>
        public bool AnotarMuerte(string? clave, long silencioMs)
        {
            if (clave is null || silencioMs < SilencioMinimoMs) return false;
            var nat = _destinos.GetOrAdd(clave, static _ => new Nat());
            long visto;
            while ((visto = Volatile.Read(ref nat.MortalMs)) == 0 || silencioMs < visto)
                if (Interlocked.CompareExchange(ref nat.MortalMs, silencioMs, visto) == visto) break;
            nat.Tocar();
            return true;
        }

        /// <summary>
        /// Un camino hacia <paramref name="destino"/> contestó tras callar <paramref name="silencioMs"/>. Si llega a lo que
        /// había matado, aquella muerte no era del NAT —un servidor que se reinició, otra red— y se olvida.
        /// </summary>
        public void AnotarSuperado(EndPoint? destino, long silencioMs)
            => AnotarSuperado(destino is null ? null : ClaveDe(destino), silencioMs);

        /// <inheritdoc cref="AnotarSuperado(EndPoint?, long)"/>
        public void AnotarSuperado(string? clave, long silencioMs)
        {
            if (clave is null || !_destinos.TryGetValue(clave, out var nat)) return;
            long mortal = Volatile.Read(ref nat.MortalMs);
            if (mortal > 0 && silencioMs >= mortal) Interlocked.CompareExchange(ref nat.MortalMs, 0, mortal);
        }

        /// <summary>Se olvidan todos: otra red, otros NAT.</summary>
        public void Olvidar() => _destinos.Clear();

        /// <summary>Cuántos destinos se recuerdan, para las pruebas.</summary>
        internal int Cuantos => _destinos.Count;

        /// <summary>
        /// La dirección y el puerto, sin la familia en forma de v6 mapeada: el mismo destino por un socket de doble pila no
        /// puede ser otra clave.
        /// </summary>
        public static string ClaveDe(EndPoint destino)
            => destino is IPEndPoint ip && ip.Address.IsIPv4MappedToIPv6
               ? new IPEndPoint(ip.Address.MapToIPv4(), ip.Port).ToString()
               : destino.ToString() ?? "";

        /// <summary>Lo que se sabe del NAT hacia un destino.</summary>
        private sealed class Nat
        {
            /// <summary>El silencio más corto que mató un camino hacia él, en ms; 0 si no se sabe de ninguno.</summary>
            public long MortalMs;

            private long _tocado = Environment.TickCount64;

            public void Tocar() => Volatile.Write(ref _tocado, Environment.TickCount64);

            /// <summary>Lo que se sabe caduca con el plazo de los recuerdos de la red.</summary>
            public bool Caducado
                => Environment.TickCount64 - Volatile.Read(ref _tocado) > (long)CambioDeRed.Recuerdo.TotalMilliseconds;
        }
    }
}
