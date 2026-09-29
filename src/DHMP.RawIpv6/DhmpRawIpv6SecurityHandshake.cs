using System.Security.Cryptography;
using DHMP.Security;

namespace DHMP.RawIpv6;

/// <summary>
/// Authenticated PSK security setup over DHMP control protocol 254.
/// This runs after/beside compatibility negotiation for an already configured peer.
/// </summary>
public static class DhmpRawIpv6SecurityHandshake
{
    public static async Task<DhmpPskChaCha20Poly1305Session> InitiateAsync(
        DhmpRawIpv6Options options,
        DhmpPreSharedKey preSharedKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(preSharedKey);

        Guid sessionId = Guid.NewGuid();

        uint correlationId =
            checked((uint)RandomNumberGenerator.GetInt32(
                1,
                int.MaxValue));

        var offer =
            new DhmpSecurityControlMessage(
                DhmpSecurityControlType.Offer,
                DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256,
                sessionId,
                preSharedKey.KeyId,
                correlationId);

        byte[] packet =
            new byte[DhmpSecurityControlCodec.PacketSize];

        DhmpSecurityControlCodec.Encode(
            offer,
            preSharedKey,
            packet);

        using var channel =
            new DhmpRawIpv6ControlChannel(options);

        await channel.SendPacketAsync(
            packet,
            cancellationToken).ConfigureAwait(false);

        byte[] responsePacket =
            new byte[DhmpSecurityControlCodec.PacketSize];

        int received =
            await channel.ReceivePacketAsync(
                responsePacket,
                cancellationToken).ConfigureAwait(false);

        if (received != responsePacket.Length ||
            !DhmpSecurityControlCodec.TryDecode(
                responsePacket,
                preSharedKey,
                out var response))
            throw new DhmpSecurityException(
                "DHMP security response failed PSK authentication or format validation.");

        if (response.CorrelationId != correlationId ||
            response.SessionId != sessionId ||
            response.KeyId != preSharedKey.KeyId)
            throw new DhmpSecurityException(
                "DHMP security response does not match the outstanding session offer.");

        if (response.Type == DhmpSecurityControlType.Reject)
            throw new DhmpSecurityException(
                $"DHMP security setup rejected: {response.RejectReason}.");

        if (response.Type != DhmpSecurityControlType.Accept)
            throw new DhmpSecurityException(
                "Expected authenticated DHMP security ACCEPT or REJECT.");

        return new DhmpPskChaCha20Poly1305Session(
            preSharedKey,
            sessionId,
            DhmpSecurityRole.Initiator);
    }

    public static async Task<DhmpPskChaCha20Poly1305Session> RespondOnceAsync(
        DhmpRawIpv6Options options,
        DhmpPreSharedKey preSharedKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(preSharedKey);

        using var channel =
            new DhmpRawIpv6ControlChannel(options);

        byte[] offerPacket =
            new byte[DhmpSecurityControlCodec.PacketSize];

        int received =
            await channel.ReceivePacketAsync(
                offerPacket,
                cancellationToken).ConfigureAwait(false);

        if (received != offerPacket.Length ||
            !DhmpSecurityControlCodec.TryDecode(
                offerPacket,
                preSharedKey,
                out var offer))
            throw new DhmpSecurityException(
                "DHMP security offer failed PSK authentication or format validation.");

        if (offer.Type != DhmpSecurityControlType.Offer)
            throw new DhmpSecurityException(
                "Expected an authenticated DHMP security OFFER.");

        var accept =
            new DhmpSecurityControlMessage(
                DhmpSecurityControlType.Accept,
                offer.Suite,
                offer.SessionId,
                preSharedKey.KeyId,
                offer.CorrelationId);

        byte[] responsePacket =
            new byte[DhmpSecurityControlCodec.PacketSize];

        DhmpSecurityControlCodec.Encode(
            accept,
            preSharedKey,
            responsePacket);

        await channel.SendPacketAsync(
            responsePacket,
            cancellationToken).ConfigureAwait(false);

        return new DhmpPskChaCha20Poly1305Session(
            preSharedKey,
            offer.SessionId,
            DhmpSecurityRole.Responder);
    }
}
