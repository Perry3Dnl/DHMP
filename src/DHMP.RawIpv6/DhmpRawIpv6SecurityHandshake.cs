using System.Security.Cryptography;
using DHMP.Security;

namespace DHMP.RawIpv6;

/// <summary>
/// Bounded one-shot PSK setup over experimental control protocol 254.
/// PSK setup V2 requires a fresh responder challenge and initiator confirmation; no retransmission.
/// </summary>
public static class DhmpRawIpv6SecurityHandshake
{
    public static Task<DhmpPskChaCha20Poly1305Session> InitiateAsync(
        DhmpRawIpv6Options options, DhmpPreSharedKey preSharedKey,
        CancellationToken cancellationToken = default)
        => InitiateCoreAsync(options, preSharedKey,
            () => new DhmpRawIpv6ControlChannel(options), TimeProvider.System, cancellationToken);

    public static Task<DhmpPskChaCha20Poly1305Session> RespondOnceAsync(
        DhmpRawIpv6Options options, DhmpPreSharedKey preSharedKey,
        CancellationToken cancellationToken = default)
        => RespondCoreAsync(options, preSharedKey,
            () => new DhmpRawIpv6ControlChannel(options), TimeProvider.System, cancellationToken);

    internal static async Task<DhmpPskChaCha20Poly1305Session> InitiateCoreAsync(
        DhmpRawIpv6Options options, DhmpPreSharedKey preSharedKey,
        Func<IDhmpControlPacketChannel> channelFactory, TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(preSharedKey);
        Guid initiatorNonce = Guid.NewGuid();
        uint correlationId = checked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue));
        var offer = new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Offer,
            initiatorNonce, Guid.Empty, preSharedKey.KeyId, correlationId);

        return await DhmpHandshakeDeadline.RunAsync(options.HandshakeTimeout, timeProvider,
            cancellationToken, async token =>
            {
                token.ThrowIfCancellationRequested();
                byte[] packet = new byte[DhmpSecuritySetupCodec.PacketSize];
                DhmpSecuritySetupCodec.Encode(offer, preSharedKey, packet);
                using var channel = channelFactory();
                await channel.SendPacketAsync(packet, token).ConfigureAwait(false);

                var challenge = await ReceiveAsync(channel, packet, preSharedKey, token).ConfigureAwait(false);
                if (challenge.Type != DhmpSecuritySetupType.Challenge ||
                    challenge.InitiatorNonce != initiatorNonce ||
                    challenge.CorrelationId != correlationId || challenge.KeyId != preSharedKey.KeyId)
                    throw new DhmpSecurityException("PSK V2 challenge does not match the outstanding OFFER.");

                token.ThrowIfCancellationRequested();
                DhmpSecuritySetupCodec.Encode(challenge.WithType(DhmpSecuritySetupType.Confirm), preSharedKey, packet);
                await channel.SendPacketAsync(packet, token).ConfigureAwait(false);
                var accept = await ReceiveAsync(channel, packet, preSharedKey, token).ConfigureAwait(false);
                if (accept.Type != DhmpSecuritySetupType.Accept || !accept.MatchesTranscript(challenge))
                    throw new DhmpSecurityException("PSK V2 ACCEPT does not match the confirmed transcript.");

                token.ThrowIfCancellationRequested();
                return new DhmpPskChaCha20Poly1305Session(preSharedKey,
                    DhmpSecuritySetupCodec.DeriveSessionId(challenge), DhmpSecurityRole.Initiator);
            }).ConfigureAwait(false);
    }

    internal static async Task<DhmpPskChaCha20Poly1305Session> RespondCoreAsync(
        DhmpRawIpv6Options options, DhmpPreSharedKey preSharedKey,
        Func<IDhmpControlPacketChannel> channelFactory, TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(preSharedKey);

        return await DhmpHandshakeDeadline.RunAsync(options.HandshakeTimeout, timeProvider,
            cancellationToken, async token =>
            {
                token.ThrowIfCancellationRequested();
                using var channel = channelFactory();
                byte[] packet = new byte[DhmpSecuritySetupCodec.PacketSize];
                var offer = await ReceiveAsync(channel, packet, preSharedKey, token).ConfigureAwait(false);
                if (offer.Type != DhmpSecuritySetupType.Offer)
                    throw new DhmpSecurityException("Expected an authenticated PSK V2 OFFER.");

                var challenge = new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Challenge,
                    offer.InitiatorNonce, Guid.NewGuid(), preSharedKey.KeyId, offer.CorrelationId);
                DhmpSecuritySetupCodec.Encode(challenge, preSharedKey, packet);
                await channel.SendPacketAsync(packet, token).ConfigureAwait(false);
                var confirm = await ReceiveAsync(channel, packet, preSharedKey, token).ConfigureAwait(false);
                if (confirm.Type != DhmpSecuritySetupType.Confirm || !confirm.MatchesTranscript(challenge))
                    throw new DhmpSecurityException("PSK V2 CONFIRM does not match this responder's fresh challenge.");

                token.ThrowIfCancellationRequested();
                DhmpSecuritySetupCodec.Encode(challenge.WithType(DhmpSecuritySetupType.Accept), preSharedKey, packet);
                await channel.SendPacketAsync(packet, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new DhmpPskChaCha20Poly1305Session(preSharedKey,
                    DhmpSecuritySetupCodec.DeriveSessionId(challenge), DhmpSecurityRole.Responder);
            }).ConfigureAwait(false);
    }

    private static async ValueTask<DhmpSecuritySetupMessage> ReceiveAsync(
        IDhmpControlPacketChannel channel, Memory<byte> packet,
        DhmpPreSharedKey preSharedKey, CancellationToken token)
    {
        int received = await channel.ReceivePacketAsync(packet, token).ConfigureAwait(false);
        if (received != packet.Length ||
            !DhmpSecuritySetupCodec.TryDecode(packet.Span, preSharedKey, out var message))
            throw new DhmpSecurityException(
                "DHMP security control packet failed PSK authentication or format validation.");
        return message;
    }
}
