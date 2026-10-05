using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;
using DHMP.Server;

namespace DHMP.RawIpv6;

/// <summary>
/// One source-IPv6-address to DHMP server/session binding.
/// The callback and optional decoder are owned by the caller.
/// Await router retirement and join all other decoder users before disposing those resources.
/// </summary>
public sealed class DhmpRawIpv6PeerBinding
{
    public DhmpRawIpv6PeerBinding(
        IPAddress remoteAddress,
        DhmpServer server,
        Action<ReadOnlySpan<byte>> publishBatch,
        IDhmpPacketDecoder? decoder = null,
        bool allowUnprotectedPayloads = false,
        ulong? connectionId = null,
        int? connectionIdOffset = null)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(publishBatch);

        if (remoteAddress.AddressFamily !=
            AddressFamily.InterNetworkV6 ||
            remoteAddress.IsIPv4MappedToIPv6 ||
            remoteAddress.Equals(IPAddress.IPv6Any))
            throw new ArgumentException(
                "DHMP peer binding requires an explicit native IPv6 address.",
                nameof(remoteAddress));

        if (decoder is null && !allowUnprotectedPayloads)
            throw new InvalidOperationException(
                "Unprotected DHMP peer binding is disabled by default because plaintext V1 has no protocol-owned end-to-end integrity/authentication check.");

        if (decoder is not null &&
            decoder.OverheadBytes < 0)
            throw new ArgumentException(
                "Packet decoder overhead cannot be negative.",
                nameof(decoder));

        if (decoder is not null &&
            decoder.OverheadBytes >
                ushort.MaxValue -
                server.ReceivePolicy.MaximumPayloadBytes)
            throw new ArgumentException(
                "Packet decoder overhead cannot fit within the supported raw IPv6 payload range.",
                nameof(decoder));

        if (connectionId.HasValue != connectionIdOffset.HasValue)
            throw new ArgumentException(
                "ConnectionId and connectionIdOffset must either both be supplied or both be omitted.");

        if (connectionId is 0)
            throw new ArgumentOutOfRangeException(
                nameof(connectionId),
                "ConnectionId must be nonzero.");

        if (connectionIdOffset is not null &&
            (connectionIdOffset < 0 ||
             connectionIdOffset > server.WireContract.RecordSize - sizeof(ulong)))
            throw new ArgumentOutOfRangeException(
                nameof(connectionIdOffset),
                "The 8-byte ConnectionId field must fit inside one application record.");

        RemoteAddress = remoteAddress;
        Server = server;
        PublishBatch = publishBatch;
        Decoder = decoder;
        UnprotectedPayloadsAllowed = allowUnprotectedPayloads;
        ConnectionId = connectionId;
        ConnectionIdOffset = connectionIdOffset;
    }

    public IPAddress RemoteAddress { get; }
    public DhmpServer Server { get; }
    public Action<ReadOnlySpan<byte>> PublishBatch { get; }
    public IDhmpPacketDecoder? Decoder { get; }
    public bool UnprotectedPayloadsAllowed { get; }
    public ulong? ConnectionId { get; }
    public int? ConnectionIdOffset { get; }

    public int MaximumNetworkPayloadBytes =>
        checked(
            Server.ReceivePolicy.MaximumPayloadBytes +
            (Decoder?.OverheadBytes ?? 0));
}
