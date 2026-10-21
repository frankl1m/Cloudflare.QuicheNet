using System;

namespace Cloudflare.QuicheNet
{
    /// <summary>
    /// Ofuscación por datagrama de una conexión QUIC: envuelve cada paquete entero —ya
    /// cifrado por quiche— justo antes del socket, y lo desenvuelve justo después de
    /// recibirlo.
    /// <para>
    /// Puede poner bytes delante del paquete (<see cref="Sobrecarga"/>), pero no detrás, y
    /// no guarda estado entre paquetes. Una sola instancia sirve a todas las conexiones de
    /// la configuración, desde varios hilos a la vez. Es la forma del salamander de
    /// Hysteria 2: una sal de 8 bytes delante y el paquete XOR una clave sacada de ella.
    /// </para>
    /// </summary>
    public interface IPaqueteOfuscador
    {
        /// <summary>Bytes que el ofuscador pone delante de cada paquete.</summary>
        int Sobrecarga { get; }

        /// <summary>
        /// Envuelve el paquete in-place. <paramref name="datagrama"/> es el datagrama
        /// entero: <see cref="Sobrecarga"/> bytes libres y, detrás, el paquete de quiche.
        /// </summary>
        void Codificar(Span<byte> datagrama);

        /// <summary>
        /// Desenvuelve el datagrama in-place. Devuelve dónde empieza el paquete dentro de
        /// él, o −1 si el datagrama no vale y hay que tirarlo.
        /// </summary>
        int Decodificar(Span<byte> datagrama);
    }
}
