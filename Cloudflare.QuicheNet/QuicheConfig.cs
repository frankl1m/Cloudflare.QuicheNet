using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using Cloudflare.Quiche;
using static Cloudflare.Quiche.NativeMethods;
using static Cloudflare.QuicheNet.QuicheLibrary;

namespace Cloudflare.QuicheNet;

public class QuicheConfig : IDisposable
{
    // quiche_config handle

    internal unsafe quiche_config* NativePtr { get; private set; }

    // quiche_config properties    

    private readonly List<string> applicationProtocols;
    public IReadOnlyList<string> ApplicationProtocols
    {
        get => applicationProtocols;
        set
        {
            List<byte> protoList = new();
            foreach (string proto in value)
            {
                protoList.AddRange([(byte)proto.Length, .. Encoding.UTF8.GetBytes(proto)]);
            }

            unsafe
            {
                fixed (byte* protosPtr = protoList.ToArray())
                {
                    QuicheException.ThrowIfError((QuicheError)NativePtr->
                        SetApplicationProtos(protosPtr, (nuint)protoList.Count),
                        "Failed to set application protocols for this instance.");
                }
            }

            applicationProtocols.Clear();
            applicationProtocols.AddRange(value);
        }
    }

    private ReadOnlyMemory<byte> ticketKey;
    public ReadOnlyMemory<byte> TicketKey
    {
        get => ticketKey;
        set
        {
            byte[] keyBytes = value.ToArray();
            unsafe
            {
                fixed (byte* keyBytesPtr = keyBytes)
                {
                    QuicheException.ThrowIfError((QuicheError)NativePtr->
                        SetTicketKey(keyBytesPtr, (nuint)keyBytes.Length),
                        "Failed to set ticket key contents for this instance.");
                }
            }

            ticketKey = keyBytes;
        }
    }

    private DatagramOptions datagramOptions;
    public DatagramOptions DatagramOptions
    {
        get => datagramOptions;
        set
        {
            datagramOptions = value;
            unsafe
            {
                // quiche_config_enable_dgram(config, enabled, recv_queue_len, send_queue_len):
                // primero la cola de RECEPCIÓN. Iban cruzadas.
                NativePtr->EnableDgram(datagramOptions.Enabled,
                    (size_t)datagramOptions.ReceiveQueueLength,
                    (size_t)datagramOptions.SendQueueLength);
            }
        }
    }

    private long acknowledgementDelayExponent;
    public long AcknowledgementDelayExponent
    {
        get => acknowledgementDelayExponent;
        set
        {
            acknowledgementDelayExponent = value;
            unsafe
            {
                NativePtr->SetAckDelayExponent((ulong)acknowledgementDelayExponent);
            }
        }
    }

    private long activeConnectionIdLimit;
    public long ActiveConnectionIdLimit
    {
        get => activeConnectionIdLimit;
        set
        {
            activeConnectionIdLimit = value;
            unsafe
            {
                NativePtr->SetActiveConnectionIdLimit((ulong)activeConnectionIdLimit);
            }
        }
    }

    private QuicheCcAlgorithm ccAlgorithm;

    /// <summary>
    /// El control de congestión por su valor en quiche. Un valor fuera del enum se rechaza
    /// aquí: la FFI lo pasaría a Rust como discriminante inválido.
    /// </summary>
    public QuicheCcAlgorithm CcAlgorithm
    {
        get => ccAlgorithm;
        set
        {
#pragma warning disable CS0618
            if (value is QuicheCcAlgorithm.QUICHE_CC_BBR or QuicheCcAlgorithm.QUICHE_CC_BBR2)
                value = QuicheCcAlgorithm.QUICHE_CC_BBR2_GCONGESTION;
#pragma warning restore CS0618
            if (!Enum.IsDefined(value))
                // Uno que no es del enum no tiene nombre: ToString() escribía su número, y esto también.
                throw new ArgumentOutOfRangeException(nameof(value), (int)value,
                    "quiche no tiene ese control de congestión.");

            ccAlgorithm = value;
            unsafe
            {
                NativePtr->SetCcAlgorithm((int)ccAlgorithm);
            }
        }
    }

    private string? ccAlgorithmName;

    /// <summary>
    /// El control de congestión por su nombre en quiche: «reno», «cubic»,
    /// «bbr2_gcongestion» o «brutal». Brutal es el de la DLL parcheada, y arranca como
    /// BBRv2 hasta que la conexión fija un ritmo. Lanza <see cref="QuicheException"/> si
    /// quiche no conoce el nombre, como «brutal» con una DLL sin el parche.
    /// </summary>
    public string? CcAlgorithmName
    {
        get => ccAlgorithmName;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);

