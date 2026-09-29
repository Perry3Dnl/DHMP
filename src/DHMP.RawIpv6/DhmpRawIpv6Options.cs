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
        int socketBufferBytes = 4 * 1024 * 1024)
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

        if (maximumPayloadBytes <= 0 ||
            maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        if (socketBufferBytes < maximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(socketBufferBytes));

        LocalAddress = localAddress;
        RemoteAddress = remoteAddress;
        MaximumPayloadBytes = maximumPayloadBytes;
        SocketBufferBytes = socketBufferBytes;
    }

    public static DhmpRawIpv6Options FromPathMtu(
        IPAddress localAddress,
        IPAddress remoteAddress,
        int pathMtu,
        int socketBufferBytes = 4 * 1024 * 1024,
        int additionalIpv6HeaderBytes = 0)
    {
        var budget =
            new DhmpIpv6PathBudget(
                pathMtu,
                additionalIpv6HeaderBytes);

        return new DhmpRawIpv6Options(
            localAddress,
            remoteAddress,
            budget.MaximumProtocolPayloadBytes,
            socketBufferBytes);
    }

    public IPAddress LocalAddress { get; }
    public IPAddress RemoteAddress { get; }
    public int MaximumPayloadBytes { get; }
    public int SocketBufferBytes { get; }

    public byte DataProtocolNumber =>
        DhmpProtocol.ExperimentalIpv6DataNextHeader;

    public byte ControlProtocolNumber =>
        DhmpProtocol.ExperimentalIpv6ControlNextHeader;
}
