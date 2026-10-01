using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// Local raw-IPv6 listener settings for bounded multi-peer routing.
/// </summary>
public sealed class DhmpRawIpv6ListenerOptions
{
    public DhmpRawIpv6ListenerOptions(
        IPAddress localAddress,
        int maximumPayloadBytes,
        int maximumPeers = 1024,
        int socketBufferBytes = 4 * 1024 * 1024,
        bool enableExperimentalProtocolNumbers = false,
        bool allowWildcardLocalAddress = false)
    {
        ArgumentNullException.ThrowIfNull(localAddress);

        if (localAddress.AddressFamily != AddressFamily.InterNetworkV6 ||
            localAddress.IsIPv4MappedToIPv6)
            throw new ArgumentException(
                "DHMP raw IPv6 listener requires a native IPv6 local address.",
                nameof(localAddress));

        if (localAddress.Equals(IPAddress.IPv6Any) &&
            !allowWildcardLocalAddress)
            throw new ArgumentException(
                "Wildcard local IPv6 binding is disabled by default because DHMP has no port field. " +
                "Bind an explicit service IPv6 address or set allowWildcardLocalAddress: true when one process intentionally owns all DHMP traffic on this protocol binding.",
                nameof(localAddress));

        if (maximumPayloadBytes <= 0 ||
            maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(maximumPayloadBytes));

        if (maximumPeers <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maximumPeers));

        if (socketBufferBytes < maximumPayloadBytes)
            throw new ArgumentOutOfRangeException(
                nameof(socketBufferBytes));

        LocalAddress = localAddress;
        MaximumPayloadBytes = maximumPayloadBytes;
        MaximumPeers = maximumPeers;
        SocketBufferBytes = socketBufferBytes;
        ExperimentalProtocolNumbersEnabled = enableExperimentalProtocolNumbers;
        WildcardLocalAddressAllowed = allowWildcardLocalAddress;
    }

    public static DhmpRawIpv6ListenerOptions FromPathMtu(
        IPAddress localAddress,
        int pathMtu,
        int maximumPeers = 1024,
        int socketBufferBytes = 4 * 1024 * 1024,
        int additionalIpv6HeaderBytes = 0,
        bool enableExperimentalProtocolNumbers = false,
        bool allowWildcardLocalAddress = false)
    {
        var budget =
            new DhmpIpv6PathBudget(
                pathMtu,
                additionalIpv6HeaderBytes);

        return new DhmpRawIpv6ListenerOptions(
            localAddress,
            budget.MaximumProtocolPayloadBytes,
            maximumPeers,
            socketBufferBytes,
            enableExperimentalProtocolNumbers,
            allowWildcardLocalAddress);
    }

    /// <summary>
    /// Conservative listener budget for paths whose PMTU has not been established.
    /// Uses the IPv6 minimum MTU (1280 bytes).
    /// </summary>
    public static DhmpRawIpv6ListenerOptions ForUnknownPath(
        IPAddress localAddress,
        int maximumPeers = 1024,
        int socketBufferBytes = 4 * 1024 * 1024,
        int additionalIpv6HeaderBytes = 0,
        bool enableExperimentalProtocolNumbers = false,
        bool allowWildcardLocalAddress = false)
        => FromPathMtu(
            localAddress,
            DhmpIpv6PathBudget.MinimumIpv6Mtu,
            maximumPeers,
            socketBufferBytes,
            additionalIpv6HeaderBytes,
            enableExperimentalProtocolNumbers,
            allowWildcardLocalAddress);

    public IPAddress LocalAddress { get; }
    public int MaximumPayloadBytes { get; }
    public int MaximumPeers { get; }
    public int SocketBufferBytes { get; }

    /// <summary>
    /// True only when the caller explicitly opted into the RFC 4727/IANA experimental
    /// IPv6 Next Header value 253. Production Internet reachability is not implied.
    /// </summary>
    public bool ExperimentalProtocolNumbersEnabled { get; }

    /// <summary>
    /// True only when one process intentionally owns wildcard raw-DHMP delivery.
    /// Prefer one explicit local IPv6 address per DHMP service.
    /// </summary>
    public bool WildcardLocalAddressAllowed { get; }

    public byte DataProtocolNumber =>
        DhmpProtocol.ExperimentalIpv6DataNextHeader;

    internal void EnsureExperimentalProtocolNumbersEnabled()
    {
        if (!ExperimentalProtocolNumbersEnabled)
            throw new InvalidOperationException(
                "DHMP raw IPv6 currently uses experimental Next Header value 253. " +
                "Set enableExperimentalProtocolNumbers: true only for an explicitly configured experiment.");
    }
}
