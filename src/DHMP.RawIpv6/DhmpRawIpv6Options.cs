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
        bool enableExperimentalProtocolNumbers = false)
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
    }

    public static DhmpRawIpv6Options FromPathMtu(
        IPAddress localAddress,
        IPAddress remoteAddress,
        int pathMtu,
        int socketBufferBytes = 4 * 1024 * 1024,
        int additionalIpv6HeaderBytes = 0,
        TimeSpan? handshakeTimeout = null,
        bool enableExperimentalProtocolNumbers = false)
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
            enableExperimentalProtocolNumbers);
    }

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
