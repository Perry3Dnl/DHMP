using DHMP.Protocol;
using Xunit;

namespace DHMP.Security.Tests;

public sealed class DhmpSecurityTests
{
    private static readonly Guid SessionId =
        Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    private static byte[] KeyBytes()
        => Enumerable.Range(1, DhmpPreSharedKey.KeySizeBytes)
            .Select(i => (byte)i)
            .ToArray();

    [Fact]
    public void SecurityControlCodec_RoundTripsAuthenticatedOffer()
    {
        using var key =
            new DhmpPreSharedKey(
                7,
                KeyBytes());

        var message =
            new DhmpSecurityControlMessage(
                DhmpSecurityControlType.Offer,
                DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256,
                SessionId,
                7,
                123);

        byte[] packet =
            new byte[DhmpSecurityControlCodec.PacketSize];

        DhmpSecurityControlCodec.Encode(
            message,
            key,
            packet);

        Assert.Equal(48, packet.Length);

        Assert.True(
            DhmpSecurityControlCodec.TryDecode(
                packet,
                key,
                out var decoded));

        Assert.Equal(message, decoded);
    }

    [Fact]
    public void SecurityControlCodec_TamperAndWrongPskAreRejected()
    {
        using var key =
            new DhmpPreSharedKey(
                7,
                KeyBytes());

        byte[] wrongKeyBytes = KeyBytes();
        wrongKeyBytes[0] ^= 0xff;

        using var wrongKey =
            new DhmpPreSharedKey(
                7,
                wrongKeyBytes);

        var message =
            new DhmpSecurityControlMessage(
                DhmpSecurityControlType.Offer,
                DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256,
                SessionId,
                7,
                123);

        byte[] packet =
            new byte[DhmpSecurityControlCodec.PacketSize];

        DhmpSecurityControlCodec.Encode(
            message,
            key,
            packet);

        Assert.False(
            DhmpSecurityControlCodec.TryDecode(
                packet,
                wrongKey,
                out _));

        packet[12] ^= 1;

        Assert.False(
            DhmpSecurityControlCodec.TryDecode(
                packet,
                key,
                out _));
    }