            byte[] nombre = Encoding.ASCII.GetBytes(value + "\0");
            unsafe
            {
                fixed (byte* nombrePtr = nombre)
                {
                    QuicheException.ThrowIfError((QuicheError)NativePtr->SetCcAlgorithmName(nombrePtr),
                        $"quiche no conoce el control de congestión «{value}».");
                }
            }

            ccAlgorithmName = value;
        }
    }

    private long maxConnectionWindow;
    public long MaxConnectionWindow
    {
        get => maxConnectionWindow;
        set
        {
            maxConnectionWindow = value;
            unsafe
            {
                NativePtr->SetMaxConnectionWindow((ulong)maxConnectionWindow);
            }
        }
    }

    private long maxStreamWindow;
    public long MaxStreamWindow
    {
        get => maxStreamWindow;
        set
        {
            maxStreamWindow = value;
            unsafe
            {
                NativePtr->SetMaxStreamWindow((ulong)maxStreamWindow);
            }
        }
    }

    private int initialCongestionWindowPackets;
    public int InitialCongestionWindowPackets
    {
        get => initialCongestionWindowPackets;
        set
        {
            initialCongestionWindowPackets = value;
            unsafe
            {
                NativePtr->SetInitialCongestionWindowPackets((nuint)initialCongestionWindowPackets);
            }
        }
    }

    private bool isActiveMigrationDisabled;
    public bool IsActiveMigrationDisabled
    {
        get => isActiveMigrationDisabled;
        set
        {
            isActiveMigrationDisabled = value;
            unsafe
            {
                NativePtr->SetDisableActiveMigration(isActiveMigrationDisabled);
            }
        }
    }

    private bool isHyStartEnabled;
    public bool IsHyStartEnabled
    {
        get => isHyStartEnabled;
        set
        {
            isHyStartEnabled = value;
            unsafe
            {
                NativePtr->EnableHystart(isHyStartEnabled);
            }
        }
    }

    private bool isPacingEnabled;
    public bool IsPacingEnabled
    {
        get => isPacingEnabled;
        set
        {
            isPacingEnabled = value;
            unsafe
            {
                NativePtr->EnablePacing(isPacingEnabled);
            }
        }
    }

    public bool IsEarlyDataEnabled { get; }

    private long maxAcknowledgmentDelay;
    public long MaxAcknowledgementDelay
    {
        get => maxAcknowledgmentDelay;
        set
        {
            maxAcknowledgmentDelay = value;
            unsafe
            {
                NativePtr->SetMaxAckDelay((ulong)maxAcknowledgmentDelay);
            }
        }
    }

    private int maxAmplificationFactor;
    public int MaxAmplificationFactor
    {
        get => maxAmplificationFactor;
        set
        {
            maxAmplificationFactor = value;
            unsafe
            {
                NativePtr->SetMaxAmplificationFactor((nuint)maxAmplificationFactor);
            }
        }
    }

    private long maxIdleTimeout;
    public long MaxIdleTimeout
    {
        get => maxIdleTimeout;
        set
        {
            maxIdleTimeout = value;
            unsafe
            {
                NativePtr->SetMaxIdleTimeout((ulong)maxIdleTimeout);
            }
        }
    }

    private long maxInitialBidiStreams;
    public long MaxInitialBidiStreams
    {
        get => maxInitialBidiStreams;
        set
        {
            maxInitialBidiStreams = value;
            unsafe
            {
                NativePtr->SetInitialMaxStreamsBidi((ulong)maxInitialBidiStreams);
            }
        }
    }

    private long maxInitialDataSize;
    public long MaxInitialDataSize
    {
        get => maxInitialDataSize;
        set
        {
            maxInitialDataSize = value;
            unsafe
            {
                NativePtr->SetInitialMaxData((ulong)maxInitialDataSize);
            }
        }
    }

    private long maxInitialLocalBidiStreamDataSize;
    public long MaxInitialLocalBidiStreamDataSize
    {
        get => maxInitialLocalBidiStreamDataSize;
        set
        {
            maxInitialLocalBidiStreamDataSize = value;
            unsafe
            {
                NativePtr->SetInitialMaxStreamDataBidiLocal((ulong)maxInitialLocalBidiStreamDataSize);
            }
        }
    }

    private long maxInitialRemoteBidiStreamDataSize;
    public long MaxInitialRemoteBidiStreamDataSize
    {
        get => maxInitialRemoteBidiStreamDataSize;
        set
        {
            maxInitialRemoteBidiStreamDataSize = value;
            unsafe
            {
                NativePtr->SetInitialMaxStreamDataBidiRemote((ulong)maxInitialRemoteBidiStreamDataSize);
            }
        }
    }

    private long maxInitialUniStreamDataSize;
    public long MaxInitialUniStreamDataSize
    {
        get => maxInitialUniStreamDataSize;
        set
        {
            maxInitialUniStreamDataSize = value;
            unsafe
            {
                NativePtr->SetInitialMaxStreamDataUni((ulong)maxInitialUniStreamDataSize);
            }
        }
    }

    private long maxInitialUniStreams;
    public long MaxInitialUniStreams
    {
        get => maxInitialUniStreams;
        set
        {
            maxInitialUniStreams = value;
            unsafe
            {
                NativePtr->SetInitialMaxStreamsUni((ulong)maxInitialUniStreams);
            }
        }
    }

    private long maxPacingRate;
    public long MaxPacingRate
    {
        get => maxPacingRate;
        set
        {
            maxPacingRate = value;
            unsafe
            {
                NativePtr->SetMaxPacingRate((ulong)maxPacingRate);
            }
        }
    }

    private int maxReceiveUdpPayloadSize;
    public int MaxReceiveUdpPayloadSize
    {
        get => maxReceiveUdpPayloadSize;
        set
        {
            maxReceiveUdpPayloadSize = value;
            unsafe
            {
                NativePtr->SetMaxRecvUdpPayloadSize((nuint)maxReceiveUdpPayloadSize);
            }
        }
    }

    private int maxSendUdpPayloadSize;
    public int MaxSendUdpPayloadSize
    {
        get => maxSendUdpPayloadSize;
        set
        {
            maxSendUdpPayloadSize = value;
            unsafe
            {
                NativePtr->SetMaxSendUdpPayloadSize((nuint)maxSendUdpPayloadSize);
            }
        }
    }

    private bool shouldDiscoverPathMtu;
    public bool ShouldDiscoverPathMtu
    {
        get => shouldDiscoverPathMtu;
        set
        {
            shouldDiscoverPathMtu = value;
            unsafe
            {
                NativePtr->DiscoverPmtu(shouldDiscoverPathMtu);
            }
        }
    }

    private bool shouldSendGrease;
    public bool ShouldSendGrease
    {
        get => shouldSendGrease;
        set
        {
            shouldSendGrease = value;
            unsafe
            {
                NativePtr->Grease(shouldSendGrease);
            }
        }
    }

    private bool shouldVerifyPeer;
    public bool ShouldVerifyPeer
    {
        get => shouldVerifyPeer;
        set
        {
            shouldVerifyPeer = value;
            unsafe
            {
                NativePtr->VerifyPeer(shouldVerifyPeer);
            }
        }
    }

    /// <summary>
    /// El ofuscador de paquetes de las conexiones de esta configuración: cada datagrama
    /// entero pasa por él al salir (<c>Codificar</c>) y al entrar (<c>Decodificar</c>).
    /// <c>null</c> = paquetes en claro, el comportamiento de siempre. Es una sola
    /// instancia para todas las conexiones, así que no puede guardar estado. Lo usa el
    /// salamander de Hysteria 2.
    /// </summary>
    public IPaqueteOfuscador? Ofuscador { get; set; }

    /// <summary>
    /// Cadencia del PING de aplicación (<c>quiche_conn_send_ack_eliciting</c>):
    /// mantiene viva la conexión —y caliente la ruta de vuelta del NAT— sin abrir
    /// flujos ni consumir el límite del par. <c>Zero</c> = apagado, el comportamiento
    /// de siempre. La cadencia tiene que ir por debajo del idle timeout.
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; }

    /// <summary>
    /// El PING de aplicación con la cadencia derivada de la inactividad: tras el saludo, un
    /// tercio de la menor entre la propia (<see cref="MaxIdleTimeout"/>) y la que anuncia el
    /// par, con un mínimo de 1 s. Así ningún lado cierra por inactividad una conexión viva,
    /// tenga el otro el plazo que tenga. Manda sobre <see cref="KeepAliveInterval"/>.
    /// </summary>
    public bool KeepAliveAuto { get; set; }

    /// <summary>
    /// Si el nativo tiene <c>quiche_config_set_initial_rtt</c>: 0 sin probar, 1 sí, -1 no. El de un móvil que aún no se
    /// recompiló no la tiene; se prueba una vez por proceso, y desde el primer fallo ni se intenta.
    /// </summary>
    private static int _conRttInicial;

    /// <summary>
    /// Fija el RTT inicial de las conexiones que nazcan desde ahora con esta configuración, en ms: quiche lo copia al crear
    /// cada una, y sin él supone 333 ms, que dan ~1 s de PTO al primer vuelo. Una configuración que comparten varias
    /// conexiones se toca bajo el cerrojo con el que se crean: el <c>lock (config)</c> de <c>QuicheConnection.ConnectAsync</c>.
    /// <para>
    /// Aún no lo usa ninguna salida. Medido (28-09-2026): empezar con el RTT ya aprendido hacia el destino no mejora abrir en
    /// frío, porque el primer datagrama del saludo ya sale dos veces (<c>QuicheConnection.RepetirPrimeroDelSaludo</c>) y lo
    /// que queda en la cola llega después del saludo, con el RTT ya medido. Ver nativo/quiche/INFORME.md.
    /// </para>
    /// </summary>
    /// <returns>Falso, sin error, si el nativo no la tiene: las conexiones siguen con el de serie.</returns>
    internal bool FijarRttInicial(long ms)
    {
        if (Volatile.Read(ref _conRttInicial) < 0) return false;
        try
        {
            unsafe
            {
                NativePtr->SetInitialRtt((ulong)Math.Max(ms, 1));
            }
            Volatile.Write(ref _conRttInicial, 1);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            Volatile.Write(ref _conRttInicial, -1);
            return false;
        }
    }

    public bool ShouldLogKeys { get; }

    public QuicheConfig(
        bool isEarlyDataEnabled = false,
        bool shouldLogKeys = false
        )
    {
        unsafe
        {
            NativePtr = quiche_config_new(PROTOCOL_VERSION);

            if (IsEarlyDataEnabled = isEarlyDataEnabled)
            {
                NativePtr->EnableEarlyData();
            }

            if (ShouldLogKeys = shouldLogKeys)
            {
                NativePtr->LogKeys();
            }

            // Lo que se anuncia que se recibe (max_udp_payload_size) es lo que se lee: un datagrama UDP entero. quiche ya
            // anunciaba 65 527, pero se leía con 6115, y lo de un par que se lo creyera llegaba cortado. Ver
            // QuicheLibrary.MaxDatagramaUdp.
            maxReceiveUdpPayloadSize = MaxDatagramaUdp;
            NativePtr->SetMaxRecvUdpPayloadSize((nuint)MaxDatagramaUdp);
        }

        applicationProtocols = new();
        ticketKey = ReadOnlyMemory<byte>.Empty;
    }

    public void LoadCertificateChainFromPemFile(string filePath)
    {
        unsafe
        {
            fixed (byte* filePathPtr = Encoding.UTF8.GetBytes([.. filePath, '\0']))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadCertChainFromPemFile(filePathPtr),
                    "Failed to load certificate chain from provided PEM file!"
                    );
            }
        }
    }

    public void LoadPrivateKeyFromPemFile(string filePath)
    {
        unsafe
        {
            fixed (byte* filePathPtr = Encoding.UTF8.GetBytes([.. filePath, '\0']))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadPrivKeyFromPemFile(filePathPtr),
                    "Failed to load private key from provided PEM file!"
                    );
            }
        }
    }

    public void LoadVerifyLocationsFromDirectory(string path)
    {
        unsafe
        {
            fixed (byte* pathPtr = Encoding.UTF8.GetBytes([.. path, '\0']))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadVerifyLocationsFromDirectory(pathPtr),
                    "Failed to load trusted CA locations from provided directory!"
                    );
            }
        }
    }

    public void LoadVerifyLocationsFromFile(string filePath)
    {
        unsafe
        {
            fixed (byte* filePathPtr = Encoding.UTF8.GetBytes([.. filePath, '\0']))
            {
                QuicheException.ThrowIfError(
                    (QuicheError)NativePtr->LoadVerifyLocationsFromFile(filePathPtr),
                    "Failed to load trusted CA locations from provided file!"
                    );
            }
        }
    }

    #region IDisposable

    private bool disposedValue;

    protected virtual void Dispose(bool disposing)
    {
        unsafe
        {
            if (!disposedValue)
            {
                NativePtr->Free();
                NativePtr = null;

                disposedValue = true;
            }
        }
    }

    ~QuicheConfig()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion
}
