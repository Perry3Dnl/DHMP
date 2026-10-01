using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// One direct IPv6 peer/path binding for the current single-peer backend.
/// Data and control use separate experimental IPv6 protocol numbers.
/// </summary>
public sealed class DhmpRawIpv6Options
{
    public DhmpRawIpv6Options(
        IPAddress localAddress,
        IPAddress remoteAddress,
        int maximumPayloadBytes,
        int socketBufferBytes = 4 * 1024 * 1024,
        TimeSpan? handshakeTimeout = null,
        bool enableExperimentalProtocolNumbers = false,
        bool allowWildcardLocalAddress = false,
        bool allowUnprotectedPayloads = false)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (localAddress.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException(
                "Local address must be IPv6.",
                nameof(localAddress));

        if (remoteAddress.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException(
                "Remote address must be IPv6.",
                nameof(remoteAddress));

        if (localAddress.IsIPv4MappedToIPv6 ||
            remoteAddress.IsIPv4MappedToIPv6)
            throw new ArgumentException(
                "DHMP raw IPv6 requires native IPv6 addresses.");

        if (remoteAddress.Equals(IPAddress.IPv6Any))
            throw new ArgumentException(
                "A single-peer DHMP binding requires an explicit remote IPv6 address.",
                nameof(remoteAddress));

        if (localAddress.Equals(IPAddress.IPv6Any) &&
            !allowWildcardLocalAddress)
            throw new ArgumentException(
                "Wildcard local IPv6 binding is disabled by default because DHMP has no port field. " +
                "Bind an explicit service IPv6 address or set allowWildcardLocalAddress: true when one process intentionally owns all DHMP traffic on this protocol binding.",
                nameof(localAddress));

        if (maximumPayloadBytes <= 0 ||
            maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        if (socketBufferBytes < maximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(socketBufferBytes));

        TimeSpan timeout = handshakeTimeout ?? TimeSpan.FromSeconds(10);
        if (timeout <= TimeSpan.Zero ||
            timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout),
                "A positive, finite handshake timeout supported by .NET timers is required.");

        LocalAddress = localAddress;
        RemoteAddress = remoteAddress;
        MaximumPayloadBytes = maximumPayloadBytes;
        SocketBufferBytes = socketBufferBytes;
        HandshakeTimeout = timeout;
        ExperimentalProtocolNumbersEnabled = enableExperimentalProtocolNumbers;
        WildcardLocalAddressAllowed = allowWildcardLocalAddress;
        UnprotectedPayloadsAllowed = allowUnprotectedPayloads;
    }

    public static DhmpRawIpv6Options FromPathMtu(
        IPAddress localAddress,
        IPAddress remoteAddress,
        int pathMtu,
        int socketBufferBytes = 4 * 1024 * 1024,
        int additionalIpv6HeaderBytes = 0,
        TimeSpan? handshakeTimeout = null,
        bool enableExperimentalProtocolNumbers = false,
        bool allowWildcardLocalAddress = false,
        bool allowUnprotectedPayloads = false)
    {
        var budget =
            new DhmpIpv6PathBudget(
                pathMtu,
                additionalIpv6HeaderBytes);

        return new DhmpRawIpv6Options(
            localAddress,
            remoteAddress,
            budget.MaximumProtocolPayloadBytes,
            socketBufferBytes,
            handshakeTimeout,
            enableExperimentalProtocolNumbers,
            allowWildcardLocalAddress,
            allowUnprotectedPayloads);
    }

    /// <summary>
    /// Conservative option for an IPv6 path whose PMTU has not been established.
    /// Uses the IPv6 minimum MTU (1280 bytes); callers can move to a larger verified
    /// path budget later without changing DHMP V1 record framing.
    /// </summary>
    public static DhmpRawIpv6Options ForUnknownPath(
        IPAddress localAddress,
        IPAddress remoteAddress,
        int socketBufferBytes = 4 * 1024 * 1024,
        int additionalIpv6HeaderBytes = 0,
        TimeSpan? handshakeTimeout = null,
        bool enableExperimentalProtocolNumbers = false,
        bool allowWildcardLocalAddress = false,
        bool allowUnprotectedPayloads = false)
        => FromPathMtu(
            localAddress,
            remoteAddress,
            DhmpIpv6PathBudget.MinimumIpv6Mtu,
            socketBufferBytes,
            additionalIpv6HeaderBytes,
            handshakeTimeout,
            enableExperimentalProtocolNumbers,
            allowWildcardLocalAddress,
            allowUnprotectedPayloads);

    public IPAddress LocalAddress { get; }
    public IPAddress RemoteAddress { get; }
    public int MaximumPayloadBytes { get; }
    public int SocketBufferBytes { get; }
    /// <summary>Total deadline for one compatibility or PSK handshake, including send and receive.</summary>
    public TimeSpan HandshakeTimeout { get; }

    /// <summary>
    /// True only when the caller explicitly opted into the RFC 4727/IANA experimental
    /// IPv6 Next Header values 253/254. Production Internet reachability is not implied.
    /// </summary>
    public bool ExperimentalProtocolNumbersEnabled { get; }

    /// <summary>
    /// True only when this application intentionally owns wildcard raw-DHMP delivery
    /// for the selected protocol binding. DHMP V1 has no transport port field.
    /// </summary>
    public bool WildcardLocalAddressAllowed { get; }

    /// <summary>
    /// True only when the caller explicitly accepts that plaintext DHMP V1 provides
    /// no protocol-owned end-to-end integrity/authentication check.
    /// </summary>
    public bool UnprotectedPayloadsAllowed { get; }

    public byte DataProtocolNumber =>
        DhmpProtocol.ExperimentalIpv6DataNextHeader;

    public byte ControlProtocolNumber =>
        DhmpProtocol.ExperimentalIpv6ControlNextHeader;

    internal void EnsureExperimentalProtocolNumbersEnabled()
    {
        if (!ExperimentalProtocolNumbersEnabled)
            throw new InvalidOperationException(
                "DHMP raw IPv6 currently uses experimental Next Header values 253/254. " +
                "Set enableExperimentalProtocolNumbers: true only for an explicitly configured experiment.");
    }
}
