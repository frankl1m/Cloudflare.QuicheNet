# Linux build on Debian 12

Builds `libquiche_bindgen.so` for `linux-x64` and `linux-arm64` against Debian 12 (glibc 2.36), so the libraries also load on newer distributions.

From Windows, through WSL:

```powershell
pwsh scripts/debian12/import-wsl.ps1                  # once: imports the official debian:12 image as "Debian12"
wsl -d Debian12 --user root bash scripts/debian12/setup.sh
wsl -d Debian12 --user root bash scripts/debian12/build.sh --tests
```

On a Debian 12 machine, run `setup.sh` as root and then `build.sh`.

`build.sh` copies the sources to `~/quichenet-build`, so the bindings generated in the repository (`NativeMethods.g.cs`) aren't overwritten. The libraries go to `entrega-quiche/linux-x64` and `entrega-quiche/linux-arm64`; set `OUT` to use another directory. Pass `--tests` to also run the quiche tests on linux-x64.

Run the `wsl` commands from the repository root: WSL starts in the current Windows directory.
