using System.Security.Cryptography;
using DHMP.Security;

namespace DHMP.RawIpv6;

/// <summary>
/// Bounded one-shot PSK setup over experimental control protocol 254.
/// Initiator attempts use fresh session IDs; no control retransmission is performed.
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
        Guid sessionId = Guid.NewGuid();
        uint correlationId = checked((uint)RandomNumberGenerator.GetInt32(1, int.MaxValue));
        var offer = new DhmpSecurityControlMessage(DhmpSecurityControlType.Offer,
            DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256, sessionId, preSharedKey.KeyId, correlationId);

        return await DhmpHandshakeDeadline.RunAsync(options.HandshakeTimeout, timeProvider,
            cancellationToken, async token =>
            {
                token.ThrowIfCancellationRequested();
                byte[] packet = new byte[DhmpSecurityControlCodec.PacketSize];
                DhmpSecurityControlCodec.Encode(offer, preSharedKey, packet);
                using var channel = channelFactory();
                await channel.SendPacketAsync(packet, token).ConfigureAwait(false);

                var response = await ReceiveAsync(channel, packet, preSharedKey, token).ConfigureAwait(false);
                if (response.CorrelationId != correlationId || response.SessionId != sessionId ||
                    response.KeyId != preSharedKey.KeyId)
                    throw new DhmpSecurityException(
                        "DHMP security response does not match the outstanding session offer.");
                if (response.Type == DhmpSecurityControlType.Reject)
                    throw new DhmpSecurityException($"DHMP security setup rejected: {response.RejectReason}.");
                if (response.Type != DhmpSecurityControlType.Accept)
                    throw new DhmpSecurityException("Expected authenticated DHMP security ACCEPT or REJECT.");

                token.ThrowIfCancellationRequested();
                return new DhmpPskChaCha20Poly1305Session(preSharedKey, sessionId, DhmpSecurityRole.Initiator);
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
                byte[] packet = new byte[DhmpSecurityControlCodec.PacketSize];
                var offer = await ReceiveAsync(channel, packet, preSharedKey, token).ConfigureAwait(false);
                if (offer.Type != DhmpSecurityControlType.Offer)
                    throw new DhmpSecurityException("Expected an authenticated DHMP security OFFER.");

                var accept = new DhmpSecurityControlMessage(DhmpSecurityControlType.Accept,
                    offer.Suite, offer.SessionId, preSharedKey.KeyId, offer.CorrelationId);
                DhmpSecurityControlCodec.Encode(accept, preSharedKey, packet);
                await channel.SendPacketAsync(packet, token).ConfigureAwait(false);

                token.ThrowIfCancellationRequested();
                return new DhmpPskChaCha20Poly1305Session(
                    preSharedKey, offer.SessionId, DhmpSecurityRole.Responder);
            }).ConfigureAwait(false);
    }

    private static async ValueTask<DhmpSecurityControlMessage> ReceiveAsync(
        IDhmpControlPacketChannel channel, Memory<byte> packet,
        DhmpPreSharedKey preSharedKey, CancellationToken token)
    {
        int received = await channel.ReceivePacketAsync(packet, token).ConfigureAwait(false);
        if (received != packet.Length ||
            !DhmpSecurityControlCodec.TryDecode(packet.Span, preSharedKey, out var message))
            throw new DhmpSecurityException(
                "DHMP security control packet failed PSK authentication or format validation.");
        return message;
    }
}
