using System;
using Cloudflare.QuicheNet;

namespace Cloudflare.QuicheNet.Internal.Quic
{
    /// <summary>
    /// Hasta dónde llegan los paquetes de una conexión QUIC por su camino: el techo con que nace —hasta donde busca el PMTUD
    /// de quiche— y lo que cabe en cada momento, que la conexión mira antes de mandar.
    /// <para>
    /// <b>Nada puesto de antemano.</b> Era un número: 1452 con DF —una Ethernet de 1500 con IPv6—, 1350 sin él y 1200 por el
    /// salto de una cascada. Por el bucle local, o una red con tramas grandes, se quedaba en 1452; por el WARP de esta máquina
    /// (1300) sondeaba por encima de lo que la interfaz deja salir; y detrás de un salto no buscaba nunca. Ahora el techo lo
    /// pone el camino de cada conexión: lo que el sistema deja mandar con DF hacia ese destino, lo que mide quien ya mide el
    /// camino, o lo que llega del par.
    /// </para>
    /// <para>
    /// <b>Cómo se le da a quiche.</b> quiche busca el MTU él solo (DPLPMTUD, RFC 8899) entre los 1200 del mínimo de QUIC y el
    /// techo que le den al crear la conexión, y no lo cambia después. Su configuración se comparte entre conexiones, así que el
    /// techo se escribe en ella justo antes de crear cada una, con el candado con que se crean (ver
    /// <c>QuicheConnection.ConnectAsync</c> y <c>Accept</c>): cada conexión nace con el suyo. Lo que cambia después —el camino
    /// medido por otro, lo que anuncia el par, lo que llega de él— lo hace cumplir la conexión: lo que pase de ahí no sale, y
    /// para quiche es una sonda perdida, que es lo que haría el camino.
    /// </para>
    /// </summary>
    internal sealed class TechoDeQuic
    {
        /// <summary>
        /// Lo más largo que arma quiche: el largo de un paquete va en un varint de dos bytes, y por encima de 16 383 no lo
        /// escribe (<c>max_send_udp_payload_size</c> de quiche). Por el bucle local es el techo de verdad.
        /// </summary>
        public const int MaxPaquete = 16_383;

        /// <summary>El mínimo de QUIC (RFC 9000 §14): todo camino que lleve QUIC lleva esto, y quiche no baja de ahí.</summary>
        public const int Minimo = LimitesDeDatagrama.MinimoInicialQuic;

        /// <summary>De dónde sale el techo de un camino.</summary>
        internal enum Origen
        {
            /// <summary>Un camino que puede trocear sin que se vea: una sonda troceada pasaría. El mínimo, sin buscar.</summary>
            Minimo,

            /// <summary>Un socket con DF, o un salto que no trocea: lo que deja el sistema, y quiche busca por debajo.</summary>
            Sistema,

            /// <summary>Un camino que ya mide otro —una sesión turn por debajo—: lo que diga en cada momento.</summary>
            Medido,

            /// <summary>
            /// Un socket sin DF —el UDP de «auto», el bucle local de un nodo tras un relay—: lo que llega del par, que mide su
            /// camino con DF; quiche busca por debajo de eso, y por debajo de lo que el sistema deja salir sin trocear. Con un
            /// par que no busca —nada de lo que manda pasa de su primer datagrama—, quiche busca solo, hasta lo del sistema, pero
            /// solo si hace falta mandar algo que no cabe en lo que ha llegado y no se puede trocear (ver
            /// <c>QuicheConnection.LoQueHaProbadoElPar</c>).
            /// </summary>
            LoQueLlega,
        }

        private readonly Func<int?>? _ahora;

        private TechoDeQuic(Origen de, int alNacer, Func<int?>? ahora)
        {
            De      = de;
            AlNacer = Math.Max(alNacer, Minimo);
            _ahora  = ahora;
        }

        /// <summary>De dónde sale.</summary>
        public Origen De { get; }

        /// <summary>
        /// La carga UDP más larga con que nace la conexión: el datagrama entero, con lo que le ponga un ofuscador. Lo que arma
        /// quiche es esto menos lo del ofuscador, y como mucho <see cref="MaxPaquete"/>.
        /// </summary>
        public int AlNacer { get; }

