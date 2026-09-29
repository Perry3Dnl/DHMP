using System.Security.Cryptography;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// One-shot compatibility/capability handshake for an already configured IPv6 peer.
/// This is not peer discovery and does not authenticate peer identity.
/// </summary>
public static class DhmpRawIpv6Handshake
{
    public static async Task<DhmpNegotiatedPeer> InitiateAsync(
        DhmpRawIpv6Options options,
        DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy,
        DhmpReceivePolicy receivePolicy,
        Guid schemaId,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalConfiguration(
            options,
            wireContract,
            sendPolicy,
            receivePolicy);

        var localProfile = new DhmpPeerProfile(
            wireContract,
            receivePolicy.MaximumPayloadBytes,
            schemaId);

        uint correlationId =
            checked((uint)RandomNumberGenerator.GetInt32(
                1,
                int.MaxValue));

        var hello =
            DhmpControlMessage.Hello(
                localProfile,
                correlationId);

        using var channel =
            new DhmpRawIpv6ControlChannel(options);

        await channel.SendAsync(
            hello,
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var response =
                await channel.ReceiveAsync(
                    cancellationToken).ConfigureAwait(false);

            if (response.CorrelationId != correlationId)
                continue;

            return DhmpControlNegotiator.CompleteResponse(
                localProfile,
                sendPolicy,
                correlationId,
                response);
        }
    }

    public static async Task<DhmpNegotiatedPeer> RespondOnceAsync(
        DhmpRawIpv6Options options,
        DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy,
        DhmpReceivePolicy receivePolicy,
        Guid schemaId,
        CancellationToken cancellationToken = default)
    {
        ValidateLocalConfiguration(
            options,
            wireContract,
            sendPolicy,
            receivePolicy);

        var localProfile = new DhmpPeerProfile(
            wireContract,
            receivePolicy.MaximumPayloadBytes,
            schemaId);

        using var channel =
            new DhmpRawIpv6ControlChannel(options);

        while (true)
        {
            var incoming =
                await channel.ReceiveAsync(
                    cancellationToken).ConfigureAwait(false);

            if (incoming.Type != DhmpControlMessageType.Hello)
                continue;

            var evaluation =
                DhmpControlNegotiator.EvaluateHello(
                    localProfile,
                    incoming);

            await channel.SendAsync(
                evaluation.Response,
                cancellationToken).ConfigureAwait(false);

            if (!evaluation.Accepted ||
                !evaluation.RemoteProfile.HasValue)
                throw new DhmpNegotiationException(
                    evaluation.Response.RejectReason);

            return DhmpControlNegotiator.CreateNegotiatedPeer(
                evaluation.RemoteProfile.Value,
                sendPolicy);
        }
    }

    private static void ValidateLocalConfiguration(
        DhmpRawIpv6Options options,
        DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy,
        DhmpReceivePolicy receivePolicy)
    {
        ArgumentNullException.ThrowIfNull(options);

        wireContract.Validate();
        sendPolicy.Validate(wireContract);
        receivePolicy.Validate(wireContract);

        if (sendPolicy.MaximumPayloadBytes >
            options.MaximumPayloadBytes)
            throw new ArgumentException(
                "Local DHMP send policy exceeds the raw IPv6 backend payload limit.",
                nameof(sendPolicy));

        if (receivePolicy.MaximumPayloadBytes >
            options.MaximumPayloadBytes)
            throw new ArgumentException(
                "Local DHMP receive policy exceeds the raw IPv6 backend payload limit.",
                nameof(receivePolicy));
    }
}
