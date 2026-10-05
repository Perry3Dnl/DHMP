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

        if (localAddress.AddressFamily != AddressFamily.InterNetworkV6 ||
            localAddress.IsIPv4MappedToIPv6)
            throw new ArgumentException(
                "DHMP Connector requires a native IPv6 local address.",
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
    /// Maximum network payload presented to the raw IPv6 backend.
    /// 1240 bytes is the IPv6 minimum-MTU payload after the mandatory 40-byte IPv6 header.
    /// </summary>
    public int MaximumPayloadBytes { get; init; } = 1240;

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

        if (MaximumPeers <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumPeers));

        if (SocketBufferBytes < MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(SocketBufferBytes));

        if (HandshakeTimeout <= TimeSpan.Zero ||
            HandshakeTimeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(HandshakeTimeout));

        if (PreSharedKey is null && !AllowUnprotectedPayloads)
            throw new InvalidOperationException(
                "DHMP Connector requires either a pre-shared key or explicit AllowUnprotectedPayloads=true. " +
                "Plaintext DHMP V1 has no protocol-owned end-to-end authentication/integrity check.");

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
