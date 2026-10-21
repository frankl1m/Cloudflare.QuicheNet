using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Cloudflare.QuicheNet.Internal;

namespace Cloudflare.QuicheNet;

/// <summary>
/// Espera precisa, de ~1 ms, para el pacing de QUIC y las citas de quien duerme hasta su próxima hora —el temporizador de
/// quiche, el bucle de KCP y mKCP—.
/// <para>
/// En Windows un <c>Task.Delay</c> corto nunca baja de ~15,6 ms, el cuanto del reloj. El pacer
/// de quiche no perdona el retraso: el paquete que sale tarde reinicia su cadena y el ritmo se
/// hunde. Con esperas de <c>Task.Delay</c>, bbr2 en bucle local pasó de 46 MB/s a 153 KB/s.
/// Aquí un único hilo para todo el proceso duerme con un temporizador de alta resolución
/// (<c>CREATE_WAITABLE_TIMER_HIGH_RESOLUTION</c>, Windows 10 1803 o posterior) y despierta las
/// citas por orden de hora. Fuera de Windows el mismo hilo duerme con la espera del sistema, que
/// ya ronda 1 ms. Si Windows no tiene ese temporizador, <see cref="Disponible"/> es false y no se
/// hace pacing: es mejor mandar en ráfaga que a destiempo.
/// </para>
/// <para>
/// Las citas están en un montículo en el que saben su sitio (<see cref="MonticuloDeCitas"/>, el del planificador): la de quien
/// duerme hasta su próxima hora (<see cref="CitaPrecisa"/>) se reutiliza, y programarla, adelantarla o anularla no reserva
/// nada —era una tarea nueva por cita, con su continuación y su estado en caja—, y una espera cancelada sale del montículo
/// —se quedaba en la cola hasta su hora—.
/// </para>
/// </summary>
internal static class DespertadorPreciso
{
    private const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;

    // Lo justo para esperar y reprogramar, como hace el runtime de Go con el mismo temporizador.
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint TIMER_QUERY_STATE = 0x0001;
    private const uint TIMER_MODIFY_STATE = 0x0002;

    /// <summary>Una cita a menos de esto ya se da por llegada.</summary>
    internal const long MargenNs = 100_000;

    /// <summary>
    /// La resolución del despertador: quien se arma una cita para dentro de menos la arma aquí —una más corta vence en el
    /// acto, el despertador da por llegada la que cae en su margen, y fuera de Windows la espera no baja de 1 ms—, y es lo que
    /// una cita pendiente puede vencer después de la pedida sin que se mueva (<see cref="Adelantar"/>).
    /// </summary>
    public const long Holgura = 1_000_000;

