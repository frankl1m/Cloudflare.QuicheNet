using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Cloudflare.Quiche
{
    /// <summary>
    /// Dónde está el nativo de quiche en cada plataforma: el RID de su carpeta, <c>runtimes/&lt;rid&gt;/native</c>, y el
    /// nombre de la librería en ese sistema. Lo usan la puerta (<c>MotorQuiche</c>) y el resolvedor del wrapper
    /// (<see cref="NativeMethods"/>), que antes repetían cada uno su mapa y no conocían ni Android ni el ARM de 32 bits.
    /// <para>
    /// En Android no hay carpeta junto al binario: .NET para Android mete la librería en <c>lib/&lt;abi&gt;</c> del APK
    /// —desde un NuGet con <c>runtimes/android-*/native</c>, o desde los <c>AndroidNativeLibrary</c> que declara
    /// <c>Socks5Lib.Android.targets</c>—, y el cargador del sistema la encuentra por su nombre. Allí se carga por nombre
    /// primero, y la ruta de <c>runtimes</c> queda de segunda.
    /// </para>
    /// </summary>
    internal sealed class UbicacionDelNativo
    {
        /// <summary>Los sistemas que el mapa distingue. Android va aparte de Linux: allí <c>IsLinux()</c> es falso.</summary>
        internal enum Sistema { Windows, Linux, MacOS, Android, IOS, MacCatalyst, Otro }

        /// <summary>El RID de la carpeta: <c>win-x64</c>, <c>linux-arm</c>, <c>android-arm64</c>…</summary>
        public string Rid { get; }

        /// <summary>El nombre de la librería en ese sistema.</summary>
        public string Libreria { get; }

        /// <summary>Si se carga por nombre antes que por ruta (Android).</summary>
        public bool PorNombre { get; }

        private UbicacionDelNativo(string rid, string libreria, bool porNombre)
        {
            Rid = rid;
            Libreria = libreria;
            PorNombre = porNombre;
        }

        internal static Sistema SistemaActual =>
            OperatingSystem.IsAndroid() ? Sistema.Android
            : OperatingSystem.IsMacCatalyst() ? Sistema.MacCatalyst
            : OperatingSystem.IsIOS() ? Sistema.IOS
            : OperatingSystem.IsWindows() ? Sistema.Windows
            : OperatingSystem.IsLinux() ? Sistema.Linux
            : OperatingSystem.IsMacOS() ? Sistema.MacOS
            : Sistema.Otro;

        /// <summary>La de este proceso; nula donde no hay nativo que buscar (ver <see cref="Motivo"/>).</summary>
        internal static readonly UbicacionDelNativo? Actual = Para(SistemaActual, RuntimeInformation.ProcessArchitecture);

        /// <summary>La ubicación para un sistema y una arquitectura, o nula si el motor no existe ahí.</summary>
        internal static UbicacionDelNativo? Para(Sistema sistema, Architecture arquitectura)
        {
            string? arch = Arquitectura(arquitectura);
            if (arch is null) return null;
            return sistema switch
            {
                Sistema.Windows => new UbicacionDelNativo($"win-{arch}", "quiche_bindgen.dll", porNombre: false),
                Sistema.Linux   => new UbicacionDelNativo($"linux-{arch}", "libquiche_bindgen.so", porNombre: false),
                Sistema.MacOS   => new UbicacionDelNativo($"osx-{arch}", "libquiche_bindgen.dylib", porNombre: false),
                Sistema.Android => new UbicacionDelNativo($"android-{arch}", "libquiche_bindgen.so", porNombre: true),
                _ => null,
            };
        }

        /// <summary>Por qué <see cref="Para"/> no da ubicación para un sistema y una arquitectura.</summary>
        internal static string Motivo(Sistema sistema, Architecture arquitectura) =>
            Arquitectura(arquitectura) is null
                ? $"el motor quiche no existe para esta arquitectura ({arquitectura})"
                : sistema is Sistema.IOS or Sistema.MacCatalyst
                ? $"{sistema} usa el XCFramework estático del paquete NativeAssets.iOS y el ensamblado específico de su plataforma; no carga una dylib de macOS"
                : "el motor quiche solo existe en Windows, Linux, macOS y Android";

        private static string? Arquitectura(Architecture a) => a switch
        {
            Architecture.X64   => "x64",
            Architecture.X86   => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm   => "arm",
            _ => null,
        };

        /// <summary>La ruta de la librería en <c>runtimes/&lt;rid&gt;/native</c> bajo <paramref name="directorioBase"/>.</summary>
        public string RutaJuntoAlBinario(string directorioBase) =>
            Path.Combine(directorioBase, "runtimes", Rid, "native", Libreria);

        /// <summary>
        /// Carga la librería: de <paramref name="directorio"/> si se da; si no, por nombre donde toca (Android) y luego
        /// de <c>runtimes/&lt;rid&gt;/native</c> junto al binario.
        /// </summary>
        public bool TryCargar(string? directorio, out IntPtr handle)
        {
            if (directorio is not null)
                return NativeLibrary.TryLoad(Path.Combine(directorio, Libreria), out handle);
            if (PorNombre && NativeLibrary.TryLoad(Libreria, out handle))
                return true;
            if (NativeLibrary.TryLoad(RutaJuntoAlBinario(AppContext.BaseDirectory), out handle)) return true;
            // A manual universal app can ship one library for both Mac architectures.
            if (Rid.StartsWith("osx-", StringComparison.Ordinal))
                return NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "runtimes", "osx-universal", "native", Libreria), out handle);
            return false;
        }
    }
}
