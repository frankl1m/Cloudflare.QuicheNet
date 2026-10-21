# Quiche.NET

[![NuGet](https://github.com/IsaMorphic/Cloudflare.QuicheNet/actions/workflows/nuget.yml/badge.svg)](https://github.com/IsaMorphic/Cloudflare.QuicheNet/actions/workflows/nuget.yml) [![NuGet version](https://img.shields.io/nuget/v/Cloudflare.QuicheNet?label=NuGet%20version)](https://www.nuget.org/packages/Cloudflare.QuicheNet)

A delicious C# wrapper for [Cloudflare's quiche](https://github.com/cloudflare/quiche) library. 

## Fork updates and Apple support

This fork carries the QUIC wrapper changes used in Socks5Lib and builds for macOS
universal (Intel and Apple Silicon), iOS/iPadOS devices and simulators, Mac Catalyst, and tvOS.
See [Apple build and integration instructions](scripts/apple/README.md).
Native Apple assets must be built locally before packaging; this source change
does not publish new NuGet packages.

The default .NET 10 library profile declares iOS/tvOS 12.2 and Catalyst 13.1 availability; a consuming
.NET application must also meet its runtime/workload minimum (Catalyst 15.0 for
.NET 10). On iOS/tvOS 12.2, the native engine supports normal 1-RTT QUIC, but server
0-RTT admission requires .NET's AES-GCM support (iOS/tvOS 13+). If unavailable,
the anti-replay gate discards early data while preserving the Initial, Handshake
and 1-RTT packets; it never accepts unverifiable early data.


# How to use 

The wrapper exposes a few key classes that mirror the functionality of .NET's built-in QUIC implementation. Importantly, however, the wrapper is completely cross-platform and includes all native dependencies as part of the [NuGet package](https://www.nuget.org/packages/Cloudflare.QuicheNet). Simply install the package into your project and go!

## Server-side (`QuicheListener`)

```csharp
// Initialize UDP socket (listening on port 8080)
Socket socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
socket.Bind(new IPEndPoint(IPAddress.Any, 8080));

// Configure listener
QuicheConfig config = new QuicheConfig 
{
    // Refer to official quiche documentation for more options;
    // certain options default to broken behavior if left unspecified.
    MaxInitialBidiStreams = 16,
    MaxInitialUniStreams = 16,
};

// Start listening for inbound QUIC connections
QuicheListener listener = new QuicheListener(socket, config);
_ = Task.Run(listener.ListenAsync);

// Wait for client to connect
QuicheConnection client = await listener.AcceptAsync();

// Accept inbound client stream
QuicheStream stream = await client.AcceptInboundStreamAsync();

// OR create outbound server stream
QuicheStream stream = await client.CreateOutboundStreamAsync(/* specify unidirectional or bidirectional */);
```

## Client-side (`QuicheConnection`)

```csharp
// Initialize UDP socket (random port)
Socket socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
socket.Bind(new IPEndPoint(IPAddress.Any, 0));

// Configure client
QuicheConfig config = new QuicheConfig 
{
    // Refer to official quiche documentation for more options;
    // certain options default to broken behavior if left unspecified.
    MaxInitialBidiStreams = 16,
    MaxInitialUniStreams = 16,
};

// Initiate QUIC connection with server
QuicheConnection client = QuicheConnection.ConnectAsync(socket, /* server endpoint here */, config);

// Accept inbound server stream
QuicheStream stream = await client.AcceptInboundStreamAsync();

// OR create outbound client stream
QuicheStream stream = await client.CreateOutboundStreamAsync(/* specify unidirectional or bidirectional */);
```

## Cleaning up after Quiche

Since Quiche is a native library, it manages its own memory that must be cleaned up after explicitly. Quiche.NET facilitates this via the built-in .NET `IDisposable` interface which exists for this purpose. All objects that are initialized by Quiche.NET inherit from this interface and must be cleaned up to avoid memory leakage in your app. 

There is also a hierarchy of objects that must be disposed in a certain order to ensure that resources are not leaked. When creating a new `QuicheConnection`, disposing of it will always shutdown all constituent `QuicheStream`s immediately and call their `Dispose` method. Additionally, disposing of a `QuicheListener` object will shutdown all of its constituent `QuicheConnection`s, disconnecting all clients immediately. Finally, a given `QuicheConfig` should always be disposed of, but not before instantiating a `QuicheListener` or `QuicheConnection`.

## Working example (`ExampleApp`)

To compile and debug a working example application with minimal logic, see the `ExampleApp` project. This program starts a client and server within the same process on separate sockets to demonstrate a basic "Hello world" scenario. Full TLS authentication is also properly demonstrated. To run the example application, you must generate a self-signed SSL certificate. Use the following command to do so in the `trust` subdirectory:

```bash
openssl req -x509 -nodes -days 365 -newkey rsa:2048 -keyout key.pem -out cert.pem -addext "subjectAltName = DNS:localhost, IP:127.0.0.1"
```

Once the certificate is generated, the example application can be run for testing (e.g. `dotnet run`, IDE / debugger)

## Extras & Goodies

Quiche also supports sending / receiving `DATAGRAM` frames from any `Connection` instance. This feature of the QUIC protocol can be used to leverage the existing semantics of a UDP session and asynchronously send smaller messages to the other side which are unordered, but encrypted and reliable. This feature is supported by Quiche.NET and can be used with any `QuicheConnection` instance as follows:

```csharp
// Before connection init
QuicheConfig config = new QuicheConfig 
{ 
  /* ... */ 
  DatagramOptions = new DatagramOptions 
  {
    Enabled = true,
    SendQueueLength = 64,
    ReceiveQueueLength = 64,
  };
};

// Once connected
QuicheConnection conn;

// Call will wait asynchronously for next DATAGRAM if none are available in the queue
byte[] data = await conn.ReceiveDatagramAsync(); 

// Call will return false if no DATAGRAMs are available yet
bool result = conn.TryReceiveDatagram(out byte[]? data);

// Call will wait asynchronously until queue is no longer full
byte[] data = /* ... */;
await conn.SendDatagramAsync(data);

// Call will return false if send queue is full
bool result = conn.TrySendDatagram(data);
```

All queued `DATAGRAM`s are guaranteed to be delivered, but will not be drained reliably by Quiche before a connection closes; users handle this behavior themselves. This feature is best suited to delivering smaller messages alongside long-running streams. These messages can be used, for example, to exchange SIP messages between two parties in a voice / video call, all within the same connection and without dealing with rate control limitations that streams have to deal with. 

# How it works

Quiche is an abstract implementation of the QUIC protocol. Although QUIC is intended to operate above UDP in the network protocol stack, quiche's implementation is uniquely cross-platform due to existing at a higher layer of abstraction- that is to say, quiche doesn't touch native sockets to do its work. Instead, quiche accepts packet data from the caller, allowing any socket abstraction to exist below it. This allows Quiche.NET to use .NET's `System.Net.Socket` API to transmit raw packet data and schedule packets for sending, while quiche itself unwraps & generates those packets for us whenever they enter or leave the system. 

### Apple en iOS y tvOS 12.0

El perfil opcional `-p:EnableAppleLegacyTargets=true` selecciona `net8.0-ios`,
`net8.0-maccatalyst` y `net8.0-tvos` tanto en el wrapper como en sus assets Apple.
Se compila con un SDK .NET 8 y workloads Apple .NET 8 históricos, por separado
respecto al perfil .NET 10. Sus mínimos de biblioteca son iOS/iPadOS 12.0,
tvOS 12.0 y Mac Catalyst 13.1. Los simuladores ARM64 requieren el mínimo nativo
14.0; para simular versiones anteriores se necesita Intel. En este perfil, el guard de AES-GCM deshabilita 0-RTT del servidor en todas
las versiones de iOS/tvOS/Catalyst, conservando el handshake normal 1-RTT.

Ejemplo de compilación de biblioteca sin regenerar nativos:

```sh
dotnet build Cloudflare.QuicheNet/Cloudflare.QuicheNet.csproj \
  -p:EnableAppleLegacyTargets=true -p:BuildNativeAssets=false
```

El perfil separado `-p:EnableAppleLegacy7Targets=true` selecciona los tres TFM
Apple de .NET 7 para iOS/tvOS 10.0 y Catalyst 13.1. Requiere los packs Apple
.NET 7 históricos; puede compilarse con un SDK .NET 8 que los incluya. En los perfiles
.NET 7 y .NET 8, el wrapper no habilita AES-GCM administrado en iOS/tvOS/Catalyst: la protección
anti-repetición rechaza 0-RTT y permite continuar el handshake normal 1-RTT.
Estos perfiles antiguos requieren un Xcode compatible para publicar aplicaciones;
compilar una biblioteca no valida el enlace AOT ni su ejecución en un dispositivo
antiguo. El soporte de 32 bits requiere además nativos y ABI verificados por separado.

Los runtimes Mono históricos 7.0.20 y 8.0.7 incluidos en estos workloads tienen
un mínimo real de Catalyst 13.5 en Intel y 14.2 en ARM64, aunque la biblioteca
managed declare 13.1. El mínimo de una aplicación es el mayor entre el declarado
por la aplicación, su runtime .NET y la slice nativa seleccionada.
