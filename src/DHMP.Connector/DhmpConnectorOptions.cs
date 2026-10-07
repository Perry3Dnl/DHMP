using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;
using DHMP.Security;

namespace DHMP.Connector;

/// <summary>
/// Application-facing configuration for one DHMP networking runtime.
/// The connector owns local networking; each remote address becomes a DhmpConnection.
/// </summary>
public sealed class DhmpConnectorOptions
{
    public DhmpConnectorOptions(
        IPAddress localAddress,
        int recordSize,
        Guid schemaId)
    {
        ArgumentNullException.ThrowIfNull(localAddress);

        if (localAddress.AddressFamily is not AddressFamily.InterNetwork and
            not AddressFamily.InterNetworkV6 ||
            localAddress.IsIPv4MappedToIPv6)
            throw new ArgumentException(
                "DHMP Connector requires an IPv4 or native IPv6 local address.",
                nameof(localAddress));

        if (recordSize <= 0 || recordSize > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(recordSize));

        if (schemaId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(schemaId));

        LocalAddress = localAddress;
        RecordSize = recordSize;
        SchemaId = schemaId;
    }

    public IPAddress LocalAddress { get; }
    public int RecordSize { get; }
    public Guid SchemaId { get; }

    public DhmpProcessingMode ReceiveMode { get; init; } =
        DhmpProcessingMode.Sequential;

    public DhmpRatePolicy RatePolicy { get; init; } =
        DhmpRatePolicy.SmoothPacing;

    public int MaximumMessagesPerSecond { get; init; } = 100_000;

    /// <summary>
    /// Maximum DHMP network payload presented to the native Raw IPv6 backend.
    /// 1240 bytes is the IPv6 minimum-MTU payload after the mandatory 40-byte IPv6 header.
    /// </summary>
    public int MaximumPayloadBytes { get; init; } = 1240;

    /// <summary>
    /// Maximum DHMP payload carried inside one UDP datagram.
    /// The 1232-byte default preserves the IPv6 minimum-MTU budget after IPv6 and UDP headers.
    /// </summary>
    public int UdpMaximumPayloadBytes { get; init; } = 1232;

    /// <summary>UDP data port used by the compatibility backend.</summary>
    public int UdpDataPort { get; init; } = 47530;

    /// <summary>Separate UDP control port used for compatibility/security setup.</summary>
    public int UdpControlPort { get; init; } = 47531;

    /// <summary>
    /// Maximum time Auto spends proving the preferred native path before trying UDP.
    /// The full handshake timeout still applies after a transport has been selected.
    /// </summary>
    public TimeSpan TransportAttemptTimeout { get; init; } =
        TimeSpan.FromMilliseconds(1500);

    public DhmpTransportPreference TransportPreference { get; init; } =
        DhmpTransportPreference.Auto;

    public int MaximumPeers { get; init; } = 1024;
    public int SocketBufferBytes { get; init; } = 4 * 1024 * 1024;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Optional authenticated PSK profile. When omitted, plaintext receive must be explicitly enabled.
    /// Ownership remains with the caller; the connector does not dispose this key.
    /// </summary>
    public DhmpPreSharedKey? PreSharedKey { get; init; }

    public bool EnableExperimentalProtocolNumbers { get; init; }
    public bool AllowWildcardLocalAddress { get; init; }
    public bool AllowUnprotectedPayloads { get; init; }

    /// <summary>
    /// Explicit opt-in for plaintext one-packet BlindFire telemetry.
    /// Source IPv6 and schema/record validation are not authentication.
    /// </summary>
    public bool AllowUnprotectedBlindFire { get; init; }

    public DhmpDuplicatePeerHandling DuplicatePeerHandling { get; init; } =
        DhmpDuplicatePeerHandling.Reject;

    /// <summary>
    /// Application-owned 64-bit field used only when duplicate-source routing is enabled.
    /// The field definition participates in Connector schema negotiation.
    /// </summary>
    public DhmpConnectionIdField? ConnectionIdField { get; init; }

    internal void Validate()
    {
        var wire = new DhmpWireContract(RecordSize);

        if (ReceiveMode is not DhmpProcessingMode.Sequential and
            not DhmpProcessingMode.Latest)
            throw new ArgumentOutOfRangeException(nameof(ReceiveMode));

        if (RatePolicy is not DhmpRatePolicy.RejectWindow and
            not DhmpRatePolicy.SmoothPacing)
            throw new ArgumentOutOfRangeException(nameof(RatePolicy));

        if (MaximumMessagesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumMessagesPerSecond));

        if (MaximumPayloadBytes < wire.RecordSize ||
            MaximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(MaximumPayloadBytes));

        if (UdpMaximumPayloadBytes <= 0 ||
            UdpMaximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(UdpMaximumPayloadBytes));

        if (TransportPreference != DhmpTransportPreference.RawIpv6Only &&
            UdpMaximumPayloadBytes < wire.RecordSize)
            throw new ArgumentOutOfRangeException(
                nameof(UdpMaximumPayloadBytes),
                "The UDP compatibility path must fit one complete DHMP record.");

        if (UdpDataPort is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(UdpDataPort));

        if (UdpControlPort is <= 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(UdpControlPort));

        if (UdpDataPort == UdpControlPort)
            throw new ArgumentException(
                "DHMP UDP data and control ports must be different.");

        if (!Enum.IsDefined(TransportPreference))
            throw new ArgumentOutOfRangeException(nameof(TransportPreference));

        if (TransportPreference == DhmpTransportPreference.RawIpv6Only &&
            (LocalAddress.AddressFamily != AddressFamily.InterNetworkV6 ||
             LocalAddress.IsIPv4MappedToIPv6))
            throw new InvalidOperationException(
                "RawIpv6Only requires a native IPv6 local address.");

        if (MaximumPeers <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumPeers));

        if (SocketBufferBytes <
            Math.Max(MaximumPayloadBytes, UdpMaximumPayloadBytes))
            throw new ArgumentOutOfRangeException(nameof(SocketBufferBytes));

        if (HandshakeTimeout <= TimeSpan.Zero ||
            HandshakeTimeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout));

        if (TransportAttemptTimeout <= TimeSpan.Zero ||
            TransportAttemptTimeout > HandshakeTimeout)
            throw new ArgumentOutOfRangeException(
                nameof(TransportAttemptTimeout),
                "Transport attempt timeout must be positive and no longer than the full handshake timeout.");

        if (PreSharedKey is null &&
            !AllowUnprotectedPayloads &&
            !AllowUnprotectedBlindFire)
            throw new InvalidOperationException(
                "DHMP Connector requires a pre-shared key, explicit AllowUnprotectedPayloads=true for normal plaintext connections, " +
                "or explicit AllowUnprotectedBlindFire=true for BlindFire-only use.");

        if (!Enum.IsDefined(DuplicatePeerHandling))
            throw new ArgumentOutOfRangeException(nameof(DuplicatePeerHandling));

        if (DuplicatePeerHandling ==
            DhmpDuplicatePeerHandling.ResolveWithConnectionId)
        {
            if (PreSharedKey is null)
                throw new InvalidOperationException(
                    "ResolveWithConnectionId requires the authenticated PSK profile so both peers derive the same per-session ConnectionId.");

            if (ConnectionIdField is null)
                throw new InvalidOperationException(
                    "ResolveWithConnectionId requires a ConnectionIdField inside the application record.");

            ConnectionIdField.Value.Validate(RecordSize);
        }
        else if (ConnectionIdField is not null)
        {
            throw new InvalidOperationException(
                "ConnectionIdField is only valid when DuplicatePeerHandling is ResolveWithConnectionId.");
        }
    }
}
