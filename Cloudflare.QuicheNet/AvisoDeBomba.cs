using System;
using System.Threading;
using System.Threading.Tasks;
using Cloudflare.QuicheNet.Internal;

namespace Cloudflare.QuicheNet;

/// <summary>
/// Lo que despierta a una bomba de la conexión: como mucho un aviso pendiente —el que llega sin nadie esperando se queda
/// puesto, y la espera siguiente vuelve en el acto—, un solo consumidor, y ninguna reserva por espera.
/// <para>
/// Era un <see cref="SemaphoreSlim"/>, y con plazo: cada espera suya que se dormía reservaba su máquina de estados, su
/// nodo, la promesa del plazo y un temporizador del runtime. En el banco de UDP sobre QUIC, un tercio de todo lo asignado;
/// y con TCP, cuatro reservas cada vez que una bomba se dormía. Aquí la espera es un <see cref="EsperaReutilizable"/>, y el
/// plazo no es un temporizador por espera: lo vigila quien vigila la conexión, con <see cref="Vigilar"/>, desde una sola
/// cita para todas sus bombas.
/// </para>
/// </summary>
internal sealed class AvisoDeBomba
{
    private const int Nada = 0, Puesto = 1, Esperando = 2;

    private readonly EsperaReutilizable _espera = new();
    private int _estado;

    /// <summary>Hasta cuándo duerme como mucho la espera en curso, en <see cref="Environment.TickCount64"/>.</summary>
    private long _vence = long.MaxValue;

    /// <summary>Despierta a la bomba si duerme; si no, deja el aviso puesto para su próxima espera.</summary>
    public void Avisar()
    {
        while (true)
        {
            int estado = Volatile.Read(ref _estado);
            if (estado == Puesto) return;
            if (estado == Esperando)
            {
                if (Interlocked.CompareExchange(ref _estado, Nada, Esperando) != Esperando) continue;
                _espera.Completar(true);
                return;
            }
            if (Interlocked.CompareExchange(ref _estado, Puesto, Nada) == Nada) return;
        }
    }

    /// <summary>
    /// Espera un aviso: verdadero si lo hubo —o ya estaba puesto—, falso si la despertó el plazo. Un solo consumidor, y una
    /// espera cada vez.
    /// </summary>
    /// <param name="plazoMs">Lo más que duerme, si alguien la vigila; 0, sin plazo.</param>
    public ValueTask<bool> EsperarAsync(long plazoMs, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _estado, Nada, Puesto) == Puesto) return new ValueTask<bool>(true);

        Volatile.Write(ref _vence, plazoMs > 0 ? Environment.TickCount64 + plazoMs : long.MaxValue);
        var espera = _espera.Armar(ct);

        // Armada antes de decir que espera: quien vea Esperando completa esta vuelta. Si entre medias llegó un aviso —el
        // estado ya no es Nada—, se consume aquí, y la vuelta se completa sin dormir.
        if (Interlocked.CompareExchange(ref _estado, Esperando, Nada) != Nada)
        {
            Volatile.Write(ref _estado, Nada);
            _espera.Completar(true);
        }
        return espera;
    }

    /// <summary>
    /// El plazo, desde la cita que vigila las bombas: si la espera en curso ya venció, la despierta sin aviso —devuelve
    /// falso—. Devuelve cuándo vence la que sigue durmiendo, o <see cref="long.MaxValue"/> si no duerme: la bomba que se
    /// duerma después vuelve a programar la cita.
    /// </summary>
    public long Vigilar(long ahora)
    {
        if (Volatile.Read(ref _estado) != Esperando) return long.MaxValue;

        long vence = Volatile.Read(ref _vence);
        if (ahora < vence) return vence;

        if (Interlocked.CompareExchange(ref _estado, Nada, Esperando) == Esperando) _espera.Completar(false);
        return long.MaxValue;
    }
}
