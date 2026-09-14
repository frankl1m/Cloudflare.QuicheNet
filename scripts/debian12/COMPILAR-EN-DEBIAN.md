# Compilar en Debian 12

Comandos para compilar `libquiche_bindgen.so` (linux-x64 y linux-arm64) y QuicheNet en una máquina Debian 12. Se probaron tal cual sobre un clon limpio de `frankl1m/Cloudflare.QuicheNet` (rama `feature/brutal-tuic`), en Debian 12.15.

Los comandos con `sudo` necesitan permisos de administrador. El resto se ejecuta con tu usuario.

## 1. Dependencias (una sola vez)

```bash
sudo apt-get update
sudo apt-get install -y --no-install-recommends \
    ca-certificates curl git rsync file binutils \
    build-essential cmake ninja-build perl golang pkg-config \
    clang libclang-dev

# Solo si también vas a compilar linux-arm64 desde una máquina x64:
sudo apt-get install -y --no-install-recommends \
    gcc-aarch64-linux-gnu g++-aarch64-linux-gnu libc6-dev-arm64-cross

# Rust (en tu usuario)
curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y
. "$HOME/.cargo/env"
rustup target add x86_64-unknown-linux-gnu aarch64-unknown-linux-gnu
```

## 2. Descargar el código

```bash
git clone --recurse-submodules -b feature/brutal-tuic https://github.com/frankl1m/Cloudflare.QuicheNet.git
cd Cloudflare.QuicheNet
```

El submódulo `quiche` apunta a `frankl1m/quiche`. Haz el clon completo, sin `--depth`: el commit del submódulo está en una rama que no es la principal.

## 3. Compilar linux-x64

```bash
cd quiche-bindgen
cargo build --release --target x86_64-unknown-linux-gnu
ls -l target/x86_64-unknown-linux-gnu/release/libquiche_bindgen.so
```

Este build también regenera `Cloudflare.QuicheNet/NativeMethods.g.cs` con los tipos de Linux. Git ignora ese fichero, y QuicheNet compila tanto con la versión generada en Linux como con la de Windows.

## 4. Compilar linux-arm64 (compilación cruzada desde x64)

Desde `quiche-bindgen`:

```bash
export CC_aarch64_unknown_linux_gnu=aarch64-linux-gnu-gcc
export CXX_aarch64_unknown_linux_gnu=aarch64-linux-gnu-g++
export AR_aarch64_unknown_linux_gnu=aarch64-linux-gnu-ar
export CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_LINKER=aarch64-linux-gnu-gcc
export BINDGEN_EXTRA_CLANG_ARGS_aarch64_unknown_linux_gnu="--sysroot=/usr/aarch64-linux-gnu -I/usr/aarch64-linux-gnu/include"

cargo build --release --target aarch64-unknown-linux-gnu
ls -l target/aarch64-unknown-linux-gnu/release/libquiche_bindgen.so
```

En una máquina arm64 con Debian 12 no hace falta nada de esto: basta con `cargo build --release`.

## 5. Comprobar las librerías

```bash
for t in x86_64 aarch64; do
    lib="target/$t-unknown-linux-gnu/release/libquiche_bindgen.so"
    file -b "$lib" | cut -d, -f1-2
    nm -D --defined-only "$lib" | grep -E ' T _quiche_conn_(set_brutal_rate|export_keying_material)$'
    objdump -T "$lib" | grep -o 'GLIBC_[0-9.]*' | sort -Vu | tail -1
done
```

Resultado esperado para cada arquitectura:

```
ELF 64-bit LSB shared object, x86-64          (o ARM aarch64)
... T _quiche_conn_export_keying_material
... T _quiche_conn_set_brutal_rate
GLIBC_2.34
```

`GLIBC_2.34` es la versión más alta de glibc que piden. Debian 12 tiene la 2.36, así que cargan ahí y en distribuciones más nuevas.

## 6. Ejecutar las pruebas de quiche

```bash
cd ../quiche
cargo test -p quiche --features ffi,qlog --lib
```

Resultado esperado: `test result: ok. 1067 passed; 0 failed`.

## 7. Alternativa: los scripts del repo

Desde la raíz del repo, como root (`setup.sh` instala Rust en la cuenta de root):

```bash
sudo bash scripts/debian12/setup.sh
sudo bash scripts/debian12/build.sh --tests
```

`build.sh` compila las dos arquitecturas sobre una copia en `~/quichenet-build`, sin tocar los ficheros generados del repo. Deja las `.so` en `entrega-quiche/linux-x64` y `entrega-quiche/linux-arm64`, y con `--tests` también ejecuta las pruebas.

## 8. .NET en Debian 12 (opcional)

SDK de .NET 8 y 10 desde el repositorio de Microsoft:

```bash
curl -sSLO https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0 dotnet-sdk-10.0
```

Compilar QuicheNet (desde la raíz del repo):

```bash
dotnet build Cloudflare.QuicheNet/Cloudflare.QuicheNet.csproj -c Release -p:TargetFrameworks=net8.0
```

En Linux indica siempre la TFM con `-p:TargetFrameworks=...`. Si no, .NET intenta compilar también las TFM de Android, iOS y macOS, que necesitan cargas de trabajo no disponibles en Linux.

Ejecutar ExampleApp con la librería de linux-x64:

```bash
dotnet build Cloudflare.QuicheNet.ExampleApp/Cloudflare.QuicheNet.ExampleApp.csproj -c Release -p:TargetFrameworks=net10.0

OUTDIR=Cloudflare.QuicheNet.ExampleApp/bin/Release/net10.0
mkdir -p "$OUTDIR/runtimes/linux-x64/native" "$OUTDIR/trust"
cp quiche-bindgen/target/x86_64-unknown-linux-gnu/release/libquiche_bindgen.so "$OUTDIR/runtimes/linux-x64/native/"
cp quiche/quiche/examples/cert.crt "$OUTDIR/trust/cert.pem"
cp quiche/quiche/examples/cert.key "$OUTDIR/trust/key.pem"

(cd "$OUTDIR" && dotnet Cloudflare.QuicheNet.ExampleApp.dll)
```

Al terminar, la salida debe acabar con `Client: got DATAGRAM from server saying "DATAGRAM from Server!"` y `Server: got DATAGRAM from client saying "DATAGRAM from Client!"`.

En tus aplicaciones, QuicheNet busca la librería en `runtimes/linux-x64/native/libquiche_bindgen.so` (o `linux-arm64`) junto al ejecutable.
