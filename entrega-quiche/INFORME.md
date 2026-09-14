# quiche para Socks5Lib: Brutal (Hysteria 2) y exportador TLS (TUIC v5)

## Qué contiene esta entrega

| Fichero | Qué es |
|---|---|
| `brutal.patch` | Control de congestión Brutal y `quiche_conn_set_brutal_rate`, con su parte de los dos `quiche.h` |
| `tuic-exporter.patch` | Exportador de material de claves TLS y `quiche_conn_export_keying_material`, con su parte de los dos `quiche.h` |
| `windows-send-at.patch` | `send_info.at` real en Windows; sin él no hay pacing a través de la FFI |
| `quichenet-csharp.patch` | Wrappers y correcciones de QuicheNet (C#), multi-target net7.0/net8.0, y `ssize_t` → `nint` en los bindings |
| `x64/`, `x86/` | `quiche_bindgen.dll`, `.dll.lib` y `.dll.exp`, con los cuatro parches |
| `NativeMethods.g.cs` | Bindings C# regenerados; idénticos para x86 y x64 |
| `generated/quiche_ffi.rs`, `generated/quiche.rs` | Resto de lo generado por `build.rs`, solo como referencia |

**SHA-256 de las DLL**

- x64: `68CA14B35A83E602C755C99F2AE6A1F7ABE8E5C0EF5978803A3FF98DE2F7DE0A`
- x86: `4586BE949D97BFE1F4D5C84922CDACD5E1D746358B7F4F873B4F7FF246325BE6`

## Linux (Debian 12)

Las librerías de Linux se compilan con los cuatro parches aplicados, dentro de una distro WSL 2 con Debian 12.15. Esa distro sale de la imagen oficial `debian:12` de Docker Hub, verificada por SHA-256.

**Toolchain:** glibc 2.36, rustc 1.98.1, cmake 3.25.1, clang 14.0.6 y `aarch64-linux-gnu-gcc` 12.2 para la compilación cruzada.

| Fichero | SHA-256 |
|---|---|
| `linux-x64/libquiche_bindgen.so` | `c1b7a3e635a3c044183d7a9308c3e4e0e966a50ec69b1c7d002695bea80e3e89` |
| `linux-arm64/libquiche_bindgen.so` | `a0a7e6a43614a807b6a78de4a17f964f76359726ad5ace15e3d3a8d58eaca271` |

**Verificación:**

- **Formato:** x86-64 y ARM aarch64 son ELF 64-bit shared object. Solo dependen de `libgcc_s.so.1` y `libc.so.6`, más el cargador en x86-64.
- **glibc:** la versión más alta que requieren es `GLIBC_2.34`, así que cargan en Debian 12 (glibc 2.36) y en distribuciones más nuevas.
- **Exports:** cada una tiene 172 con el prefijo `_quiche_` (`nm -D --defined-only`), igual que las DLL.

  ```
  x86_64:  00000000000f8830 T _quiche_conn_export_keying_material
           00000000000f8af0 T _quiche_conn_set_brutal_rate
           00000000000f8b30 T _quiche_conn_set_session
  aarch64: 0000000000035310 T _quiche_conn_export_keying_material
           00000000000353c8 T _quiche_conn_set_brutal_rate
           00000000000353d8 T _quiche_conn_set_session
  ```

- **Pruebas de quiche en linux-x64** (`cargo test -p quiche --features ffi,qlog --lib`): **1067 passed, 0 failed**. Son las 1069 de Windows menos las dos de `send_info.at` exclusivas de Windows. En Linux, `send_info.at` ya usaba `CLOCK_MONOTONIC`, el mismo reloj que `Stopwatch` de .NET.
- **Bindings:** el `NativeMethods.g.cs` que se genera en Linux solo difiere del del repo en `int` → `uint` en `socklen_t` (longitudes de `sockaddr`) y en algunos parámetros enum. Los dos son de 32 bits y la ABI es la misma, así que el `NativeMethods.g.cs` entregado sirve para Windows y Linux.
- **arm64:** no se ha ejecutado en hardware arm64.

**Cómo reproducirlo:** está en `scripts/debian12/` (ver su `README.md`):

```powershell
pwsh scripts/debian12/import-wsl.ps1
wsl -d Debian12 --user root bash scripts/debian12/setup.sh
wsl -d Debian12 --user root bash scripts/debian12/build.sh --tests
```

**Corrección del csproj de Linux:** `Cloudflare.QuicheNet.NativeAssets.Linux.csproj` ejecutaba `cargo cross build`, que no es un comando de cargo. Ahora ejecuta `cargo build --target-dir $(RustTargetDir)`, que necesita un host Linux.

## Commit base

- QuicheNet `a85b3e790fa98eec5f7392c8b55b75c1749d536b`
- Submódulo quiche `55886df3be579579207104c8e645825b6347a209` (0.29.3)

Los dos coinciden con los pins. El submódulo está completo y no tiene submódulos anidados, porque BoringSSL llega por el crate `boring`. El repositorio no guarda ninguna DLL. Aun así, las DLL compiladas que había (x64 y x86) exportaban 170 funciones, las mismas que el `quiche.h` y el `NativeMethods.g.cs` de esa base, así que la base es coherente con ellas.

`quiche-bindgen/build.rs` lee **su propia copia** `quiche-bindgen/include/quiche.h`, no la del submódulo. Cada parche modifica las dos copias.

## Cómo aplicar

Desde la raíz de QuicheNet, sobre la base limpia:

```
git apply entrega-quiche/brutal.patch
git apply entrega-quiche/tuic-exporter.patch
git apply entrega-quiche/windows-send-at.patch
git apply entrega-quiche/quichenet-csharp.patch
```

- **Aplicación comprobada** en un clon limpio de la base:
  - cada parche por separado;
  - Brutal y el exportador en los dos órdenes;
  - los cuatro seguidos. El resultado es idéntico, byte a byte tras normalizar EOL, al árbol con el que se compilaron las DLL (12 ficheros de quiche y 11 de QuicheNet).
- **Rutas:** los parches de quiche usan rutas `quiche/quiche/...`, y `git apply` los aplica desde la raíz sin problema aunque sea un submódulo.
- **Avisos al aplicar:** `quichenet-csharp.patch` avisa de 3 líneas con espacios finales que ya estaban en el código original.

## Firmas exactas

### Brutal

Rust, en `quiche::Connection`:

```rust
pub fn set_brutal_rate(&mut self, bytes_per_sec: u64) -> Result<()>
```

- Devuelve `Err(Error::CongestionControl)` si el algoritmo no es `Brutal`.
- Se aplica a todos los caminos existentes y también a los que se creen después.

Rust, enum:

```rust
pub enum CongestionControlAlgorithm {
    Reno = 0,
    CUBIC = 1,
    Bbr2Gcongestion = 4,
    Brutal = 5, // nuevo, "brutal" en FromStr
}
```

Rust, FFI:

```rust
#[no_mangle]
pub extern "C" fn quiche_conn_set_brutal_rate(conn: &mut Connection, v: u64) -> c_int
```

C (`quiche.h`):

```c
enum quiche_cc_algorithm { ..., QUICHE_CC_BBR2_GCONGESTION = 4, QUICHE_CC_BRUTAL = 5, };
int quiche_conn_set_brutal_rate(quiche_conn *conn, uint64_t bytes_per_sec);
```

C#, generado:

```csharp
[DllImport(__DllName, EntryPoint = "_quiche_conn_set_brutal_rate", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
internal static extern int quiche_conn_set_brutal_rate(quiche_conn* conn, ulong bytes_per_sec);
```

C#, wrapper:

```csharp
public void QuicheConnection.SetBrutalRate(ulong bytesPerSecond)
```

Lanza `QuicheException` con `QUICHE_ERR_CONGESTION_CONTROL` si el algoritmo no es Brutal.

### Exportador TLS

Rust, en `quiche::Connection`:

```rust
pub fn export_keying_material(&self, label: &[u8], context: &[u8], out: &mut [u8]) -> Result<()>
```

- `Err(Error::InvalidState)` si el saludo no está completo.
- `Err(Error::TlsFail)` si BoringSSL devuelve 0.

Rust, interno (`tls/mod.rs`):

```rust
pub fn Handshake::export_keying_material(&self, label: &[u8], context: &[u8], out: &mut [u8]) -> Result<()>
// extern: SSL_export_keying_material(ssl, out, out_len, label as *const c_char, label_len, context, context_len, 1)
```

La llamada va directa a BoringSSL con bytes crudos. No se usa `boring::ssl::SslRef::export_keying_material`, porque su label es `&str` y los bytes de un UUID no son UTF-8 válido.

Rust, FFI:

```rust
#[no_mangle]
pub extern "C" fn quiche_conn_export_keying_material(
    conn: &Connection, label: *const u8, label_len: size_t,
    context: *const u8, context_len: size_t, out: *mut u8, out_len: size_t,
) -> c_int
```

Con longitud 0, cada buffer se trata como slice vacío y no se desreferencia el puntero, que puede ser nulo.

C (`quiche.h`):

```c
int quiche_conn_export_keying_material(const quiche_conn *conn,
                                       const uint8_t *label, size_t label_len,
                                       const uint8_t *context, size_t context_len,
                                       uint8_t *out, size_t out_len);
```

C#, generado:

```csharp
[DllImport(__DllName, EntryPoint = "_quiche_conn_export_keying_material", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
internal static extern int quiche_conn_export_keying_material(quiche_conn* conn, byte* label, nuint label_len, byte* context, nuint context_len, byte* @out, nuint out_len);
```

C#, wrappers:

```csharp
public byte[] QuicheConnection.ExportKeyingMaterial(ReadOnlySpan<byte> label, ReadOnlySpan<byte> context, int length)
public void   QuicheConnection.ExportKeyingMaterial(ReadOnlySpan<byte> label, ReadOnlySpan<byte> context, Span<byte> output)
```

Token de TUIC v5: `conn.ExportKeyingMaterial(uuid.ToByteArray(bigEndian: true), Encoding.UTF8.GetBytes(password), 32)`.

## Brutal

### Constantes, contrastadas con apernet/hysteria

Fuentes: `core/internal/congestion/brutal/brutal.go` y `core/internal/congestion/common/pacer.go`, rama master, consultadas el 13-09-2026.

| hysteria | Valor | quiche (`recovery/gcongestion/brutal.rs`) |
|---|---|---|
| `pktInfoSlotCount` | 5 | `PKT_INFO_SLOT_COUNT = 5` |
| `minSampleCount` | 50 | `MIN_SAMPLE_COUNT = 50` |
| `minAckRate` | 0.8 | `MIN_ACK_RATE = 0.8` |
| `congestionWindowMultiplier` | 2 | `CONGESTION_WINDOW_MULTIPLIER = 2.0` |
| cwnd sin RTT (`rtt <= 0`) | 10240 | `NO_RTT_CONGESTION_WINDOW = 10240` |
| `maxBurstPackets` | 10 | `MAX_BURST_PACKETS = 10` |
| `maxBurstPacingDelayMultiplier × MinPacingDelay` | 4 × 1 ms | `MAX_BURST_PACING_DELAY = 4 ms` |

### Lógica, igual que en hysteria

- **Tasa de ACK.** Es `acked/(acked+lost)`, contando paquetes y no bytes, sobre ranuras indexadas por `segundo % 5`.
  - Una ranura cuenta si `timestamp >= segundo_actual − 5`.
  - Con menos de 50 muestras, la tasa es 1.0.
  - Nunca baja de 0.8.
- **Ventana.** `cwnd = max(bps × sRTT × 2 / tasa, MSS)`. Sin muestra de RTT es 10240 bytes.
- **Pacing.** `bps / tasa`, y también `bandwidth_estimate` y `max_bandwidth`.
- **Ráfaga máxima.** `max(ritmo/tasa × 4 ms, 10 × MSS)`, en paquetes.
- **`can_send`.** `bytes_in_flight <= cwnd`, con `<=`, como en hysteria.
- **Pérdidas y recovery.** La pérdida solo baja la tasa de ACK; la ventana nunca se reduce. `is_in_recovery()` devuelve siempre false. `on_retransmission_timeout`, `limit_cwnd` y `on_connection_migration` no tocan nada.
- **Ritmo 0.** Es el estado inicial y delega todo en un BBRv2 construido igual que en `GRecovery::new`.
  - Pasar a un ritmo > 0 crea un estado Brutal nuevo, con las ranuras a cero.
  - Volver a 0 crea un BBRv2 nuevo.
- **Enganche.** El trait `CongestionControl` pasa a usar `enum_dispatch`, como `RecoveryOps`/`Recovery`. El `Pacer` guarda `Congestion { BBRv2, Brutal }`, y `GRecovery::new` tiene un brazo para `Brutal`.

### Desviaciones y adaptaciones

1. **Reloj de las ranuras.** hysteria usa los segundos enteros del `monotime` de quic-go. En Rust, `Instant` no tiene época, así que uso segundos enteros desde el primer evento visto tras activar Brutal. La indexación (`ts % 5`) y la comparación son las mismas.
2. **Tamaño real de la ventana.** La comparación de hysteria es inclusiva (`>= actual − 5`). Pero la ranura de hace 5 s comparte índice con la actual y se reinicia antes de sumar, así que la ventana efectiva es de 5 s. La prueba `ack_rate_window_expiry` lo comprueba: las muestras del segundo 1 cuentan en el segundo 5 y dejan de contar en el 7.
3. **Ráfaga.** `pacer.rs` no admitía una ráfaga definida por el controlador.
   - **Cambio:** añadí al trait `fn max_burst_packets(&self) -> Option<usize>`, que por defecto devuelve `None`. Si devuelve `Some(n)`, el pacer usa `n` como lumpy tokens en lugar de su fórmula (máximo 2, y 1 por debajo de 1,2 Mbps o si la cwnd está limitada). BBRv2 no cambia.
   - **Limitación:** el pacer de quiche encadena tiempos de envío (`ideal_next_packet_send_time += delay`) y no lleva un presupuesto de bytes como el de quic-go. Los lumpy tokens solo afectan a `ReleaseDecision::allow_burst`, que usan los integradores Rust (tokio-quiche); `send_info.at` no los tiene en cuenta.
   - **Consumidores por FFI (QuicheNet):** la ráfaga se consigue en C#, que envía de inmediato todo paquete cuyo `at` caiga dentro de 2 ms.
4. **Semilla del sRTT (añadido).** Al fijar un ritmo con conexión ya en marcha, el sRTT se toma del `RttStats` del camino. Así no se cae a la ventana de 10240 bytes hasta el siguiente ACK. hysteria no lo necesita, porque consulta el RTT en cada llamada.
5. **Caminos nuevos (añadido).** `Connection` guarda el ritmo y lo aplica a los caminos creados después (sondeo o migración, en cliente y servidor) y tras `reinit_recovery`. Si no, un camino nuevo arrancaría con ritmo 0, en modo BBR.
6. **Fijar 0 estando ya a 0** no hace nada: no reinicia un BBRv2 que ya está en marcha.
7. **`MaxPacingRate` recorta Brutal.** La configuración `max_pacing_rate` de quiche sigue aplicando `min(max_pacing_rate, ritmo)`, así que no hay que fijarla si se usa Brutal.

## Exportador TLS

- **Requisito de saludo.** Devuelve `InvalidState` mientras `is_established()` sea falso.
- **0-RTT.** En 0-RTT todavía no existe el secreto de exportación, así que en TUIC v5 el comando Authenticate debe enviarse tras el saludo 1-RTT. Los comandos de relay sí pueden ir en early data.

## `send_info.at` en Windows (`windows-send-at.patch`)

- **Problema.** `std_time_to_c` ponía `at = 0` en Windows (había un TODO). Sin ese dato no llega ningún pacing a quien consume la FFI, y Brutal enviaría ráfagas de hasta 2 × ritmo × RTT.
- **Solución.** En Windows, `at` se expresa en el dominio de `QueryPerformanceCounter`, que es el reloj de `Instant` y también el de `Stopwatch.GetTimestamp()` en .NET. Se calcula como QPC ahora ± la diferencia con `Instant::now()`.
- **Alcance.** No cambian firmas ni structs. macOS e iOS siguen devolviendo 0, como antes. En Linux ya era `CLOCK_MONOTONIC`, el mismo dominio que `Stopwatch`.

## QuicheNet C# (`quichenet-csharp.patch`)

### Wrappers y API nuevas

- **`QuicheConnection`:**
  - `SetBrutalRate`, `ExportKeyingMaterial` (las dos sobrecargas), `Session`, `IsResumed`, `IsEstablished` e `IsInEarlyData`.
  - Sobrecarga `ConnectAsync(..., ReadOnlyMemory<byte> session, ...)`: fija la sesión antes del primer paquete y, si la config tiene early data, vuelve en cuanto se puede enviar 0-RTT.
- **`QuicheConfig`:** `CcAlgorithmName` (por ejemplo `"brutal"`), `MaxConnectionWindow`, `MaxStreamWindow` y un getter para `CcAlgorithm`.
- **`QuicheCcAlgorithm`:** añade `QUICHE_CC_BBR2_GCONGESTION = 4` y `QUICHE_CC_BRUTAL = 5`.
  - `QUICHE_CC_BBR = 2` y `QUICHE_CC_BBR2 = 3` ya no existen en quiche 0.29, y pasarlos llegaba a Rust como un discriminante inválido (UB). Ahora están marcados `[Obsolete]`, se traducen a 4, y cualquier otro valor fuera de rango lanza una excepción.

### Envío con pacing

- **Antes.** Un único buffer y un `Timer` con `at` como plazo. Cada paquete nuevo sobrescribía al anterior si aún no había salido, lo que provocaba pérdidas propias.
- **Ahora.** Los paquetes van a una cola `(datos, at)`, y un hilo emisor los envía cuando `at <= ahora + 2 ms`.
  - Si faltan más de 20 ms duerme; si falta menos, hace spin (`Thread.Sleep` en Windows tarda ~15,6 ms).
- **Esperas.** Los `Task.Delay(75)` pasan a ser esperas por señal: paquete recibido, datos escritos o paquetes generados, con tope en `quiche_conn_timeout_as_nanos`. `on_timeout` solo se llama cuando vence. La recepción usa un `Channel`, y `QuicheListener` escribe en él.

### Fallos que ya existían y se corrigieron

Aparecieron al probar con transferencias reales.

1. **El FIN de un stream nunca se enviaba.** El flag `fin` se calculaba antes de enviar nada (`bytesSent == streamBuf.Length`) y los buffers vacíos se descartaban.
   - Ahora el FIN se envía cuando la aplicación ha cerrado el stream y ya se entregaron todos sus bytes. `IsWriteCompleted` se marca después del último `Flush`, así que el FIN nunca sale antes que los datos.
2. **`QuicheStream.Flush` se llamaba a la vez desde la app y desde el bucle de envío.** `PipeReader` no admite lectores concurrentes, y con `StreamWriter` bloqueaba el `Dispose` (reproducido con la réplica de ExampleApp). Ahora está serializado con un lock.
3. **`QuicheStream.Write` era `async void`.** Las escrituras sin esperar podían solaparse y tumbar el proceso. Ahora hay `WriteAsync(ReadOnlyMemory)` real, y `Write` síncrono espera a que termine.
4. **`QuicheStream.Read` devolvía 0 cuando aún no había datos.** Eso rompe `CopyToAsync`/`ReadToEndAsync`, que lo toman como fin de stream.
   - Ahora espera a que lleguen datos y solo devuelve 0 al final del stream.
   - El código que trataba 0 como "sin datos aún" sigue funcionando.
5. **Orden de los bytes sin enviar.** Tras un envío parcial, lo que quedaba por enviar se ponía *detrás* de lo escrito entretanto, lo que reordenaba datos. Ahora va delante.
6. **Punteros a variables locales de métodos async** (`&sendInfo`, `&errorCode`, `Unsafe.AsPointer`). Esas variables pueden vivir en el heap y moverse. Las llamadas nativas se movieron a métodos síncronos.

### Plataformas y bindings

- **Multi-target.** net7.0 y net8.0 añadidos en `Cloudflare.QuicheNet` y en los NativeAssets de Linux, macOS y Win32, con `LangVersion=latest`. En net7.0/net8.0 se añade el paquete `System.IO.Pipelines` 8.0.0, y en net7.0 `SocketAddress.Buffer` se sustituye por una copia de bytes.
- **x86.** `Architecture.X86` en el resolver de la DLL, RID `win-x86` → `i686-pc-windows-msvc`, y la propiedad `RustTargetDir` para usar una ruta corta, por ejemplo `-p:RustTargetDir=C:\q`.
- **`ssize_t` → `nint`.** En la copia de `quiche.h` de bindgen, `#define ssize_t SSIZE_T` pasa a `intptr_t`. Antes el `.g.cs` dependía de la arquitectura: `long` en x64 y `CLong` (32 bits) en x86. Un proceso x86 con los bindings de x64 leía mal el valor de retorno de `send`, `recv`, `stream_recv`, etc. Ahora es `nint` en las dos y el fichero es idéntico. La ABI de la DLL no cambia, porque Rust ya usa `isize`.
  - **Compatibilidad:** las funciones que devolvían `long` ahora devuelven `nint`. `nint` se convierte implícitamente a `long`, pero conviene revisar el código propio de Socks5Lib que use esos `DllImport` directamente.

## Resultados de las pruebas

### Rust

| Ejecución | Resultado |
|---|---|
| x64, suite completa con los 3 parches de quiche (`cargo test -p quiche --features ffi,qlog --lib`) | **1069 passed, 0 failed** |
| i686 (`--target i686-pc-windows-msvc`), pruebas nuevas + `ffi` + `lookup_cc` | **22 passed, 0 failed** |
| Parte 1 sola, sobre la base (filtros `brutal recovery gcongestion lookup_cc`) | 432 passed |
| Parte 2 sola (`export_keying_material tls ffi`) | 5 passed |
| Parte 3 sola (`ffi`) | 6 passed |

Avisos del compilador: los 7 que salen están en `h3/qpack/decoder.rs` y ya existían.

Pruebas nuevas:

- **Brutal, unitarias** (`recovery::gcongestion::brutal::tests`):
  - `congestion_window`: 1 MB/s × 125 ms × 2 = 250 000; 10240 sin RTT; suelo en MSS; `can_send` con `<=`.
  - `ack_rate_floor`: 10 ACK y 90 pérdidas → 0.8; 910/1000 → 0.91.
  - `ack_rate_min_samples`: 49 muestras → 1.0; a la 50 → 0.8.
  - `ack_rate_window_expiry`: caducidad de la ventana.
  - `rate_changes`: 0 → ritmo → otro ritmo → 0, con reinicio de ranuras y semilla del sRTT.
  - `loss_does_not_reduce_window`: 20 s con 1000 pérdidas por segundo y PTO, sin reducir la ventana.
  - `max_burst`.
- **Brutal, con pipe** (`tests`):
  - `brutal_set_rate`: saludo con "brutal"; Ok; `pacing_rate` = ritmo en cliente y servidor.
  - `brutal_set_rate_other_cc` con reno, cubic y bbr2_gcongestion: `Err(CongestionControl)`.
  - `brutal_transfer`: 1 MB completo con ritmo fijado.
  - `brutal_rate_on_new_paths`: el camino sondeado hereda el ritmo en cliente y servidor.
- **Exportador, con pipe** (`tests::export_keying_material`):
  - antes del saludo → `InvalidState` en los dos lados;
  - tras el saludo, con label de 16 bytes no UTF-8 (0xFF…) y context "password" → mismos 32 bytes en cliente y servidor;
  - otro context u otro label → resultado distinto, pero igual en ambos lados.
- **Tests existentes.** `recovery::tests::lookup_cc_algo_ok` ahora incluye "brutal".
- **Windows** (`ffi::tests`):
  - `time_to_c_windows`: dominio QPC; ±5 ms se reflejan con menos de 1 ms de error.
  - `send_info_at_windows`: `at` de `quiche_conn_send` dentro de [QPC antes − 1 ms, QPC después + 100 ms].

### C#

- **Build.** `Cloudflare.QuicheNet` compila para net7.0, net8.0 y net10.0 con **0 errores y 0 avisos**, usando el SDK 10.0.401. Las TFM de Android, iOS y macOS necesitan workloads y no se compilaron.
- **ExampleApp** (net10.0, DLL x64 nueva, con los certificados de ejemplo de quiche en `trust/`): completa todo el flujo, con streams en ambos sentidos y datagramas en ambos sentidos. La carpeta `trust/` del repo solo trae scripts `gen-cert`, sin certificados.
- **App de humo** (net8.0, loopback, cliente y servidor en el mismo proceso), resultados de la última ejecución:

| Escenario | Resultado |
|---|---|
| Brutal 5 MB/s, 20 MB por un stream | 4,99 MB/s — PASS (las otras ejecuciones: 4,79 / 4,66 / 4,99) |
| Brutal 20 MB/s, 60 MB | 10,34 MB/s — por debajo del umbral de la prueba (ver nota) |
| Brutal con ritmo 0 (BBRv2) | 6,03 MB/s — PASS |
| Exportador (UUID big-endian no UTF-8 + "password", 32 bytes) | cliente == servidor; otro context da otro token — PASS |
| `SetBrutalRate` con cubic | `QUICHE_ERR_CONGESTION_CONTROL` — PASS |
| `QUICHE_CC_BBR2` (obsoleto) | conecta y transfiere 1 MB — PASS |

**Sobre los 20 MB/s.** El techo lo marca QuicheNet en loopback, no Brutal.
- **Techo sin límite:** en las mismas ejecuciones, BBRv2 sin límite dio entre 6,0 y 11,6 MB/s, y Brutal a 20 MB/s dio entre 6,5 y 18,1 MB/s según la ejecución.
- **Ritmos por debajo del techo:** Brutal a 5 MB/s se clava en ~5 MB/s en todas las ejecuciones, así que el pacing cumple el ritmo.
- **Pendiente:** no se optimizó el rendimiento de QuicheNet, que queda fuera de este encargo. El candidato principal es el reensamblado de buffers por stream (`byte[]` concatenados).

## Líneas de dumpbin

`dumpbin /exports`, con `_quiche_conn_set_session` como referencia del patrón.

x64 (`x64/quiche_bindgen.dll`):

```
         56   37 00001370 _quiche_conn_export_keying_material
        100   63 00001630 _quiche_conn_set_brutal_rate
        104   67 00001670 _quiche_conn_set_session
        229   E4 0000AE40 quiche_conn_export_keying_material
        273  110 0000DF80 quiche_conn_set_brutal_rate
        277  114 0000E9B0 quiche_conn_set_session
```

x86 (`x86/quiche_bindgen.dll`):

```
         56   37 00001370 _quiche_conn_export_keying_material
        100   63 00001630 _quiche_conn_set_brutal_rate
        104   67 00001670 _quiche_conn_set_session
        229   E4 00009760 quiche_conn_export_keying_material
        273  110 0000CA60 quiche_conn_set_brutal_rate
        277  114 0000D4A0 quiche_conn_set_session
```

- **Totales:** cada DLL tiene 345 exports, 172 con el prefijo `_quiche_` (170 + 2), y `NativeMethods.g.cs` tiene 172 `DllImport` con `EntryPoint = "_quiche_…"`.
- **Nombres decorados en x86:** en compilaciones anteriores de esta sesión, `dumpbin` mostraba en x86 `_quiche_x = __quiche_x`, el mismo patrón que los exports existentes.

## Cómo se compiló

- **Entorno:** `LIBCLANG_PATH=C:\Program Files\LLVM\bin`; Rust 1.98.1; MSVC 14.16.
- **x64:** `cargo build --release` en `quiche-bindgen`.
- **x86:** `cargo build --release --target i686-pc-windows-msvc --target-dir C:\q`.
- **Orden:** primero x86 y después x64, porque `build.rs` reescribe `NativeMethods.g.cs`, `src/quiche.rs` y `src/quiche_ffi.rs` en cada build. Con el cambio de `ssize_t` los dos `.g.cs` salen idénticos.
- **SDK .NET:** 10.0.401, instalado en el perfil de usuario (`%LOCALAPPDATA%\Microsoft\dotnet`) con `dotnet-install.ps1`, porque en el equipo no había winget. Las apps net8.0 se ejecutaron con el runtime de sistema 8.0.10.
- **Copia de seguridad:** las DLL anteriores están en el scratchpad de la sesión (`dll-backup/`).

## Notas de integración (fuera de alcance, sin código)

- **Hysteria 2, autenticación.** La autenticación HTTP/3 (`POST /auth`, `Hysteria-Auth`, `Hysteria-CC-RX`, estado 233) hay que escribirla a mano sobre streams crudos (SETTINGS y QPACK estático). El `quiche_h3` expuesto choca con el bucle de streams de QuicheNet, porque `h3::poll` consume todos los streams, incluidos los TCP con marco 0x401.
- **Hysteria 2, ritmo.** Tras el 233, llamar a `SetBrutalRate(min(tx del cliente, rx del servidor))`. Con `Hysteria-CC-RX: auto`, dejar el ritmo en 0 (BBRv2). Configuración: `CcAlgorithmName = "brutal"`, sin `MaxPacingRate`.
- **Hysteria 2, ventanas.** Los valores de Hysteria son 8 MB por stream y 20 MB por conexión: `MaxStreamWindow` y `MaxConnectionWindow`, y los `MaxInitial*` correspondientes.
- **Salamander y port hopping.** Necesitan un hook de transporte en QuicheNet, que hoy llama a `Socket.SendTo`/`ReceiveFromAsync` directamente.
- **TUIC v5.** Authenticate va solo tras el saludo 1-RTT (ver arriba). Para el UDP nativo por datagramas hay que respetar `MaxDatagramSize` y fragmentar por encima.
- **Windows UDP.** Conviene desactivar `SIO_UDP_CONNRESET` en los sockets (`IOControl(0x9800000C, [0,0,0,0])`). Si no, un ICMP port unreachable hace fallar el siguiente `ReceiveFromAsync` y se cae el listener. La app de humo lo hace; QuicheNet no.
- **CPU del emisor.** El hilo emisor por conexión hace spin cuando el siguiente paquete sale en menos de 20 ms. Con muchas conexiones simultáneas transmitiendo a la vez, conviene medir el uso de CPU.
- **x86 desde .NET.** No hay runtime x86 instalado, así que la carga de la DLL x86 desde C# no se probó. La DLL sí pasó en i686 las pruebas nuevas de Rust y las de `ffi`.

## Estado del repositorio local

- **Working tree:** tiene aplicados los cuatro parches (el submódulo quiche y QuicheNet).
- **Git:** no se ha hecho ningún commit, push ni PR.
- **`entrega-quiche/`:** está sin seguimiento en git.
