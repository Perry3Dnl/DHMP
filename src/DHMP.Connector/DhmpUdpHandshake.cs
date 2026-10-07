using System.Net;
using System.Security.Cryptography;
using DHMP.Protocol;
using DHMP.Security;

namespace DHMP.Connector;

/// <summary>
/// Compatibility and PSK setup over the UDP compatibility control socket.
/// Data packets remain headerless DHMP records inside the UDP payload.
/// </summary>
internal static class DhmpUdpHandshake
{
    public static Task<DhmpNegotiatedPeer> InitiateCompatibilityAsync(
        DhmpUdpRuntime runtime,
        IPAddress remoteAddress,
        DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy,
        DhmpReceivePolicy receivePolicy,
        Guid schemaId,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunWithDeadlineAsync(
            timeout,
            cancellationToken,
            async token =>
            {
                Validate(
                    runtime,
                    wireContract,
                    sendPolicy,
                    receivePolicy);

                var localProfile =
                    new DhmpPeerProfile(
                        wireContract,
                        receivePolicy.MaximumPayloadBytes,
                        schemaId);

                uint correlationId =
                    checked(
                        (uint)RandomNumberGenerator.GetInt32(
                            1,
                            int.MaxValue));

                using var channel =
                    runtime.CreateControlChannel(
                        remoteAddress);

                byte[] packet =
                    new byte[
                        DhmpProtocol.ControlPacketSize];

                DhmpControlCodec.Encode(
                    DhmpControlMessage.Hello(
                        localProfile,
                        correlationId),
                    packet);

                await channel.SendPacketAsync(
                    packet,
                    token).ConfigureAwait(false);

                while (true)
                {
                    int received =
                        await channel.ReceivePacketAsync(
                            packet,
                            token).ConfigureAwait(false);

                    if (received != packet.Length ||
                        !DhmpControlCodec.TryDecode(
                            packet,
                            out DhmpControlMessage response))
                        throw new DhmpProtocolException(
                            "Received malformed DHMP compatibility control packet over UDP.");

                    if (response.CorrelationId !=
                        correlationId)
                        continue;

                    return
                        DhmpControlNegotiator.CompleteResponse(
                            localProfile,
                            sendPolicy,
                            correlationId,
                            response);
                }
            });

    public static Task<DhmpNegotiatedPeer> RespondCompatibilityAsync(
        DhmpUdpRuntime runtime,
        IPAddress remoteAddress,
        DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy,
        DhmpReceivePolicy receivePolicy,
        Guid schemaId,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunWithDeadlineAsync(
            timeout,
            cancellationToken,
            async token =>
            {
                Validate(
                    runtime,
                    wireContract,
                    sendPolicy,
                    receivePolicy);

                var localProfile =
                    new DhmpPeerProfile(
                        wireContract,
                        receivePolicy.MaximumPayloadBytes,
                        schemaId);

                using var channel =
                    runtime.CreateControlChannel(
                        remoteAddress);

                byte[] packet =
                    new byte[
                        DhmpProtocol.ControlPacketSize];

                while (true)
                {
                    int received =
                        await channel.ReceivePacketAsync(
                            packet,
                            token).ConfigureAwait(false);

                    if (received != packet.Length ||
                        !DhmpControlCodec.TryDecode(
                            packet,
                            out DhmpControlMessage incoming))
                        throw new DhmpProtocolException(
                            "Received malformed DHMP compatibility control packet over UDP.");

                    if (incoming.Type !=
                        DhmpControlMessageType.Hello)
                        continue;

                    var evaluation =
                        DhmpControlNegotiator.EvaluateHello(
                            localProfile,
                            incoming);

                    DhmpControlCodec.Encode(
                        evaluation.Response,
                        packet);

                    await channel.SendPacketAsync(
                        packet,
                        token).ConfigureAwait(false);

                    if (!evaluation.Accepted ||
                        !evaluation.RemoteProfile.HasValue)
                        throw new DhmpNegotiationException(
                            evaluation.Response.RejectReason);

                    return
                        DhmpControlNegotiator.CreateNegotiatedPeer(
                            evaluation.RemoteProfile.Value,
                            sendPolicy);
                }
            });

    public static Task<DhmpPskChaCha20Poly1305Session> InitiateSecurityAsync(
        DhmpUdpRuntime runtime,
        IPAddress remoteAddress,
        DhmpPreSharedKey preSharedKey,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunWithDeadlineAsync(
            timeout,
            cancellationToken,
            async token =>
            {
                Guid initiatorNonce =
                    Guid.NewGuid();

                uint correlationId =
                    checked(
                        (uint)RandomNumberGenerator.GetInt32(
                            1,
                            int.MaxValue));

                var offer =
                    new DhmpSecuritySetupMessage(
                        DhmpSecuritySetupType.Offer,
                        initiatorNonce,
                        Guid.Empty,
                        preSharedKey.KeyId,
                        correlationId);

                byte[] packet =
                    new byte[
                        DhmpSecuritySetupCodec.PacketSize];

                using var channel =
                    runtime.CreateControlChannel(
                        remoteAddress);

                DhmpSecuritySetupCodec.Encode(
                    offer,
                    preSharedKey,
                    packet);

                await channel.SendPacketAsync(
                    packet,
                    token).ConfigureAwait(false);

                DhmpSecuritySetupMessage challenge =
                    await ReceiveSecurityAsync(
                        channel,
                        packet,
                        preSharedKey,
                        token).ConfigureAwait(false);

                if (challenge.Type !=
                        DhmpSecuritySetupType.Challenge ||
                    challenge.InitiatorNonce !=
                        initiatorNonce ||
                    challenge.CorrelationId !=
                        correlationId ||
                    challenge.KeyId !=
                        preSharedKey.KeyId)
                    throw new DhmpSecurityException(
                        "UDP PSK challenge does not match the outstanding offer.");

                DhmpSecuritySetupCodec.Encode(
                    challenge.WithType(
                        DhmpSecuritySetupType.Confirm),
                    preSharedKey,
                    packet);

                await channel.SendPacketAsync(
                    packet,
                    token).ConfigureAwait(false);

                DhmpSecuritySetupMessage accept =
                    await ReceiveSecurityAsync(
                        channel,
                        packet,
                        preSharedKey,
                        token).ConfigureAwait(false);

                if (accept.Type !=
                        DhmpSecuritySetupType.Accept ||
                    !accept.MatchesTranscript(
                        challenge))
                    throw new DhmpSecurityException(
                        "UDP PSK accept does not match the confirmed transcript.");

                return
                    new DhmpPskChaCha20Poly1305Session(
                        preSharedKey,
                        DhmpSecuritySetupCodec.DeriveSessionId(
                            challenge),
                        DhmpSecurityRole.Initiator);
            });

    public static Task<DhmpPskChaCha20Poly1305Session> RespondSecurityAsync(
        DhmpUdpRuntime runtime,
        IPAddress remoteAddress,
        DhmpPreSharedKey preSharedKey,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        RunWithDeadlineAsync(
            timeout,
            cancellationToken,
            async token =>
            {
                byte[] packet =
                    new byte[
                        DhmpSecuritySetupCodec.PacketSize];

                using var channel =
                    runtime.CreateControlChannel(
                        remoteAddress);

                DhmpSecuritySetupMessage offer =
                    await ReceiveSecurityAsync(
                        channel,
                        packet,
                        preSharedKey,
                        token).ConfigureAwait(false);

                if (offer.Type !=
                    DhmpSecuritySetupType.Offer)
                    throw new DhmpSecurityException(
                        "Expected an authenticated UDP PSK offer.");

                var challenge =
                    new DhmpSecuritySetupMessage(
                        DhmpSecuritySetupType.Challenge,
                        offer.InitiatorNonce,
                        Guid.NewGuid(),
                        preSharedKey.KeyId,
                        offer.CorrelationId);

                DhmpSecuritySetupCodec.Encode(
                    challenge,
                    preSharedKey,
                    packet);

                await channel.SendPacketAsync(
                    packet,
                    token).ConfigureAwait(false);

                DhmpSecuritySetupMessage confirm =
                    await ReceiveSecurityAsync(
                        channel,
                        packet,
                        preSharedKey,
                        token).ConfigureAwait(false);

                if (confirm.Type !=
                        DhmpSecuritySetupType.Confirm ||
                    !confirm.MatchesTranscript(
                        challenge))
                    throw new DhmpSecurityException(
                        "UDP PSK confirm does not match the responder challenge.");

                DhmpSecuritySetupCodec.Encode(
                    challenge.WithType(
                        DhmpSecuritySetupType.Accept),
                    preSharedKey,
                    packet);

                await channel.SendPacketAsync(
                    packet,
                    token).ConfigureAwait(false);

                return
                    new DhmpPskChaCha20Poly1305Session(
                        preSharedKey,
                        DhmpSecuritySetupCodec.DeriveSessionId(
                            challenge),
                        DhmpSecurityRole.Responder);
            });

    private static async ValueTask<DhmpSecuritySetupMessage> ReceiveSecurityAsync(
        DhmpUdpControlChannel channel,
        Memory<byte> packet,
        DhmpPreSharedKey preSharedKey,
        CancellationToken cancellationToken)
    {
        int received =
            await channel.ReceivePacketAsync(
                packet,
                cancellationToken)
            .ConfigureAwait(false);

        if (received != packet.Length ||
            !DhmpSecuritySetupCodec.TryDecode(
                packet.Span,
                preSharedKey,
                out DhmpSecuritySetupMessage message))
            throw new DhmpSecurityException(
                "UDP DHMP security control packet failed authentication or format validation.");

        return message;
    }

    private static void Validate(
        DhmpUdpRuntime runtime,
        DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy,
        DhmpReceivePolicy receivePolicy)
    {
        wireContract.Validate();
        sendPolicy.Validate(
            wireContract);
        receivePolicy.Validate(
            wireContract);

        if (sendPolicy.MaximumPayloadBytes >
                runtime.MaximumPayloadBytes ||
            receivePolicy.MaximumPayloadBytes >
                runtime.MaximumPayloadBytes)
            throw new ArgumentException(
                "DHMP policy exceeds the UDP compatibility payload ceiling.");
    }

    private static async Task<T> RunWithDeadlineAsync<T>(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> action)
    {
        using var linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        linked.CancelAfter(
            timeout);

        try
        {
            return await action(
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "DHMP UDP compatibility handshake timed out.");
        }
    }
}
