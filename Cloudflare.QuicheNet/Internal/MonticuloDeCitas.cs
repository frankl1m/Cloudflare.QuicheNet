using System;
using System.Threading;

namespace Cloudflare.QuicheNet.Internal
{
    /// <summary>
    /// Un montículo de citas por su hora —<see cref="Cita.ProgramadaPara"/>, en la escala de quien lo usa—, en el que cada cita
    /// sabe su sitio: ponerla otra vez la mueve donde está, antes o después, y quitarla la saca de verdad, sin retener nada
    /// hasta su hora. Sin candado propio: lo toma su dueño. Lo comparten el planificador, en milisegundos, y el despertador
    /// preciso, en nanosegundos; estaba escrito dentro del primero.
    /// </summary>
    internal struct MonticuloDeCitas
    {
        private Cita?[] _citas;
        private int _cuantas;

        public MonticuloDeCitas(int capacidad) => _citas = new Cita?[capacidad];

        /// <summary>Las citas que hay.</summary>
        public readonly int Cuantas => _cuantas;

        /// <summary>La que vence antes. Solo con alguna.</summary>
        public readonly Cita Primera => _citas[0]!;

        /// <summary>Que <paramref name="cita"/> venza a las <paramref name="cuando"/>: la mete, o la mueve donde esté.</summary>
        public void Poner(Cita cita, long cuando)
        {
            long antes = cita.ProgramadaPara;
            Volatile.Write(ref cita.ProgramadaPara, cuando);
            if (cita.Indice < 0)
            {
                if (_cuantas == _citas.Length) Array.Resize(ref _citas, _cuantas * 2);
                _citas[_cuantas] = cita;
                cita.Indice = _cuantas++;
                Subir(cita.Indice);
            }
            else if (cuando < antes) Subir(cita.Indice);
            else if (cuando > antes) Bajar(cita.Indice);
        }

        /// <summary>La saca, si está, y la deja sin hora: ya no vence ni retiene nada.</summary>
        public void Quitar(Cita cita)
        {
            Volatile.Write(ref cita.ProgramadaPara, long.MaxValue);
            int i = cita.Indice;
            if (i < 0) return;
            cita.Indice = -1;
            var ultima = _citas[--_cuantas]!;
            _citas[_cuantas] = null;
            if (i == _cuantas) return;
            _citas[i] = ultima;
            ultima.Indice = i;
            Subir(i);
            Bajar(ultima.Indice);
        }

        /// <summary>Que el arreglo no se quede grande para siempre tras un pico de citas.</summary>
        public void Recortar()
        {
            if (_citas.Length > 1024 && _cuantas < _citas.Length / 4)
                Array.Resize(ref _citas, Math.Max(64, _citas.Length / 2));
        }

        private readonly void Subir(int i)
        {
            var cita = _citas[i]!;
            long clave = cita.ProgramadaPara;
            while (i > 0)
            {
                int padre = (i - 1) >> 1;
                var p = _citas[padre]!;
                if (p.ProgramadaPara <= clave) break;
                _citas[i] = p;
                p.Indice = i;
                i = padre;
            }
            _citas[i] = cita;
            cita.Indice = i;
        }

        private readonly void Bajar(int i)
        {
            var cita = _citas[i]!;
            long clave = cita.ProgramadaPara;
            while (true)
            {
                int hijo = 2 * i + 1;
                if (hijo >= _cuantas) break;
                if (hijo + 1 < _cuantas && _citas[hijo + 1]!.ProgramadaPara < _citas[hijo]!.ProgramadaPara) hijo++;
                var h = _citas[hijo]!;
                if (h.ProgramadaPara >= clave) break;
                _citas[i] = h;
                h.Indice = i;
                i = hijo;
            }
            _citas[i] = cita;
            cita.Indice = i;
        }
    }
}
