using System.Security.Cryptography;
using DHMP.Protocol;
using Xunit;

namespace DHMP.Security.Tests;

public sealed class DhmpSecurityHardeningTests
{
    private static readonly Guid SessionId =
        Guid.Parse(
            "abcdef01-2345-6789-abcd-ef0123456789");

    [Fact]
    public void HappyFlow_PreSharedKey_CopiesCallerOwnedKeyBytes()
    {
        byte[] original = KeyBytes();
        byte[] source = original.ToArray();

        using var initiatorKey =
            new DhmpPreSharedKey(
                1,
                source);

        source.AsSpan().Fill(0);

        using var responderKey =
            new DhmpPreSharedKey(
                1,
                original);

        using var initiator =
            new DhmpPskChaCha20Poly1305Session(
                initiatorKey,
                SessionId,
                DhmpSecurityRole.Initiator);

        using var responder =
            new DhmpPskChaCha20Poly1305Session(
                responderKey,
                SessionId,
                DhmpSecurityRole.Responder);

        byte[] packet =
            new byte[
                16 +
                DhmpPskChaCha20Poly1305Session.Overhead];

        initiator.Protect(
            new byte[16],
            packet);

        Assert.True(
            responder.TryDecode(
                packet,
                new byte[16],
                out int written));

        Assert.Equal(16, written);
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(1, 0)]
    [InlineData(1, 31)]
    [InlineData(1, 33)]
    public void CriticalFlow_PreSharedKey_InvalidIdentityOrLengthIsRejected(
        uint keyId,
        int keyLength)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new DhmpPreSharedKey(
                keyId,
                new byte[keyLength]));
    }

    [Fact]
    public void CriticalFlow_DisposedPreSharedKeyCannotBeReused()
    {
        var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        key.Dispose();
        key.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator));

        Assert.Throws<ObjectDisposedException>(() =>
            DhmpSecurityControlCodec.Encode(
                ValidOffer(),
                key,
                new byte[
                    DhmpSecurityControlCodec.PacketSize]));
    }

    [Fact]
    public void CriticalFlow_SecurityControlCodec_RejectsShortDestinationAndWrongKeyId()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        Assert.Throws<ArgumentException>(() =>
            DhmpSecurityControlCodec.Encode(
                ValidOffer(),
                key,
                new byte[
                    DhmpSecurityControlCodec.PacketSize - 1]));

        var wrongKeyId =
            new DhmpSecurityControlMessage(
                DhmpSecurityControlType.Offer,
                DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256,
                SessionId,
                keyId: 2,
                correlationId: 1);

        Assert.Throws<ArgumentException>(() =>
            DhmpSecurityControlCodec.Encode(
                wrongKeyId,
                key,
                new byte[
                    DhmpSecurityControlCodec.PacketSize]));
    }

    [Theory]
    [InlineData(5, 99)]
    [InlineData(6, 99)]
    [InlineData(7, 99)]
    public void CriticalFlow_SecurityControlCodec_RejectsAuthenticatedInvalidEnums(
        int offset,
        byte invalidValue)
    {
        byte[] rawKey =
            KeyBytes();

        using var key =
            new DhmpPreSharedKey(
                1,
                rawKey);

        byte[] packet =
            new byte[
                DhmpSecurityControlCodec.PacketSize];

        DhmpSecurityControlCodec.Encode(
            ValidOffer(),
            key,
            packet);

        packet[offset] =
            invalidValue;

        RetagSecurityControl(
            packet,
            rawKey);

        Assert.False(
            DhmpSecurityControlCodec.TryDecode(
                packet,
                key,
                out _));
    }

    [Fact]
    public void CriticalFlow_SecurityControlCodec_RejectsAuthenticatedEmptySessionAndCorrelation()
    {
        byte[] rawKey =
            KeyBytes();

        using var key =
            new DhmpPreSharedKey(
                1,
                rawKey);

        byte[] packet =
            new byte[
                DhmpSecurityControlCodec.PacketSize];

        DhmpSecurityControlCodec.Encode(
            ValidOffer(),
            key,
            packet);

        packet.AsSpan(8, 16).Clear();

        RetagSecurityControl(
            packet,
            rawKey);

        Assert.False(
            DhmpSecurityControlCodec.TryDecode(
                packet,
                key,
                out _));

        DhmpSecurityControlCodec.Encode(
            ValidOffer(),
            key,
            packet);

        packet.AsSpan(28, 4).Clear();

        RetagSecurityControl(
            packet,
            rawKey);

        Assert.False(
            DhmpSecurityControlCodec.TryDecode(
                packet,
                key,
                out _));
    }

    [Fact]
    public void CriticalFlow_ProtectedSession_ValidatesBufferBoundaries()
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

        Assert.Throws<ArgumentException>(() =>
            initiator.Protect(
                ReadOnlySpan<byte>.Empty,
                new byte[64]));

        Assert.Throws<ArgumentException>(() =>
            initiator.Protect(
                new byte[32],
                new byte[
                    32 +
                    DhmpPskChaCha20Poly1305Session.Overhead -
                    1]));

        Assert.False(
            responder.TryDecode(
                new byte[
                    DhmpPskChaCha20Poly1305Session.Overhead],
                new byte[32],
                out int shortBytes));

        Assert.Equal(0, shortBytes);

        byte[] packet =
            new byte[
                32 +
                DhmpPskChaCha20Poly1305Session.Overhead];

        initiator.Protect(
            new byte[32],
            packet);

        Assert.Throws<ArgumentException>(() =>
            responder.TryDecode(
                packet,
                new byte[31],
                out _));
    }

    [Fact]
    public void CriticalFlow_ReplayWindowRejectsAuthenticatedPacketOlderThan64CounterWindow()
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
            Enumerable.Range(0, 66)
                .Select(_ =>
                {
                    byte[] packet =
                        new byte[
                            8 +
                            DhmpPskChaCha20Poly1305Session.Overhead];

                    initiator.Protect(
                        new byte[8],
                        packet);

                    return packet;
                })
                .ToArray();

        byte[] plaintext =
            new byte[8];

        Assert.True(
            responder.TryDecode(
                packets[^1],
                plaintext,
                out _));

        Assert.False(
            responder.TryDecode(
                packets[0],
                plaintext,
                out _));

        var snapshot =
            responder.GetReceiveSnapshot();

        Assert.Equal(1, snapshot.ReplayRejectedPackets);
        Assert.Equal(66UL, snapshot.HighestPacketCounter);
        Assert.Equal(64, snapshot.WindowSpan);
    }

    [Fact]
    public void CriticalFlow_TamperedCongestionFeedbackDoesNotConsumeReplaySequence()
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

        using var initiator =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        byte[] original =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .CongestionFeedbackPacketSize];

        responder.EncodeCongestionFeedback(
            new DhmpCongestionFeedback(
                DhmpCongestionPressure.Soft,
                750,
                1,
                4,
                1),
            original);

        byte[] tampered =
            original.ToArray();

        tampered[32] ^= 1;

        Assert.False(
            initiator.TryDecodeCongestionFeedback(
                tampered,
                out _));

        Assert.True(
            initiator.TryDecodeCongestionFeedback(
                original,
                out var feedback));

        Assert.Equal(
            DhmpCongestionPressure.Soft,
            feedback.Pressure);
    }

    [Fact]
    public void CriticalFlow_PathRequestAndResponseUseSeparateReplayDomains()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var sender =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Responder);

        using var receiver =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        const ulong probeId = 42;

        byte[] request =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .PathProbePacketSize];

        byte[] response =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .PathProbePacketSize];

        sender.EncodePathProbe(
            new DhmpPathProbeMessage(
                DhmpPathProbeType.Request,
                probeId,
                123,
                0,
                0,
                0,
                0),
            request);

        sender.EncodePathProbe(
            new DhmpPathProbeMessage(
                DhmpPathProbeType.Response,
                probeId,
                123,
                10,
                10,
                1,
                9),
            response);

        Assert.True(
            receiver.TryDecodePathProbe(
                request,
                out var decodedRequest));

        Assert.True(
            receiver.TryDecodePathProbe(
                response,
                out var decodedResponse));

        Assert.Equal(
            DhmpPathProbeType.Request,
            decodedRequest.Type);

        Assert.Equal(
            DhmpPathProbeType.Response,
            decodedResponse.Type);

        Assert.False(
            receiver.TryDecodePathProbe(
                request,
                out _));

        Assert.False(
            receiver.TryDecodePathProbe(
                response,
                out _));
    }

    [Fact]
    public void CriticalFlow_DisposedSessionRejectsDataAndControlOperations()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        var session =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        session.Dispose();
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            session.Protect(
                new byte[1],
                new byte[
                    1 +
                    DhmpPskChaCha20Poly1305Session.Overhead]));

        Assert.Throws<ObjectDisposedException>(() =>
            session.TryDecodeCongestionFeedback(
                new byte[
                    DhmpPskChaCha20Poly1305Session
                        .CongestionFeedbackPacketSize],
                out _));

        Assert.Throws<ObjectDisposedException>(() =>
            session.EncodePathProbe(
                new DhmpPathProbeMessage(
                    DhmpPathProbeType.Request,
                    1,
                    1,
                    0,
                    0,
                    0,
                    0),
                new byte[
                    DhmpPskChaCha20Poly1305Session
                        .PathProbePacketSize]));
    }

    [Fact]
    public async Task HappyFlow_ProtectedSender_PropagatesExactlyOneProtectedPacket()
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

        byte[]? delivered = null;

        var inner =
            new RecordingSender(
                maximumPayloadBytes: 128,
                send: packet =>
                {
                    delivered =
                        packet.ToArray();

                    return ValueTask.CompletedTask;
                });

        var sender =
            new DhmpProtectedPacketSender(
                inner,
                initiator);

        byte[] payload =
            Enumerable.Range(0, 32)
                .Select(i => (byte)i)
                .ToArray();

        await sender.SendPacketAsync(
            payload,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, inner.Calls);
        Assert.NotNull(delivered);
        Assert.Equal(
            payload.Length +
            DhmpPskChaCha20Poly1305Session.Overhead,
            delivered!.Length);

        byte[] decoded =
            new byte[payload.Length];

        Assert.True(
            responder.TryDecode(
                delivered,
                decoded,
                out int decodedBytes));

        Assert.Equal(
            payload.Length,
            decodedBytes);

        Assert.Equal(
            payload,
            decoded);
    }

    [Fact]
    public async Task CriticalFlow_ProtectedSender_CancellationAndInnerFailureDoNotRetry()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var session =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        var failing =
            new RecordingSender(
                maximumPayloadBytes: 128,
                _ => ValueTask.FromException(
                    new IOException(
                        "send failed")));

        var sender =
            new DhmpProtectedPacketSender(
                failing,
                session);

        using var cancelled =
            new CancellationTokenSource();

        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () =>
                await sender.SendPacketAsync(
                    new byte[16],
                    cancelled.Token));

        Assert.Equal(0, failing.Calls);

        var error =
            await Assert.ThrowsAsync<IOException>(
                async () =>
                    await sender.SendPacketAsync(
                        new byte[16],
                        TestContext.Current.CancellationToken));

        Assert.Equal(
            "send failed",
            error.Message);

        Assert.Equal(1, failing.Calls);
    }

    [Fact]
    public void CriticalFlow_ProtectedSender_RejectsImpossibleBackendAndPayloadBounds()
    {
        using var key =
            new DhmpPreSharedKey(
                1,
                KeyBytes());

        using var session =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        Assert.Throws<ArgumentException>(() =>
            new DhmpProtectedPacketSender(
                new RecordingSender(
                    DhmpPskChaCha20Poly1305Session.Overhead,
                    _ => ValueTask.CompletedTask),
                session));

        var sender =
            new DhmpProtectedPacketSender(
                new RecordingSender(
                    64,
                    _ => ValueTask.CompletedTask),
                session);

        Assert.Throws<DhmpProtocolException>(() =>
            sender.SendPacketAsync(
                ReadOnlyMemory<byte>.Empty));

        Assert.Throws<DhmpProtocolException>(() =>
            sender.SendPacketAsync(
                new byte[
                    sender.MaximumPayloadBytes + 1]));
    }

    private static DhmpSecurityControlMessage ValidOffer()
        => new(
            DhmpSecurityControlType.Offer,
            DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256,
            SessionId,
            keyId: 1,
            correlationId: 1);

    private static byte[] KeyBytes()
        => Enumerable.Range(
                1,
                DhmpPreSharedKey.KeySizeBytes)
            .Select(i => checked((byte)i))
            .ToArray();

    private static void RetagSecurityControl(
        byte[] packet,
        byte[] key)
    {
        Span<byte> fullTag =
            stackalloc byte[32];

        HMACSHA256.HashData(
            key,
            packet.AsSpan(
                0,
                DhmpSecurityControlCodec.BodySize),
            fullTag);

        fullTag[
            ..DhmpSecurityControlCodec.TagSize]
            .CopyTo(
                packet.AsSpan(
                    DhmpSecurityControlCodec.BodySize,
                    DhmpSecurityControlCodec.TagSize));
    }

    private sealed class RecordingSender :
        IDhmpPacketSender
    {
        private readonly Func<
            ReadOnlyMemory<byte>,
            ValueTask> _send;

        public RecordingSender(
            int maximumPayloadBytes,
            Func<
                ReadOnlyMemory<byte>,
                ValueTask> send)
        {
            MaximumPayloadBytes =
                maximumPayloadBytes;
            _send = send;
        }

        public int MaximumPayloadBytes { get; }

        public int Calls { get; private set; }

        public async ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            Calls++;

            await _send(payload)
                .ConfigureAwait(false);
        }
    }
}
