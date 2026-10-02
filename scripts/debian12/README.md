# Linux build on Debian 12

Builds `libquiche_bindgen.so` for `linux-x64`, `linux-arm64` and `linux-arm` (armhf, 32-bit) against Debian 12 (glibc 2.36), so the libraries also load on newer distributions.

From Windows, through WSL:

```powershell
pwsh scripts/debian12/import-wsl.ps1                  # once: imports the official debian:12 image as "Debian12"
wsl -d Debian12 --user root bash scripts/debian12/setup.sh
wsl -d Debian12 --user root bash scripts/debian12/build.sh --tests
wsl -d Debian12 --user root bash scripts/debian12/build.sh linux-arm   # only some targets
```

On a Debian 12 machine, run `setup.sh` as root and then `build.sh`.

`build.sh` copies the sources to `~/quichenet-build`, so the bindings generated in the repository (`NativeMethods.g.cs`) aren't overwritten. With no target it builds the three. The libraries go to `entrega-quiche/<target>`; set `OUT` to use another directory, and `RUNTIMES` to also copy them to `$RUNTIMES/<target>/native`, the .NET runtimes layout. Pass `--tests` to also run the quiche tests on linux-x64.

For each target it prints the exports (173 `_quiche_` functions), the highest GLIBC symbol version, NEEDED, and the size and offsets that bindgen computed for `quiche_send_info`, `quiche_path_stats` and `quiche_stats`, which are kept in `~/quichenet-build/layout-<target>.rs`. On `linux-arm`, `timespec` is `{i32, i32}` and `sockaddr_storage` is 4-aligned, so those structs differ from the 64-bit ones: a C# wrapper has to read them with these offsets.

The header copy, `quiche-bindgen/include/quiche.h`, has to declare `quiche_config_set_initial_rtt`; `build.sh` stops if it doesn't.

Run the `wsl` commands from the repository root: WSL starts in the current Windows directory. From Git Bash, set `MSYS_NO_PATHCONV=1` so paths passed in `OUT` or `RUNTIMES` reach WSL unchanged.

Step-by-step commands for a Debian 12 machine, including .NET and ExampleApp, are in [COMPILAR-EN-DEBIAN.md](COMPILAR-EN-DEBIAN.md).
