using System.Security.Cryptography;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// Bounded one-shot compatibility exchange for a configured IPv6 peer.
/// This is not peer discovery or authentication. Lost control packets are not retried.
/// </summary>
public static class DhmpRawIpv6Handshake
{
    public static Task<DhmpNegotiatedPeer> InitiateAsync(
        DhmpRawIpv6Options options, DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy, DhmpReceivePolicy receivePolicy, Guid schemaId,
        CancellationToken cancellationToken = default)
        => InitiateCoreAsync(options, wireContract, sendPolicy, receivePolicy, schemaId,
            () => new DhmpRawIpv6ControlChannel(options), TimeProvider.System, cancellationToken);

    public static Task<DhmpNegotiatedPeer> RespondOnceAsync(
        DhmpRawIpv6Options options, DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy, DhmpReceivePolicy receivePolicy, Guid schemaId,
        CancellationToken cancellationToken = default)
        => RespondCoreAsync(options, wireContract, sendPolicy, receivePolicy, schemaId,
            () => new DhmpRawIpv6ControlChannel(options), TimeProvider.System, cancellationToken);

    internal static async Task<DhmpNegotiatedPeer> InitiateCoreAsync(
        DhmpRawIpv6Options options, DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy, DhmpReceivePolicy receivePolicy, Guid schemaId,
        Func<IDhmpControlPacketChannel> channelFactory, TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalConfiguration(options, wireContract, sendPolicy, receivePolicy);
        var localProfile = new DhmpPeerProfile(wireContract, receivePolicy.MaximumPayloadBytes, schemaId);
        uint correlationId = checked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue));

        return await DhmpHandshakeDeadline.RunAsync(options.HandshakeTimeout, timeProvider,
            cancellationToken, async token =>
            {
                token.ThrowIfCancellationRequested();
                using var channel = channelFactory();
                byte[] packet = new byte[DhmpProtocol.ControlPacketSize];
                DhmpControlCodec.Encode(DhmpControlMessage.Hello(localProfile, correlationId), packet);
                await channel.SendPacketAsync(packet, token).ConfigureAwait(false);

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var response = await ReceiveAsync(channel, packet, token).ConfigureAwait(false);
                    if (response.CorrelationId != correlationId)
                        continue;

                    token.ThrowIfCancellationRequested();
                    return DhmpControlNegotiator.CompleteResponse(
                        localProfile, sendPolicy, correlationId, response);
                }
            }).ConfigureAwait(false);
    }

    internal static async Task<DhmpNegotiatedPeer> RespondCoreAsync(
        DhmpRawIpv6Options options, DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy, DhmpReceivePolicy receivePolicy, Guid schemaId,
        Func<IDhmpControlPacketChannel> channelFactory, TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalConfiguration(options, wireContract, sendPolicy, receivePolicy);
        var localProfile = new DhmpPeerProfile(wireContract, receivePolicy.MaximumPayloadBytes, schemaId);

        return await DhmpHandshakeDeadline.RunAsync(options.HandshakeTimeout, timeProvider,
            cancellationToken, async token =>
            {
                token.ThrowIfCancellationRequested();
                using var channel = channelFactory();
                byte[] packet = new byte[DhmpProtocol.ControlPacketSize];

                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var incoming = await ReceiveAsync(channel, packet, token).ConfigureAwait(false);
                    if (incoming.Type != DhmpControlMessageType.Hello)
                        continue;

                    var evaluation = DhmpControlNegotiator.EvaluateHello(localProfile, incoming);
                    DhmpControlCodec.Encode(evaluation.Response, packet);
                    await channel.SendPacketAsync(packet, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();

                    if (!evaluation.Accepted || !evaluation.RemoteProfile.HasValue)
                        throw new DhmpNegotiationException(evaluation.Response.RejectReason);

                    return DhmpControlNegotiator.CreateNegotiatedPeer(
                        evaluation.RemoteProfile.Value, sendPolicy);
                }
            }).ConfigureAwait(false);
    }

    private static async ValueTask<DhmpControlMessage> ReceiveAsync(
        IDhmpControlPacketChannel channel, Memory<byte> packet, CancellationToken token)
    {
        int received = await channel.ReceivePacketAsync(packet, token).ConfigureAwait(false);
        if (received != packet.Length || !DhmpControlCodec.TryDecode(packet.Span, out var message))
            throw new DhmpProtocolException("Received malformed or unsupported DHMP control packet.");
        return message;
    }

    private static void ValidateLocalConfiguration(
        DhmpRawIpv6Options options, DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy, DhmpReceivePolicy receivePolicy)
    {
        ArgumentNullException.ThrowIfNull(options);
        wireContract.Validate();
        sendPolicy.Validate(wireContract);
        receivePolicy.Validate(wireContract);

        if (sendPolicy.MaximumPayloadBytes > options.MaximumPayloadBytes)
            throw new ArgumentException(
                "Local DHMP send policy exceeds the raw IPv6 backend payload limit.", nameof(sendPolicy));
        if (receivePolicy.MaximumPayloadBytes > options.MaximumPayloadBytes)
            throw new ArgumentException(
                "Local DHMP receive policy exceeds the raw IPv6 backend payload limit.", nameof(receivePolicy));
    }
}
