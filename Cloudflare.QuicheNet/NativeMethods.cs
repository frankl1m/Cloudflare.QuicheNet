using GroupedNativeMethodsGenerator;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Cloudflare.Quiche
{
    [GroupedNativeMethods]
    internal static partial class NativeMethods
    {
        internal const string LibraryName = __DllName;

        public static string? LibraryDirPath { get; set; }

        static NativeMethods()
        {
#if !IOS && !MACCATALYST && !TVOS
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), ImportResolver);
#endif
        }

        private static IntPtr ImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName != __DllName)
            {
                return IntPtr.Zero;
            }

            // El mapa de SO y arquitectura es el de la puerta (UbicacionDelNativo). Lo que no se encuentre aquí vuelve
            // con IntPtr.Zero a la búsqueda por omisión del runtime, que en .NET para Android conoce las librerías de la app.
            if (UbicacionDelNativo.Actual is not { } ubicacion)
            {
                return IntPtr.Zero;
            }

            return ubicacion.TryCargar(LibraryDirPath, out IntPtr handle) ? handle : IntPtr.Zero;
        }
    }
}