        /// <summary>Si quiche busca el MTU por debajo del techo.</summary>
        public bool Busca => De != Origen.Minimo;

        /// <summary>
        /// Lo más largo que puede caber ahora por un camino medido, en carga UDP: lo que está por debajo de lo que quien mide
        /// vio que no cabe. null si no se mide.
        /// </summary>
        public int? Ahora => _ahora?.Invoke();

        /// <summary>Un camino que puede trocear: el mínimo de QUIC, sin buscar.</summary>
        public static readonly TechoDeQuic SinBuscar = new(Origen.Minimo, Minimo, null);

        /// <summary>
        /// Un tamaño puesto a mano, sin buscar: solo para las pruebas que necesitan paquetes de un tamaño desde el principio
        /// —un cliente de prueba que lleva paquetes IPv6 de 1280 por datagramas—. Nada de la librería lo usa.
        /// </summary>
        internal static TechoDeQuic FijoParaPruebas(int carga) => new(Origen.Minimo, carga, null);

        /// <summary>Un socket con DF o un salto que no trocea: <paramref name="carga"/> es lo que el sistema deja salir.</summary>
        public static TechoDeQuic DelSistema(int? carga) => new(Origen.Sistema, carga ?? MaxPaquete, null);

        /// <summary>Un camino que mide otro: <paramref name="ahora"/> dice en cada momento lo más que cabe.</summary>
        public static TechoDeQuic Medido(Func<int?> ahora) => new(Origen.Medido, ahora() ?? Minimo, ahora);

        /// <summary>Un socket sin DF: lo que llega del par, hasta <paramref name="carga"/>, lo que sale sin trocear.</summary>
        public static TechoDeQuic PorLoQueLlega(int? carga) => new(Origen.LoQueLlega, carga ?? MaxPaquete, null);

        /// <summary>
        /// El techo de un camino por un socket hacia <paramref name="remoto"/>: con DF, lo que deja el sistema; sin él, lo que
        /// llegue del par. Un par en el proceso —un flujo de turn, uno del relay— dice el suyo.
        /// </summary>
        public static TechoDeQuic DeSocket(bool conDf, System.Net.EndPoint remoto)
        {
            int? carga = remoto is System.Net.IPEndPoint ip ? MtuDelSistema.CargaHacia(ip) : null;
            return conDf ? DelSistema(carga) : PorLoQueLlega(carga);
        }

        /// <summary>Convierte la descripción neutral del canal en el techo que usa quiche.</summary>
        internal static TechoDeQuic DePista(PistaCaminoUdp pista)
            => pista.Tipo switch
            {
                PistaCaminoUdp.Clase.Socket => DeSocket(SocketUdpDf.ConDf(pista.Socket!), pista.Remoto!),
                PistaCaminoUdp.Clase.Medido => Medido(pista.Ahora!),
                PistaCaminoUdp.Clase.SinTrozos => DelSistema(pista.Carga),
                _ => SinBuscar,
            };

        /// <summary>
        /// Un par en el proceso: si su camino lo mide alguien —un flujo de turn, por las sondas de su sesión—, lo medido; si
        /// no —un flujo del relay de un nodo—, lo que llegue de él: su camino de verdad es el del cliente con el relay, que
        /// mide el cliente. Y hasta lo que el extremo sepa del tramo que conoce —el marco del relay y lo que el sistema deja
        /// mandar hacia él—: con lo más que arma quiche, la conexión nacía con 16 383, y quiche hace su ventana inicial y su
        /// ritmo inicial con el techo, veinte paquetes de ese tamaño, hasta que su búsqueda baja.
        /// </summary>


        /// <summary>
        /// El mismo techo sin pasar de <paramref name="carga"/> al nacer: lo que se sabe de un tramo del camino que el sistema no
        /// ve, como el marco de los datagramas del relay de un nodo.
        /// </summary>
        public TechoDeQuic Acotado(int carga) => carga >= AlNacer ? this : new(De, carga, _ahora);

        /// <inheritdoc/>
        public override string ToString() => $"{De}, {AlNacer} B al nacer";
    }
}
