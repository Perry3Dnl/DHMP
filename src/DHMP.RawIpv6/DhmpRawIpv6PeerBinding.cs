using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;
using DHMP.Server;

namespace DHMP.RawIpv6;

/// <summary>
/// One source-IPv6-address to DHMP server/session binding.
/// The callback and optional decoder are owned by the caller.
/// </summary>
public sealed class DhmpRawIpv6PeerBinding
{
    public DhmpRawIpv6PeerBinding(
        IPAddress remoteAddress,
        DhmpServer server,
        Action<ReadOnlySpan<byte>> publishBatch,
        IDhmpPacketDecoder? decoder = null)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(publishBatch);

        if (remoteAddress.AddressFamily !=
            AddressFamily.InterNetworkV6 ||
            remoteAddress.IsIPv4MappedToIPv6)
            throw new ArgumentException(
                "DHMP peer binding requires a native IPv6 address.",
                nameof(remoteAddress));

        if (decoder is not null &&
            decoder.OverheadBytes < 0)
            throw new ArgumentException(
                "Packet decoder overhead cannot be negative.",
                nameof(decoder));

        RemoteAddress = remoteAddress;
        Server = server;
        PublishBatch = publishBatch;
        Decoder = decoder;
    }

    public IPAddress RemoteAddress { get; }
    public DhmpServer Server { get; }
    public Action<ReadOnlySpan<byte>> PublishBatch { get; }
    public IDhmpPacketDecoder? Decoder { get; }

    public int MaximumNetworkPayloadBytes =>
        checked(
            Server.ReceivePolicy.MaximumPayloadBytes +
            (Decoder?.OverheadBytes ?? 0));
}