    // El ensamblado desactiva el marshalling del runtime (DisableRuntimeMarshalling): las firmas
    // van con tipos blittables y punteros, sin SetLastError, cadenas, bool ni SafeHandle.
    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr atributos, IntPtr nombre, uint opciones, uint acceso);

    [DllImport("kernel32.dll")]
    private static extern unsafe int SetWaitableTimer(
        IntPtr temporizador, long* vencimiento, int periodo, IntPtr rutina, IntPtr argumento, int reanudar);

    private sealed class ManijaDeTemporizador : WaitHandle
    {
        public ManijaDeTemporizador(SafeWaitHandle manija) => SafeWaitHandle = manija;
    }

    private static readonly object _cerrojo = new();
    private static MonticuloDeCitas _citas = new(64);
    private static readonly AutoResetEvent _aviso = new(false);
    private static readonly SafeWaitHandle? _temporizador;
    private static readonly IntPtr _manija;
    private static readonly WaitHandle[]? _esperas;

    /// <summary>
    /// Hasta cuándo duerme el hilo: la hora de la primera cita cuando se echó a dormir, <see cref="long.MaxValue"/> sin
    /// ninguna, y <see cref="long.MinValue"/> mientras está despierto —va a mirar el montículo otra vez antes de dormir—. Con
    /// el cerrojo. Solo lo despierta una cita que vence antes, como en el planificador.
    /// </summary>
    private static long _dormidoHasta = long.MinValue;

    /// <summary>Si hay espera precisa. Sin ella no se hace pacing.</summary>
    public static bool Disponible { get; }

    /// <summary>Por qué no hay espera precisa, para la traza; null si la hay.</summary>
    public static string? MotivoNoDisponible { get; }

    static DespertadorPreciso()
    {
        if (!OperatingSystem.IsWindows())
        {
            Disponible = true;
        }
        else
        {
            try
            {
                IntPtr manija = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
                    CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, SYNCHRONIZE | TIMER_QUERY_STATE | TIMER_MODIFY_STATE);
                if (manija == IntPtr.Zero)
                {
                    // Windows anterior a 10 1803 no conoce el temporizador de alta resolución.
                    MotivoNoDisponible = $"CreateWaitableTimerExW falló con el error {Marshal.GetLastSystemError()}";
                }
                else
                {
                    // La SafeWaitHandle es para esperar con WaitAny; el hilo reprograma con la manija.
                    var temporizador = new SafeWaitHandle(manija, ownsHandle: true);
                    _manija = manija;
                    _temporizador = temporizador;
                    _esperas = new WaitHandle[] { new ManijaDeTemporizador(temporizador), _aviso };
                    Disponible = true;
                }
            }
            catch (Exception ex)
            {
                // Sin el temporizador no se hace pacing, pero QUIC sigue funcionando.
                _temporizador = null;
                _manija = IntPtr.Zero;
                Disponible = false;
                MotivoNoDisponible = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        // Sin el temporizador de Windows, el hilo espera con el sistema: su cuanto, ~15,6 ms, y de ahí que no haya pacing.
        var hilo = new Thread(Bucle) { IsBackground = true, Name = "despertador-preciso" };
        hilo.Start();
    }

    /// <summary>
    /// Los nanosegundos del reloj monotónico de <c>Stopwatch</c>: QueryPerformanceCounter en
    /// Windows y CLOCK_MONOTONIC en Linux. Se calculan sin desbordar ni perder la fracción.
    /// </summary>
    public static long AhoraNs()
    {
        long ts = System.Diagnostics.Stopwatch.GetTimestamp();
        long frecuencia = System.Diagnostics.Stopwatch.Frequency;
        return ts / frecuencia * 1_000_000_000 + ts % frecuencia * 1_000_000_000 / frecuencia;
    }

    /// <summary>Que <paramref name="cita"/> venza a las <paramref name="horaNs"/>: la pone, o la mueve donde esté, antes o después.</summary>
    public static void Programar(Cita cita, long horaNs)
    {
        bool avisar;
        lock (_cerrojo)
        {
            _citas.Poner(cita, horaNs);
            avisar = horaNs < _dormidoHasta;
            if (avisar) _dormidoHasta = horaNs;
        }
        if (avisar) _aviso.Set();
    }

    /// <summary>
    /// Que <paramref name="cita"/> venza como tarde a las <paramref name="horaNs"/>: si ya está programada para antes —o para
    /// como mucho <paramref name="holguraNs"/> después—, no se toca. Es lo de quien duerme hasta su próxima hora y se vuelve a
    /// armar al despertar: una cita que vence antes de lo necesario le cuesta una vuelta de más, que sale más barata que
    /// moverla, y una tanda de armados casi iguales no pasa por el cerrojo.
    /// </summary>
    public static void Adelantar(Cita cita, long horaNs, long holguraNs = Holgura)
    {
        long pendiente = Volatile.Read(ref cita.ProgramadaPara);
        if (pendiente != long.MaxValue && pendiente <= horaNs + holguraNs) return;

        bool avisar;
        lock (_cerrojo)
        {
            pendiente = cita.ProgramadaPara;
            if (pendiente != long.MaxValue && pendiente <= horaNs + holguraNs) return;
            _citas.Poner(cita, horaNs);
            avisar = horaNs < _dormidoHasta;
            if (avisar) _dormidoHasta = horaNs;
        }
        if (avisar) _aviso.Set();
    }

    /// <summary>Que no venza, y fuera del montículo: ya no retiene a su dueño.</summary>
    public static void Anular(Cita cita)
    {
        lock (_cerrojo) _citas.Quitar(cita);
    }

    /// <summary>
    /// Espera <paramref name="nanos"/> nanosegundos, con precisión de ~1 ms: una tarea nueva cada vez. Es la del ritmo de envío
    /// de QUIC: sobre una espera reutilizable, que no reservaba, la subida por un camino lejano iba más lenta.
    /// </summary>
    public static Task EsperarAsync(long nanos, CancellationToken ct)
    {
        if (nanos <= MargenNs) return Task.CompletedTask;
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);

        var cita = new CitaDeTarea();
        Programar(cita, AhoraNs() + nanos);
        if (ct.CanBeCanceled) cita.Vigilar(ct);
        return cita.Tarea;
    }

    /// <summary>La cita de <see cref="EsperarAsync"/>: una tarea que se completa al vencer, o se cancela y sale del montículo.</summary>
    private sealed class CitaDeTarea : Cita
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registro;

        public Task Tarea => _tcs.Task;

        public void Vigilar(CancellationToken ct)
            => _registro = ct.UnsafeRegister(static (estado, ct) =>
            {
                var cita = (CitaDeTarea)estado!;
                Anular(cita);
                cita._tcs.TrySetCanceled(ct);
            }, this);

        protected internal override long Vencer(long ahora)
        {
            _registro.Dispose();
            _tcs.TrySetResult();   // la continuación va al pool, no corre aquí
            return long.MaxValue;
        }
    }

    private static void Bucle()
    {
        var vencidas = new List<Cita>();
        while (true)
        {
            long ahora = AhoraNs();
            long proxima;
            lock (_cerrojo)
            {
                _dormidoHasta = long.MinValue;   // despierto: lo que se programe ahora lo ve la vuelta siguiente
                while (_citas.Cuantas > 0 && _citas.Primera.ProgramadaPara <= ahora + MargenNs)
                {
                    var cita = _citas.Primera;
                    _citas.Quitar(cita);   // sin hora: deja de estar pendiente antes de vencer
                    vencidas.Add(cita);
                }
                proxima = _citas.Cuantas > 0 ? _citas.Primera.ProgramadaPara : long.MaxValue;
                if (vencidas.Count == 0)
                {
                    _dormidoHasta = proxima;
                    _citas.Recortar();
                }
            }

            if (vencidas.Count > 0)
            {
                // Fuera del cerrojo: cada una solo avisa —libera una espera, despierta una bomba—, y una que quiera volver
                // se programa otra vez.
                foreach (var cita in vencidas)
                {
                    long otra;
                    try { otra = cita.Vencer(ahora); }
                    catch { otra = long.MaxValue; }   // una cita que lanza no puede parar las de los demás
                    if (otra != long.MaxValue) Programar(cita, otra);
                }
                vencidas.Clear();
                continue;   // el tiempo pasó mientras vencían: se mira otra vez antes de dormir
            }

            if (proxima == long.MaxValue)
            {
                _aviso.WaitOne();
                continue;
            }

            long faltaNs = proxima - AhoraNs();
            if (faltaNs <= MargenNs) continue;

            if (_temporizador is null)
            {
                // Fuera de Windows, o sin su temporizador de alta resolución: la espera del sistema, al milisegundo más cercano.
                _aviso.WaitOne((int)Math.Min(int.MaxValue, Math.Max(1, (faltaNs + 500_000) / 1_000_000)));
                continue;
            }

            // Plazo relativo, en centenas de nanosegundos: va en negativo.
            long vencimiento = -Math.Max(1, faltaNs / 100);
            int programado;
            unsafe
            {
                programado = SetWaitableTimer(_manija, &vencimiento, 0, IntPtr.Zero, IntPtr.Zero, 0);
            }

            if (programado == 0)
            {
                Thread.Sleep(1);
                continue;
            }

            // Despierta el temporizador, o una cita nueva más temprana.
            WaitHandle.WaitAny(_esperas!);
        }
    }
}

/// <summary>
/// Una cita del <see cref="DespertadorPreciso"/> que al vencer llama a su aviso —despertar una bomba o un bucle—, en el hilo
/// del despertador, que es de todos: el aviso solo avisa. Se reutiliza: se programa, se adelanta o se anula sin reservar nada.
/// La cita del temporizador de quiche y la del bucle de KCP y mKCP estaban escritas dos veces, igual, con un
/// <c>ContinueWith</c> y su estado en caja por cita.
/// </summary>
internal sealed class CitaPrecisa : Cita
{
    private readonly Action _alVencer;

    public CitaPrecisa(Action alVencer) => _alVencer = alVencer;

    /// <summary>
    /// ¿Está programada y sin vencer? Es lo que dice el despertador, que la deja sin hora al vencer, y no la hora: por la hora,
    /// una cita que vencía algo antes de la suya —el despertador vence por tandas, las que caen en su margen de 100 µs— seguía
    /// contando como pendiente, y su dueño se dormía sin cita.
    /// </summary>
    public bool Pendiente => Volatile.Read(ref ProgramadaPara) != long.MaxValue;

    protected internal override long Vencer(long ahora)
    {
        _alVencer();
        return long.MaxValue;
    }
}
