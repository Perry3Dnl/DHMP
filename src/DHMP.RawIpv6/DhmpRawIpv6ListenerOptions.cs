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
        bool enableExperimentalProtocolNumbers = false)
    {
        ArgumentNullException.ThrowIfNull(localAddress);

        if (localAddress.AddressFamily != AddressFamily.InterNetworkV6 ||
            localAddress.IsIPv4MappedToIPv6)
            throw new ArgumentException(
                "DHMP raw IPv6 listener requires a native IPv6 local address.",
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
    }

    public static DhmpRawIpv6ListenerOptions FromPathMtu(
        IPAddress localAddress,
        int pathMtu,
        int maximumPeers = 1024,
        int socketBufferBytes = 4 * 1024 * 1024,
        int additionalIpv6HeaderBytes = 0,
        bool enableExperimentalProtocolNumbers = false)
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
            enableExperimentalProtocolNumbers);
    }

    public IPAddress LocalAddress { get; }
    public int MaximumPayloadBytes { get; }
    public int MaximumPeers { get; }
    public int SocketBufferBytes { get; }

    /// <summary>
    /// True only when the caller explicitly opted into the RFC 4727/IANA experimental
    /// IPv6 Next Header value 253. Production Internet reachability is not implied.
    /// </summary>
    public bool ExperimentalProtocolNumbersEnabled { get; }

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
