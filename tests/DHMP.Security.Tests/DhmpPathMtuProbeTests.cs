using DHMP.Security;
using Xunit;

namespace DHMP.Security.Tests;

public sealed class DhmpPathMtuProbeTests
{
    private static readonly Guid SessionId =
        Guid.Parse("50112233-4455-6677-8899-aabbccddeeff");

    [Fact]
    public void PaddedRequest_IsAuthenticatedAtExactProbeSize_AndReplayProtected()
    {
        using var key = new DhmpPreSharedKey(7, new byte[DhmpPreSharedKey.KeySizeBytes]);
        using var sender = new DhmpPskChaCha20Poly1305Session(
            key, SessionId, DhmpSecurityRole.Initiator);
        using var receiver = new DhmpPskChaCha20Poly1305Session(
            key, SessionId, DhmpSecurityRole.Responder);

        const int payloadBytes = 1460;
        var request = new DhmpPathMtuProbeMessage(
            DhmpPathMtuProbeType.Request,
            probeId: 1,
            probedPayloadBytes: payloadBytes);

        byte[] packet = new byte[payloadBytes];

        Assert.Equal(
            payloadBytes,
            sender.EncodePathMtuProbe(request, packet));

        Assert.True(receiver.TryDecodePathMtuProbe(packet, out var decoded));
        Assert.Equal(request, decoded);

        Assert.False(receiver.TryDecodePathMtuProbe(packet, out _));
    }

    [Fact]
    public void PaddingTamper_WrongDirection_AndTruncationAreRejected()
    {
        using var key = new DhmpPreSharedKey(7, new byte[DhmpPreSharedKey.KeySizeBytes]);
        using var sender = new DhmpPskChaCha20Poly1305Session(
            key, SessionId, DhmpSecurityRole.Initiator);
        using var receiver = new DhmpPskChaCha20Poly1305Session(
            key, SessionId, DhmpSecurityRole.Responder);

        const int payloadBytes = 1240;
        var request = new DhmpPathMtuProbeMessage(
            DhmpPathMtuProbeType.Request,
            probeId: 2,
            probedPayloadBytes: payloadBytes);

        byte[] packet = new byte[payloadBytes];
        sender.EncodePathMtuProbe(request, packet);

        byte[] tampered = packet.ToArray();
        tampered[100] ^= 1;

        Assert.False(receiver.TryDecodePathMtuProbe(tampered, out _));
        Assert.False(sender.TryDecodePathMtuProbe(packet, out _));
        Assert.False(receiver.TryDecodePathMtuProbe(packet.AsSpan(0, packet.Length - 1), out _));
        Assert.True(receiver.TryDecodePathMtuProbe(packet, out _));
    }

    [Fact]
    public void Response_IsCompact_ButAuthenticallyEchoesProbedSize()
    {
        using var key = new DhmpPreSharedKey(7, new byte[DhmpPreSharedKey.KeySizeBytes]);
        using var responder = new DhmpPskChaCha20Poly1305Session(
            key, SessionId, DhmpSecurityRole.Responder);
        using var initiator = new DhmpPskChaCha20Poly1305Session(
            key, SessionId, DhmpSecurityRole.Initiator);

        var response = new DhmpPathMtuProbeMessage(
            DhmpPathMtuProbeType.Response,
            probeId: 3,
            probedPayloadBytes: 1460);

        byte[] packet =
            new byte[DhmpPskChaCha20Poly1305Session.PathMtuProbeMinimumPacketSize];

        Assert.Equal(
            packet.Length,
            responder.EncodePathMtuProbe(response, packet));

        Assert.True(initiator.TryDecodePathMtuProbe(packet, out var decoded));
        Assert.Equal(response, decoded);
    }

    [Fact]
    public void InvalidProbeSizes_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpPathMtuProbeMessage(
                DhmpPathMtuProbeType.Request,
                1,
                DhmpPskChaCha20Poly1305Session.PathMtuProbeMinimumPacketSize - 1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpPathMtuProbeMessage(
                DhmpPathMtuProbeType.Request,
                1,
                ushort.MaxValue + 1));
    }
}