    [Fact]
    public void InitiatorAndResponder_RoundTripBothDirections()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var initiator =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        using var responder =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Responder);

        byte[] plaintext = Enumerable.Range(0, 64)
            .Select(i => (byte)i)
            .ToArray();

        byte[] protectedPacket =
            new byte[
                plaintext.Length +
                DhmpPskChaCha20Poly1305Session.Overhead];

        int written =
            initiator.Protect(
                plaintext,
                protectedPacket);

        byte[] decoded =
            new byte[plaintext.Length];

        Assert.True(
            responder.TryDecode(
                protectedPacket.AsSpan(0, written),
                decoded,
                out int decodedBytes));

        Assert.Equal(
            plaintext.Length,
            decodedBytes);
        Assert.Equal(
            plaintext,
            decoded);

        byte[] reply =
            Enumerable.Range(100, 32)
                .Select(i => (byte)i)
                .ToArray();

        byte[] protectedReply =
            new byte[
                reply.Length +
                DhmpPskChaCha20Poly1305Session.Overhead];

        responder.Protect(
            reply,
            protectedReply);

        byte[] decodedReply =
            new byte[reply.Length];

        Assert.True(
            initiator.TryDecode(
                protectedReply,
                decodedReply,
                out int replyBytes));

        Assert.Equal(reply.Length, replyBytes);
        Assert.Equal(reply, decodedReply);
    }

    [Fact]
    public void TamperedCiphertext_IsRejectedAndPlaintextCleared()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var initiator =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        using var responder =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Responder);

        byte[] packet =
            new byte[
                32 +
                DhmpPskChaCha20Poly1305Session.Overhead];

        initiator.Protect(
            new byte[32],
            packet);

        packet[10] ^= 1;

        byte[] plaintext =
            Enumerable.Repeat((byte)0xaa, 32)
                .ToArray();

        Assert.False(
            responder.TryDecode(
                packet,
                plaintext,
                out int bytes));

        Assert.Equal(0, bytes);
        Assert.All(
            plaintext,
            value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void Replay_IsRejectedAfterSuccessfulAuthentication()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var initiator =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        using var responder =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Responder);

        byte[] packet =
            new byte[
                16 +
                DhmpPskChaCha20Poly1305Session.Overhead];

        initiator.Protect(
            new byte[16],
            packet);

        byte[] plaintext = new byte[16];

        Assert.True(
            responder.TryDecode(
                packet,
                plaintext,
                out _));

        Assert.False(
            responder.TryDecode(
                packet,
                plaintext,
                out _));
    }

    [Fact]
    public void ReplayWindow_AllowsLimitedPacketReordering()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var initiator =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        using var responder =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Responder);

        byte[][] packets =
            Enumerable.Range(0, 3)
                .Select(i =>
                {
                    byte[] packet =
                        new byte[
                            16 +
                            DhmpPskChaCha20Poly1305Session.Overhead];

                    byte[] plaintext =
                        Enumerable.Repeat(
                            checked((byte)i),
                            16)
                        .ToArray();

                    initiator.Protect(
                        plaintext,
                        packet);

                    return packet;
                })
                .ToArray();

        byte[] output = new byte[16];

        Assert.True(
            responder.TryDecode(
                packets[2],
                output,
                out _));

        Assert.True(
            responder.TryDecode(
                packets[0],
                output,
                out _));

        Assert.True(
            responder.TryDecode(
                packets[1],
                output,
                out _));
    }

    [Fact]
    public void WrongSessionId_CannotDecrypt()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var initiator =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        using var wrongResponder =
            new DhmpPskChaCha20Poly1305Session(
                key,
                Guid.Parse("11112233-4455-6677-8899-aabbccddeeff"),
                DhmpSecurityRole.Responder);

        byte[] packet =
            new byte[
                16 +
                DhmpPskChaCha20Poly1305Session.Overhead];

        initiator.Protect(
            new byte[16],
            packet);

        Assert.False(
            wrongResponder.TryDecode(
                packet,
                new byte[16],
                out _));
    }

    [Fact]
    public void CongestionFeedback_IsAuthenticatedDirectionallyAndReplaySafe()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var initiator =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        using var responder =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Responder);

        var feedback =
            new DhmpCongestionFeedback(
                DhmpCongestionPressure.Hard,
                500,
                pendingBatches: 4,
                capacity: 4,
                lostPendingWork: 12);

        byte[] packet =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .CongestionFeedbackPacketSize];

        int written =
            responder.EncodeCongestionFeedback(
                feedback,
                packet);

        Assert.Equal(
            packet.Length,
            written);

        Assert.True(
            initiator.TryDecodeCongestionFeedback(
                packet,
                out var decoded));

        Assert.Equal(
            feedback,
            decoded);

        Assert.False(
            initiator.TryDecodeCongestionFeedback(
                packet,
                out _));
    }

    [Fact]
    public void CongestionFeedback_TamperAndWrongDirectionAreRejected()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var initiator =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        using var responder =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Responder);

        var feedback =
            new DhmpCongestionFeedback(
                DhmpCongestionPressure.Soft,
                750,
                pendingBatches: 1,
                capacity: 4,
                lostPendingWork: 3);

        byte[] packet =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .CongestionFeedbackPacketSize];

        responder.EncodeCongestionFeedback(
            feedback,
            packet);

        Assert.False(
            responder.TryDecodeCongestionFeedback(
                packet,
                out _));

        packet[35] ^= 1;

        Assert.False(
            initiator.TryDecodeCongestionFeedback(
                packet,
                out _));
    }

    [Fact]
    public void CongestionFeedback_WrongSessionCannotAuthenticate()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var responder =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Responder);

        using var wrongInitiator =
            new DhmpPskChaCha20Poly1305Session(
                key,
                Guid.Parse(
                    "11112233-4455-6677-8899-aabbccddeeff"),
                DhmpSecurityRole.Initiator);

        byte[] packet =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .CongestionFeedbackPacketSize];

        responder.EncodeCongestionFeedback(
            new DhmpCongestionFeedback(
                DhmpCongestionPressure.None,
                1000,
                0,
                1,
                0),
            packet);

        Assert.False(
            wrongInitiator
                .TryDecodeCongestionFeedback(
                    packet,
                    out _));
    }

    [Fact]
    public async Task ProtectedSender_AccountsForSecurityOverhead()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var initiator =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        using var responder =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Responder);

        var inner = new TestSender
        {
            MaximumPayloadBytes = 1408,
            Deliver = packet =>
            {
                byte[] plaintext = new byte[32];

                Assert.True(
                    responder.TryDecode(
                        packet.Span,
                        plaintext,
                        out int written));

                Assert.Equal(32, written);
                Assert.All(
                    plaintext,
                    value => Assert.Equal((byte)0x5a, value));
            }
        };

        var sender =
            new DhmpProtectedPacketSender(
                inner,
                initiator);

        Assert.Equal(
            1408 -
            DhmpPskChaCha20Poly1305Session.Overhead,
            sender.MaximumPayloadBytes);

        await sender.SendPacketAsync(
            Enumerable.Repeat((byte)0x5a, 32)
                .ToArray(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, inner.Calls);
    }

    private sealed class TestSender : IDhmpPacketSender
    {
        public int MaximumPayloadBytes { get; init; }
        public int Calls { get; private set; }
        public Action<ReadOnlyMemory<byte>>? Deliver { get; init; }

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Deliver?.Invoke(payload);
            return ValueTask.CompletedTask;
        }
    }
}
