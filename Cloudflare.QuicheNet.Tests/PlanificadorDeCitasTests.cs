using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Cloudflare.QuicheNet.Internal;

namespace Cloudflare.QuicheNet.Tests
{
    /// <summary>El planificador de citas: un bucle, un montículo y plazos perezosos.</summary>
    internal static class PlanificadorDeCitasTests
    {
        /// <summary>Una cita que apunta cuándo venció y decide la siguiente con una función.</summary>
        private sealed class CitaDePrueba : Cita
        {
            private readonly Func<long, long> _siguiente;
            public readonly ConcurrentQueue<long> Vencimientos = new();
            public readonly string Nombre;
            private readonly ConcurrentQueue<string>? _orden;

            public CitaDePrueba(string nombre, Func<long, long>? siguiente = null, ConcurrentQueue<string>? orden = null)
            {
                Nombre = nombre; _siguiente = siguiente ?? (_ => long.MaxValue); _orden = orden;
            }

            protected internal override long Vencer(long ahora)
            {
                Vencimientos.Enqueue(ahora);
                _orden?.Enqueue(Nombre);
                return _siguiente(ahora);
            }
        }

        private static long Ahora => Environment.TickCount64;

        public static void Register()
        {
            Harness.Test("montículo de citas: mover antes y después, quitar, y siempre sale la primera", () =>
            {
                // El del planificador, que ahora comparte el despertador preciso: allí las citas se mueven también a más tarde.
                var m = new MonticuloDeCitas(4);
                var citas = new CitaDePrueba[200];
                var azar = new Random(7);
                for (int i = 0; i < citas.Length; i++) m.Poner(citas[i] = new CitaDePrueba("c" + i), azar.Next(1_000_000));
                for (int i = 0; i < citas.Length; i += 2) m.Poner(citas[i], azar.Next(1_000_000));   // antes o después
                for (int i = 0; i < citas.Length; i += 5) m.Quitar(citas[i]);
                Harness.Equal(160, m.Cuantas, "quedan las que no se quitaron");
                Harness.True(citas[0].Indice == -1 && citas[0].ProgramadaPara == long.MaxValue, "una quitada, fuera y sin hora");

                long anterior = long.MinValue;
                int sacadas = 0;
                while (m.Cuantas > 0)
                {
                    var primera = m.Primera;
                    Harness.True(primera.ProgramadaPara >= anterior, "siempre sale la que vence antes");
                    anterior = primera.ProgramadaPara;
                    m.Quitar(primera);
                    sacadas++;
                }
                Harness.Equal(160, sacadas, "y salen todas");
            });

            Harness.Test("planificador: las citas vencen en su orden, no en el de llegada", async () =>
            {
                using var p = new PlanificadorDeCitas(log: null);
                var orden = new ConcurrentQueue<string>();
                long t = Ahora;
                p.Programar(new CitaDePrueba("c", orden: orden), t + 90);
                p.Programar(new CitaDePrueba("a", orden: orden), t + 30);
                p.Programar(new CitaDePrueba("b", orden: orden), t + 60);

                await Harness.EsperarAsync(() => orden.Count == 3, "vencen las tres", 5);
                Harness.Equal("a,b,c", string.Join(",", orden), "en el orden de su hora");
            });

            Harness.Test("planificador: una cita perezosa se reprograma sola y se anula", async () =>
            {
                using var p = new PlanificadorDeCitas(log: null);
                int veces = 0;
                var cita = new CitaDePrueba("perezosa", ahora => Interlocked.Increment(ref veces) < 3 ? ahora + 20 : long.MaxValue);
                p.Programar(cita, Ahora + 20);
                await Harness.EsperarAsync(() => Volatile.Read(ref veces) == 3, "vence tres veces y para", 5);
                await Task.Delay(100);
                Harness.Equal(3, Volatile.Read(ref veces), "tras devolver MaxValue no vuelve");

                var anulada = new CitaDePrueba("anulada");
                p.Programar(anulada, Ahora + 50);
                p.Anular(anulada);
                await Task.Delay(200);
                Harness.True(anulada.Vencimientos.IsEmpty, "anulada no vence");
            });

            Harness.Test("planificador: adelantar una cita lejana despierta al bucle", async () =>
            {
                using var p = new PlanificadorDeCitas(log: null);
                var lejana = new CitaDePrueba("lejana");
                p.Programar(new CitaDePrueba("ancla"), Ahora + 60_000);   // el bucle duerme hasta aquí
                p.Programar(lejana, Ahora + 30_000);
                await Task.Delay(50);

                long pedido = Ahora;
                p.Programar(lejana, pedido + 20);
                await Harness.EsperarAsync(() => !lejana.Vencimientos.IsEmpty, "la adelantada vence", 5);
                lejana.Vencimientos.TryPeek(out long cuando);
                Harness.InRange(cuando - pedido, 0, 1000, "vence cuando se pidió, no a los 30 s");
                Harness.Equal(1, lejana.Vencimientos.Count, "y una sola vez: su entrada vieja se tira");
            });

            Harness.Test("planificador: la vuelta que una cita se programa al vencer no despierta al bucle, que ya está despierto", async () =>
            {
                using var p = new PlanificadorDeCitas(log: null);
                await Task.Delay(100);   // el bucle, dormido y sin citas
                long antes = p.Despertares;
                int veces = 0;
                var cita = new CitaDePrueba("se reprograma", ahora => Interlocked.Increment(ref veces) == 1 ? ahora + 60_000 : long.MaxValue);
                p.Programar(cita, Ahora + 20);
                await Harness.EsperarAsync(() => Volatile.Read(ref veces) == 1, "vence", 5);
                await Task.Delay(100);
                Harness.Equal(1L, p.Despertares - antes,
                              "un solo aviso, el de la cita nueva: su vuelta la ve el bucle solo, y avisarle le costaba una vuelta de más");
                Harness.Equal(1, p.Pendientes, "y la vuelta queda programada");

                // Una que vence antes de cuando el bucle iba a despertar lo despierta; una de después, no.
                p.Programar(new CitaDePrueba("antes"), Ahora + 30_000);
                Harness.Equal(2L, p.Despertares - antes, "adelantar su sueño lo despierta");
                p.Programar(new CitaDePrueba("después"), Ahora + 45_000);
                Harness.Equal(2L, p.Despertares - antes, "una que vence después de su sueño, no");
            });

            Harness.Test("planificador: una cita que lanza no para las demás", async () =>
            {
                string? aviso = null;
                using var p = new PlanificadorDeCitas(log: m => aviso = m);
                var buena = new CitaDePrueba("buena");
                p.Programar(new CitaDePrueba("mala", _ => throw new InvalidOperationException("mal")), Ahora + 10);
                p.Programar(buena, Ahora + 30);
                await Harness.EsperarAsync(() => !buena.Vencimientos.IsEmpty, "la buena vence igual", 5);
                Harness.True(aviso != null && aviso.Contains("mal"), $"y la mala deja aviso: {aviso}");
            });

            Harness.Test("planificador: diez mil citas con un solo bucle", async () =>
            {
                using var p = new PlanificadorDeCitas(log: null);
                int vencidas = 0;
                var citas = new CitaDePrueba[10_000];
                long t = Ahora;
                for (int i = 0; i < citas.Length; i++)
                {
                    citas[i] = new CitaDePrueba($"c{i}", _ => { Interlocked.Increment(ref vencidas); return long.MaxValue; });
                    p.Programar(citas[i], t + 50 + i % 100);
                }
                await Harness.EsperarAsync(() => Volatile.Read(ref vencidas) == citas.Length,
                                           () => $"vencen todas: {Volatile.Read(ref vencidas)} de {citas.Length}", 10);
                Harness.Equal(0, p.Pendientes, "y el montículo queda vacío");
            });

            Harness.Test("planificador: anular una cita la saca del montículo y no la retiene", () =>
            {
                using var p = new PlanificadorDeCitas(log: null);
                var lejanas = new CitaDePrueba[100];
                for (int i = 0; i < lejanas.Length; i++)
                {
                    lejanas[i] = new CitaDePrueba($"l{i}");
                    p.Programar(lejanas[i], Ahora + 60_000 + i);
                }

                // Una que se anula: sale ya, no a su hora, y con ella lo que retuviera —una conexión cerrada
                // se quedaba en memoria los 130 s de su plazo—.
                var debil = ProgramarYAnular(p);
                Harness.Equal(100, p.Pendientes, "anulada, sale del montículo en el acto");
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Harness.True(!debil.IsAlive, "y el planificador ya no la retiene");

                // Anular una del medio no desordena las demás: adelantadas, vencen en su orden.
                for (int i = 0; i < lejanas.Length; i += 2) p.Anular(lejanas[i]);
                Harness.Equal(50, p.Pendientes, "quedan las que no se anularon");
            });
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static WeakReference ProgramarYAnular(PlanificadorDeCitas p)
        {
            var c = new CitaDePrueba("anulada");
            p.Programar(c, Ahora + 60_000);
            p.Anular(c);
            return new WeakReference(c);
        }
    }
}
