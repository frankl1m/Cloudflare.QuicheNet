using System;
using System.Net.NetworkInformation;
using System.Threading;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// El aviso de que cambiaron las direcciones de red del equipo —una VPN que sube o baja, el salto de Wi-Fi a datos
    /// móviles o a Ethernet, un DHCP que renueva—, uno solo para todo el proceso. Lo que se aprendió de la red de antes deja
    /// de valer: la familia que ganaba puede no tener ruta, el UDP que se tragaba un cortafuegos puede pasar, el
    /// middlebox que rompía el EDNS puede no estar.
    /// <para>
    /// Había tres suscripciones al evento del sistema —la memoria de familias, el DNS y la suscripción WebSocket—, cada una
    /// con su manejo de lo que falla según la plataforma, y el resto de memorias de la red no se enteraba: el EDNS negado y
    /// la espera tras no poder abrir un upstream del DNS, la veda del UDP de la cascada y el aparcado de h3 del cliente
    /// HTTP seguían sus diez minutos en la red nueva.
    /// </para>
    /// <para>
    /// El evento del sistema se pide una vez, con el primero que se apunta. Donde no existe —según la plataforma falla con
    /// una u otra excepción: netlink en Linux, SCDynamicStore en iOS— no avisa nunca, y lo aprendido se olvida con su
    /// caducidad, como antes; quien se apunta se entera por <c>sinAviso</c>.
    /// </para>
    /// </summary>
    internal static class CambioDeRed
    {
        /// <summary>
        /// Cuánto se recuerda lo que se aprende de la red —la familia que ganó, el UDP que no pasa, el EDNS que se rompe, el
        /// salto que no habla h2—, si antes no cambia: lo que sugiere RFC 8305 §4 para la familia. Estaba escrito cinco
        /// veces.
        /// </summary>
        public static readonly TimeSpan Recuerdo = TimeSpan.FromMinutes(10);

        private static readonly object _candado = new();
        private static Action[] _oyentes = Array.Empty<Action>();
        private static bool _pedido;
        private static string? _sinAviso;

        /// <summary>
        /// Apunta <paramref name="alCambiar"/> al aviso; lo que devuelve lo desapunta, y hay que soltarlo con quien lo
        /// apuntó: el evento es de todo el proceso, y lo apuntado retendría a su dueño. Se llama en el hilo del aviso del
        /// sistema, así que tiene que ser corto: marcar, olvidar, programar.
        /// </summary>
        /// <param name="sinAviso">
        /// Por qué esta plataforma no avisa, o nulo si avisa. Aun sin aviso, lo apuntado se queda, y
        /// <see cref="Avisar"/> —las pruebas— lo llama igual.
        /// </param>
        public static IDisposable Suscribir(Action alCambiar, out string? sinAviso)
        {
            ArgumentNullException.ThrowIfNull(alCambiar);
            lock (_candado)
            {
                var nuevos = new Action[_oyentes.Length + 1];
                _oyentes.CopyTo(nuevos, 0);
                nuevos[^1] = alCambiar;
                Volatile.Write(ref _oyentes, nuevos);

                if (!_pedido)
                {
                    _pedido = true;
                    try { NetworkChange.NetworkAddressChanged += static (_, _) => Avisar(); }
                    catch (Exception ex) { _sinAviso = ex.Message; }
                }
                sinAviso = _sinAviso;
            }
            return new Baja(alCambiar);
        }

        /// <inheritdoc cref="Suscribir(Action, out string?)"/>
        public static IDisposable Suscribir(Action alCambiar) => Suscribir(alCambiar, out _);

        /// <summary>
        /// Avisa a todos los apuntados; lo que falle en uno no quita el aviso a los demás. Lo llama el evento del sistema, y
        /// las pruebas, que no pueden cambiar la red de verdad.
        /// </summary>
        internal static void Avisar()
        {
            foreach (var oyente in Volatile.Read(ref _oyentes))
            {
                try { oyente(); }
                catch { /* el de uno no es de los demás */ }
            }
        }

        /// <summary>Cuántos hay apuntados, para las pruebas.</summary>
        internal static int Apuntados => Volatile.Read(ref _oyentes).Length;

        private sealed class Baja : IDisposable
        {
            private Action? _oyente;

            public Baja(Action oyente) => _oyente = oyente;

            /// <summary>Desapunta, una vez: se puede llamar más veces sin efecto.</summary>
            public void Dispose()
            {
                var oyente = Interlocked.Exchange(ref _oyente, null);
                if (oyente is null) return;
                lock (_candado)
                {
                    int i = Array.IndexOf(_oyentes, oyente);
                    if (i < 0) return;
                    var nuevos = new Action[_oyentes.Length - 1];
                    Array.Copy(_oyentes, 0, nuevos, 0, i);
                    Array.Copy(_oyentes, i + 1, nuevos, i, _oyentes.Length - i - 1);
                    Volatile.Write(ref _oyentes, nuevos);
                }
            }
        }
    }
}
