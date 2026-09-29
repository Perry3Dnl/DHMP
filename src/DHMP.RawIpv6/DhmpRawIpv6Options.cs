using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// One direct IPv6 peer/path binding for the current single-session raw backend.
/// </summary>
public sealed class DhmpRawIpv6Options
{
    public DhmpRawIpv6Options(
        IPAddress localAddress,
        IPAddress remoteAddress,
        int maximumPayloadBytes,
        byte protocolNumber = DhmpProtocol.ExperimentalIpv6NextHeader,
        int socketBufferBytes = 4 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (localAddress.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException("Local address must be IPv6.", nameof(localAddress));
        if (remoteAddress.AddressFamily != AddressFamily.InterNetworkV6)
            throw new ArgumentException("Remote address must be IPv6.", nameof(remoteAddress));
        if (localAddress.IsIPv4MappedToIPv6 || remoteAddress.IsIPv4MappedToIPv6)
            throw new ArgumentException("DHMP raw IPv6 requires native IPv6 addresses.");
        if (maximumPayloadBytes <= 0 || maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        if (protocolNumber is not 253 and not 254)
            throw new ArgumentOutOfRangeException(
                nameof(protocolNumber),
                "Until DHMP has a permanent assignment, raw IPv6 bindings are restricted to experimental protocol numbers 253 or 254.");
        if (socketBufferBytes < maximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(socketBufferBytes));

        LocalAddress = localAddress;
        RemoteAddress = remoteAddress;
        MaximumPayloadBytes = maximumPayloadBytes;
        ProtocolNumber = protocolNumber;
        SocketBufferBytes = socketBufferBytes;
    }

    public IPAddress LocalAddress { get; }
    public IPAddress RemoteAddress { get; }
    public int MaximumPayloadBytes { get; }
    public byte ProtocolNumber { get; }
    public int SocketBufferBytes { get; }
}
