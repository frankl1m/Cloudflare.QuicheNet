# Build de Linux (Debian 12)

`libquiche_bindgen.so` compila para **linux-x64** y **linux-arm64** en Debian 12, y el build está guardado en `scripts/debian12/` para repetirlo.

## Resultado

- **Compatibilidad.** Las dos librerías se compilaron en Debian 12.15 y piden como mucho **glibc 2.34**, así que cargan en Debian 12 (glibc 2.36) y en distribuciones más nuevas.
- **Funciones exportadas.** Cada `.so` exporta las 172 funciones `_quiche_`, igual que las DLL de Windows, incluidas `_quiche_conn_set_brutal_rate` y `_quiche_conn_export_keying_material`.
- **Pruebas.** En linux-x64 pasan **1067 pruebas de quiche y no falla ninguna**. Son las mismas que en Windows, menos las dos que solo se ejecutan allí (`send_info.at` en Windows).
- **arm64.** La versión arm64 se compiló en cruzado y no se ha ejecutado en hardware ARM.
- **C#.** El `NativeMethods.g.cs` entregado también sirve en Linux. Generado allí solo cambia `int` por `uint` en algunos parámetros (`socklen_t` y enums), que tienen el mismo tamaño.

| Fichero | SHA-256 |
|---|---|
| `linux-x64/libquiche_bindgen.so` | `c1b7a3e635a3c044183d7a9308c3e4e0e966a50ec69b1c7d002695bea80e3e89` |
| `linux-arm64/libquiche_bindgen.so` | `a0a7e6a43614a807b6a78de4a17f964f76359726ad5ace15e3d3a8d58eaca271` |

## Cómo repetir el build

Desde la raíz del repo, en Windows con WSL (detalles en `scripts/debian12/README.md`):

```powershell
pwsh scripts/debian12/import-wsl.ps1
wsl -d Debian12 --user root bash scripts/debian12/setup.sh
wsl -d Debian12 --user root bash scripts/debian12/build.sh --tests
```

- **`import-wsl.ps1`**: descarga la imagen oficial `debian:12`, verifica su SHA-256 y la importa en WSL como `Debian12`. Solo hace falta la primera vez.
- **`setup.sh`**: instala Rust, cmake, clang y el compilador cruzado para arm64.
- **`build.sh`**: compila sobre una copia en `~/quichenet-build`, así que no reescribe los bindings del repo. Deja las `.so` en `entrega-quiche/linux-x64` y `entrega-quiche/linux-arm64`. Con `--tests` también ejecuta las pruebas de quiche.

En una máquina con Debian 12 basta con ejecutar `setup.sh` como root y después `build.sh`.

## Qué se instaló y cambió

- **Distro WSL `Debian12`**, creada a partir de la imagen oficial `debian:12`, con Rust 1.98.1, cmake 3.25, clang 14 y `aarch64-linux-gnu-gcc` 12. El Ubuntu que ya había no se tocó.
- **`Cloudflare.QuicheNet.NativeAssets.Linux.csproj`**: ejecutaba `cargo cross build`, que no existe; ahora usa `cargo build` con `RustTargetDir` configurable. Esa compilación nativa necesita un host Linux.
- **`.gitattributes`**: los `.sh` mantienen finales de línea LF, para que `bash` no falle con un checkout hecho en Windows.

## Dónde está

Rama `feature/brutal-tuic` de `frankl1m/Cloudflare.QuicheNet`: commit `b6eaab3`, junto con la sección de Linux de `INFORME.md`.
