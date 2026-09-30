using System.Net;
using System.Threading.Channels;
using DHMP.Protocol;
using DHMP.RawIpv6;
using DHMP.Security;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpHandshakeLifecycleTests
{
    private static readonly DhmpWireContract Wire = new(32);
    private static readonly DhmpSendPolicy Send = new(1000, 128);
    private static readonly DhmpReceivePolicy Receive = new(DhmpProcessingMode.Sequential, 128);
    private static readonly Guid Schema = Guid.Parse("f5bc84bc-6e10-4851-99c3-befabdcf4531");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public void TimeoutConfiguration_IsFiniteAndPropagatesFromPathBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), Options().HandshakeTimeout);
        foreach (TimeSpan invalid in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1),
                     System.Threading.Timeout.InfiniteTimeSpan, TimeSpan.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => Options(invalid));

        var configured = DhmpRawIpv6Options.FromPathMtu(IPAddress.IPv6Loopback,
            IPAddress.IPv6Loopback, 1280, handshakeTimeout: TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(3), configured.HandshakeTimeout);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task PreCancelledPublicCall_DoesNotOpenRawSocket(int kind)
    {
        using var key = Key();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            switch (kind)
            {
                case 0:
                    await DhmpRawIpv6Handshake.InitiateAsync(Options(), Wire, Send, Receive, Schema, cancellation.Token);
                    break;
                case 1:
                    await DhmpRawIpv6Handshake.RespondOnceAsync(Options(), Wire, Send, Receive, Schema, cancellation.Token);
                    break;
                case 2:
                    using (await DhmpRawIpv6SecurityHandshake.InitiateAsync(Options(), key, cancellation.Token)) { }
                    break;
                default:
                    using (await DhmpRawIpv6SecurityHandshake.RespondOnceAsync(Options(), key, cancellation.Token)) { }
                    break;
            }
        });
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MissingResponse_ExpiresAtTotalDeadlineAndDisposesChannel(int kind)
    {
        using var key = Key();
        var channel = new PacketChannel();
        var clock = new ManualClock();
        Task<object> run = Start(kind, channel, clock, key, TestContext.Current.CancellationToken);
        await channel.Receiving.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(run.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.Equal(1, channel.DisposeCount);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Equal(kind is 0 or 2 ? 1 : 0, channel.Sent.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CancellationDuringReceive_RemainsCancellationAndDisposesChannel(int kind)
    {
        using var key = Key();
        using var cancellation = new CancellationTokenSource();
        var channel = new PacketChannel();
        var clock = new ManualClock();
        Task<object> run = Start(kind, channel, clock, key, cancellation.Token);
        await channel.Receiving.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, channel.DisposeCount);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task BlockedSend_UsesSameDeadlineAsReceive(int kind)
    {
        using var key = Key();
        var channel = new PacketChannel { BlockSend = true };
        if (kind == 1)
            channel.Enqueue(Control(DhmpControlMessage.Hello(Profile(), 17)));
        if (kind == 3)
            channel.Enqueue(Security(Offer(), key));

        var clock = new ManualClock();
        Task<object> run = Start(kind, channel, clock, key, TestContext.Current.CancellationToken);
        await channel.Sending.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Fact]
    public async Task OtherCorrelation_DoesNotResetDeadline()
    {
        var clock = new ManualClock();
        var channel = new PacketChannel();
        channel.OnSend = packet =>
        {
            Assert.True(DhmpControlCodec.TryDecode(packet.Span, out var hello));
            clock.Advance(TimeSpan.FromSeconds(9));
            channel.Enqueue(Control(DhmpControlMessage.Accept(Profile(), hello.CorrelationId + 1)));
        };
        Task<DhmpNegotiatedPeer> run = DhmpRawIpv6Handshake.InitiateCoreAsync(
            Options(), Wire, Send, Receive, Schema, () => channel, clock, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.Single(channel.Sent);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task SendFailure_PropagatesWithoutRetryAndDisposesChannel(int kind)
    {
        using var key = Key();
        var failure = new IOException("Injected control sender failure.");
        var channel = new PacketChannel { SendFailure = failure };
        if (kind == 1) channel.Enqueue(Control(DhmpControlMessage.Hello(Profile(), 42)));
        if (kind == 3) channel.Enqueue(Security(Offer(), key));
        var clock = new ManualClock();
        var error = await Assert.ThrowsAsync<IOException>(() => Start(kind, channel, clock, key, TestContext.Current.CancellationToken));
        Assert.Same(failure, error);
        Assert.Single(channel.Sent);
        Assert.Equal(1, channel.DisposeCount);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CancellationDuringSend_DoesNotCreateSessionOrRetry(int kind)
    {
        using var key = Key();
        using var cancellation = new CancellationTokenSource();
        var channel = new PacketChannel { BlockSend = true };
        if (kind == 1) channel.Enqueue(Control(DhmpControlMessage.Hello(Profile(), 42)));
        if (kind == 3) channel.Enqueue(Security(Offer(), key));
        Task<object> run = Start(kind, channel, new ManualClock(), key, cancellation.Token);
        await channel.Sending.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Single(channel.Sent);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ModifiedSecurityTag_DoesNotActivateSession(int kind)
    {
        using var key = Key();
        var channel = new PacketChannel();
        if (kind == 3)
        {
            byte[] invalid = Security(Offer(), key);
            invalid[^1] ^= 1;
            channel.Enqueue(invalid);
        }
        else
        {
            channel.OnSend = packet =>
            {
                Assert.True(DhmpSecuritySetupCodec.TryDecode(packet.Span, key, out var offer));
                byte[] invalid = Security(new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Challenge,
                    offer.InitiatorNonce, Guid.NewGuid(), offer.KeyId, offer.CorrelationId), key);
                invalid[^1] ^= 1;
                channel.Enqueue(invalid);
            };
        }
        await Assert.ThrowsAsync<DhmpSecurityException>(() => Start(kind, channel, new ManualClock(), key, TestContext.Current.CancellationToken));
        Assert.Equal(kind == 2 ? 1 : 0, channel.Sent.Count);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Fact]
    public async Task CompatibilityInitiator_IgnoresOtherCorrelationAndClampsRemoteCeiling()
    {
        var channel = new PacketChannel();
        channel.OnSend = packet =>
        {
            Assert.True(DhmpControlCodec.TryDecode(packet.Span, out var hello));
            var remote = new DhmpPeerProfile(Wire, 95, Schema);
            channel.Enqueue(Control(DhmpControlMessage.Accept(remote, hello.CorrelationId + 1)));
            channel.Enqueue(Control(DhmpControlMessage.Accept(remote, hello.CorrelationId)));
        };
        var clock = new ManualClock();
        var peer = await DhmpRawIpv6Handshake.InitiateCoreAsync(
            Options(), Wire, Send, Receive, Schema, () => channel, clock, TestContext.Current.CancellationToken);

        Assert.Equal(64, peer.EffectiveSendPolicy.MaximumPayloadBytes);
        Assert.Equal(Send.Pmax, peer.EffectiveSendPolicy.Pmax);
        Assert.Single(channel.Sent);
        Assert.Equal(1, channel.DisposeCount);
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task CompatibilityResponder_SkipsNonHelloAndEchoesCorrelation()
    {
        var channel = new PacketChannel();
        channel.Enqueue(Control(DhmpControlMessage.Accept(Profile(), 12)));
        channel.Enqueue(Control(DhmpControlMessage.Hello(Profile(), 42)));
        await DhmpRawIpv6Handshake.RespondCoreAsync(Options(), Wire, Send, Receive, Schema,
            () => channel, new ManualClock(), TestContext.Current.CancellationToken);

        Assert.True(DhmpControlCodec.TryDecode(Assert.Single(channel.Sent), out var accept));
        Assert.Equal(DhmpControlMessageType.Accept, accept.Type);
        Assert.Equal(42u, accept.CorrelationId);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Fact]
    public async Task CompatibilityReject_IsSentAndDoesNotActivatePeer()
    {
        var channel = new PacketChannel();
        channel.Enqueue(Control(DhmpControlMessage.Hello(
            new DhmpPeerProfile(Wire, 128, Guid.NewGuid()), 42)));
        var error = await Assert.ThrowsAsync<DhmpNegotiationException>(() =>
            DhmpRawIpv6Handshake.RespondCoreAsync(Options(), Wire, Send, Receive, Schema,
                () => channel, new ManualClock(), TestContext.Current.CancellationToken));

        Assert.Equal(DhmpControlRejectReason.SchemaMismatch, error.Reason);
        Assert.True(DhmpControlCodec.TryDecode(Assert.Single(channel.Sent), out var reject));
        Assert.Equal(DhmpControlMessageType.Reject, reject.Type);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task MalformedControl_FailsClosedAndDisposesChannel(int kind)
    {
        using var key = Key();
        var channel = new PacketChannel();
        channel.Enqueue(new byte[] { 1 });
        Task<object> run = Start(kind, channel, new ManualClock(), key, TestContext.Current.CancellationToken);

        if (kind < 2)
            await Assert.ThrowsAsync<DhmpProtocolException>(() => run);
        else
            await Assert.ThrowsAsync<DhmpSecurityException>(() => run);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedResponse_WrongSessionOrCorrelationDoesNotActivateSecurity(bool wrongSession)
    {
        using var key = Key();
        var channel = new PacketChannel();
        channel.OnSend = packet =>
        {
            Assert.True(DhmpSecuritySetupCodec.TryDecode(packet.Span, key, out var offer));
            channel.Enqueue(Security(new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Challenge,
                wrongSession ? Guid.NewGuid() : offer.InitiatorNonce, Guid.NewGuid(),
                offer.KeyId, wrongSession ? offer.CorrelationId : offer.CorrelationId + 1), key));
        };

        await Assert.ThrowsAsync<DhmpSecurityException>(() =>
            DhmpRawIpv6SecurityHandshake.InitiateCoreAsync(Options(), key,
                () => channel, new ManualClock(), TestContext.Current.CancellationToken));
        Assert.Equal(1, channel.DisposeCount);
    }

    [Fact]
    public async Task PskExchange_CreatesMatchingDirectionalSessionsAndFreshAttemptIds()
    {
        using var key = Key();
        Guid? previous = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var initiatorChannel = new PacketChannel();
            var responderChannel = new PacketChannel();
            initiatorChannel.OnSend = packet => responderChannel.Enqueue(packet.ToArray());
            responderChannel.OnSend = packet => initiatorChannel.Enqueue(packet.ToArray());

            Task<DhmpPskChaCha20Poly1305Session> response = DhmpRawIpv6SecurityHandshake.RespondCoreAsync(
                Options(), key, () => responderChannel, new ManualClock(), TestContext.Current.CancellationToken);
            using var initiator = await DhmpRawIpv6SecurityHandshake.InitiateCoreAsync(
                Options(), key, () => initiatorChannel, new ManualClock(), TestContext.Current.CancellationToken);
            using var responder = await response.WaitAsync(Guard, TestContext.Current.CancellationToken);

            Assert.Equal(initiator.SessionId, responder.SessionId);
            Assert.NotEqual(previous, initiator.SessionId);
            previous = initiator.SessionId;
            byte[] plaintext = { 1, 2, 3 };
            byte[] protectedPacket = new byte[plaintext.Length + DhmpPskChaCha20Poly1305Session.Overhead];
            byte[] decoded = new byte[plaintext.Length];
            Assert.Equal(protectedPacket.Length, initiator.Protect(plaintext, protectedPacket));
            Assert.True(responder.TryDecode(protectedPacket, decoded, out int received));
            Assert.Equal(plaintext.Length, received);
            Assert.Equal(plaintext, decoded);
            Assert.Equal(1, initiatorChannel.DisposeCount);
            Assert.Equal(1, responderChannel.DisposeCount);
        }
    }

    [Fact]
    public async Task CapturedOfferAndConfirm_CannotRecreateSessionInNewResponderInstances()
    {
        using var key = Key();
        var captured = await CaptureExchange(key);
        Assert.True(DhmpSecuritySetupCodec.TryDecode(captured.Challenge, key, out var oldChallenge));
        Guid? previousChallenge = null;

        for (int restart = 0; restart < 2; restart++)
        {
            var channel = new PacketChannel();
            channel.Enqueue(captured.Offer);
            channel.OnSend = packet =>
            {
                Assert.True(DhmpSecuritySetupCodec.TryDecode(packet.Span, key, out var fresh));
                Assert.Equal(DhmpSecuritySetupType.Challenge, fresh.Type);
                Assert.NotEqual(oldChallenge.ResponderNonce, fresh.ResponderNonce);
                Assert.NotEqual(previousChallenge, fresh.ResponderNonce);
                previousChallenge = fresh.ResponderNonce;
                channel.Enqueue(captured.Confirm);
            };
            await Assert.ThrowsAsync<DhmpSecurityException>(() =>
                DhmpRawIpv6SecurityHandshake.RespondCoreAsync(Options(), key,
                    () => channel, new ManualClock(), TestContext.Current.CancellationToken));
            Assert.Single(channel.Sent); // No ACCEPT and no activated responder session.
            Assert.Equal(1, channel.DisposeCount);
        }
    }

    [Fact]
    public async Task CapturedOfferAlone_OnlyIssuesFreshChallengeAndExpires()
    {
        using var key = Key();
        var channel = new PacketChannel();
        channel.Enqueue(Security(Offer(), key));
        var clock = new ManualClock();
        Task<DhmpPskChaCha20Poly1305Session> run = DhmpRawIpv6SecurityHandshake.RespondCoreAsync(
            Options(), key, () => channel, clock, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        Assert.Single(channel.Sent);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.Single(channel.Sent);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Fact]
    public async Task SameOfferWithFreshLiveConfirmation_UsesDifferentKeysAndRejectsOldData()
    {
        using var key = Key();
        var captured = await CaptureExchange(key);
        var channel = new PacketChannel();
        channel.Enqueue(captured.Offer);
        channel.OnSend = packet =>
        {
            Assert.True(DhmpSecuritySetupCodec.TryDecode(packet.Span, key, out var message));
            if (message.Type == DhmpSecuritySetupType.Challenge)
                channel.Enqueue(Security(message.WithType(DhmpSecuritySetupType.Confirm), key));
        };
        using var freshResponder = await DhmpRawIpv6SecurityHandshake.RespondCoreAsync(
            Options(), key, () => channel, new ManualClock(), TestContext.Current.CancellationToken);
        Assert.NotEqual(captured.SessionId, freshResponder.SessionId);
        Assert.False(freshResponder.TryDecode(captured.Data, new byte[3], out _));
        using var freshInitiator = new DhmpPskChaCha20Poly1305Session(
            key, freshResponder.SessionId, DhmpSecurityRole.Initiator);
        byte[] newData = new byte[captured.Data.Length];
        freshInitiator.Protect(new byte[] { 1, 2, 3 }, newData);
        Assert.False(captured.Data.AsSpan().SequenceEqual(newData));
        Assert.True(freshResponder.TryDecode(newData, new byte[3], out int bytes));
        Assert.Equal(3, bytes);
        Assert.Equal(2, channel.Sent.Count);
    }

    [Theory]
    [InlineData(DhmpSecuritySetupType.Challenge)]
    [InlineData(DhmpSecuritySetupType.Accept)]
    public async Task ReflectedResponse_IsNotInitiatorConfirmation(DhmpSecuritySetupType reflectedType)
    {
        using var key = Key();
        var channel = new PacketChannel();
        channel.Enqueue(Security(Offer(), key));
        channel.OnSend = packet =>
        {
            Assert.True(DhmpSecuritySetupCodec.TryDecode(packet.Span, key, out var challenge));
            channel.Enqueue(Security(challenge.WithType(reflectedType), key));
        };
        await Assert.ThrowsAsync<DhmpSecurityException>(() => DhmpRawIpv6SecurityHandshake.RespondCoreAsync(
            Options(), key, () => channel, new ManualClock(), TestContext.Current.CancellationToken));
        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task CapturedFinalAccept_DoesNotCompleteNewInitiatorAttempt()
    {
        using var key = Key();
        var captured = await CaptureExchange(key);
        var channel = new PacketChannel();
        channel.OnSend = packet =>
        {
            Assert.True(DhmpSecuritySetupCodec.TryDecode(packet.Span, key, out var message));
            if (message.Type == DhmpSecuritySetupType.Offer)
                channel.Enqueue(Security(new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Challenge,
                    message.InitiatorNonce, Guid.NewGuid(), message.KeyId, message.CorrelationId), key));
            else channel.Enqueue(captured.Accept);
        };
        await Assert.ThrowsAsync<DhmpSecurityException>(() => DhmpRawIpv6SecurityHandshake.InitiateCoreAsync(
            Options(), key, () => channel, new ManualClock(), TestContext.Current.CancellationToken));
        Assert.Equal(2, channel.Sent.Count);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Fact]
    public async Task LostFinalAccept_DoesNotReturnInitiatorSession()
    {
        using var key = Key();
        var channel = new PacketChannel();
        channel.OnSend = packet =>
        {
            Assert.True(DhmpSecuritySetupCodec.TryDecode(packet.Span, key, out var message));
            if (message.Type == DhmpSecuritySetupType.Offer)
                channel.Enqueue(Security(new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Challenge,
                    message.InitiatorNonce, Guid.NewGuid(), message.KeyId, message.CorrelationId), key));
        };
        var clock = new ManualClock();
        Task<DhmpPskChaCha20Poly1305Session> run = DhmpRawIpv6SecurityHandshake.InitiateCoreAsync(
            Options(), key, () => channel, clock, TestContext.Current.CancellationToken);
        Assert.Equal(2, channel.Sent.Count);
        Assert.False(run.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task LastSendBlocked_StillHonorsDeadlineOrCancellation(int kind, bool cancel)
    {
        using var key = Key();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var channel = new PacketChannel { BlockSend = true, BlockOnSendNumber = 2 };
        if (kind == 3) channel.Enqueue(Security(Offer(), key));
        channel.OnSend = packet =>
        {
            Assert.True(DhmpSecuritySetupCodec.TryDecode(packet.Span, key, out var message));
            channel.Enqueue(Security(kind == 2
                ? new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Challenge, message.InitiatorNonce,
                    Guid.NewGuid(), message.KeyId, message.CorrelationId)
                : message.WithType(DhmpSecuritySetupType.Confirm), key));
        };
        var clock = new ManualClock();
        Task<object> run = Start(kind, channel, clock, key, cancellation.Token);
        await channel.SecondSending.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAsync<TimeoutException>(() => run);
        }
        Assert.Equal(2, channel.Sent.Count);
        Assert.Equal(1, channel.DisposeCount);
    }

    private sealed record CapturedExchange(byte[] Offer, byte[] Challenge, byte[] Confirm,
        byte[] Accept, Guid SessionId, byte[] Data);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LegacyV1Control_DoesNotDowngradeRawHandshake(int kind)
    {
        using var key = Key();
        byte[] legacy = new byte[DhmpSecurityControlCodec.PacketSize];
        DhmpSecurityControlCodec.Encode(new DhmpSecurityControlMessage(DhmpSecurityControlType.Offer,
            DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256, Guid.NewGuid(), 7, 42), key, legacy);
        var channel = new PacketChannel();
        channel.Enqueue(legacy);
        await Assert.ThrowsAsync<DhmpSecurityException>(() => Start(
            kind, channel, new ManualClock(), key, TestContext.Current.CancellationToken));
        Assert.Equal(kind == 2 ? 1 : 0, channel.Sent.Count);
        Assert.Equal(1, channel.DisposeCount);
    }

    [Theory]
    [InlineData(DhmpSecuritySetupType.Accept)]
    [InlineData(DhmpSecuritySetupType.Confirm)]
    public async Task PrematureAuthenticatedAcceptOrConfirm_DoesNotReplaceChallenge(DhmpSecuritySetupType type)
    {
        using var key = Key();
        var channel = new PacketChannel();
        channel.OnSend = packet =>
        {
            Assert.True(DhmpSecuritySetupCodec.TryDecode(packet.Span, key, out var offer));
            channel.Enqueue(Security(new DhmpSecuritySetupMessage(type, offer.InitiatorNonce,
                Guid.NewGuid(), offer.KeyId, offer.CorrelationId), key));
        };
        await Assert.ThrowsAsync<DhmpSecurityException>(() => DhmpRawIpv6SecurityHandshake.InitiateCoreAsync(
            Options(), key, () => channel, new ManualClock(), TestContext.Current.CancellationToken));
        Assert.Single(channel.Sent);
        Assert.Equal(1, channel.DisposeCount);
    }

    private static async Task<CapturedExchange> CaptureExchange(DhmpPreSharedKey key)
    {
        var initiatorChannel = new PacketChannel();
        var responderChannel = new PacketChannel();
        initiatorChannel.OnSend = packet => responderChannel.Enqueue(packet.ToArray());
        responderChannel.OnSend = packet => initiatorChannel.Enqueue(packet.ToArray());
        Task<DhmpPskChaCha20Poly1305Session> response = DhmpRawIpv6SecurityHandshake.RespondCoreAsync(
            Options(), key, () => responderChannel, new ManualClock(), TestContext.Current.CancellationToken);
        using var initiator = await DhmpRawIpv6SecurityHandshake.InitiateCoreAsync(
            Options(), key, () => initiatorChannel, new ManualClock(), TestContext.Current.CancellationToken);
        using var responder = await response.WaitAsync(Guard, TestContext.Current.CancellationToken);
        byte[] data = new byte[3 + DhmpPskChaCha20Poly1305Session.Overhead];
        initiator.Protect(new byte[] { 1, 2, 3 }, data);
        return new CapturedExchange(initiatorChannel.Sent[0], responderChannel.Sent[0],
            initiatorChannel.Sent[1], responderChannel.Sent[1], initiator.SessionId, data);
    }

    private static async Task<object> Start(int kind, PacketChannel channel, ManualClock clock,
        DhmpPreSharedKey key, CancellationToken token)
        => kind switch
        {
            0 => await DhmpRawIpv6Handshake.InitiateCoreAsync(Options(), Wire, Send, Receive, Schema, () => channel, clock, token),
            1 => await DhmpRawIpv6Handshake.RespondCoreAsync(Options(), Wire, Send, Receive, Schema, () => channel, clock, token),
            2 => await DhmpRawIpv6SecurityHandshake.InitiateCoreAsync(Options(), key, () => channel, clock, token),
            _ => await DhmpRawIpv6SecurityHandshake.RespondCoreAsync(Options(), key, () => channel, clock, token)
        };

    private static DhmpRawIpv6Options Options(TimeSpan? timeout = null)
        => new(IPAddress.IPv6Loopback, IPAddress.IPv6Loopback, 128, handshakeTimeout: timeout);
    private static DhmpPreSharedKey Key() => new(7, new byte[32]);
    private static DhmpPeerProfile Profile() => new(Wire, 128, Schema);
    private static DhmpSecuritySetupMessage Offer()
        => new(DhmpSecuritySetupType.Offer, Guid.NewGuid(), Guid.Empty, 7, 42);
    private static byte[] Control(DhmpControlMessage message)
    {
        byte[] bytes = new byte[DhmpProtocol.ControlPacketSize];
        DhmpControlCodec.Encode(message, bytes);
        return bytes;
    }
    private static byte[] Security(DhmpSecuritySetupMessage message, DhmpPreSharedKey key)
    {
        byte[] bytes = new byte[DhmpSecuritySetupCodec.PacketSize];
        DhmpSecuritySetupCodec.Encode(message, key, bytes);
        return bytes;
    }

    private sealed class PacketChannel : IDhmpControlPacketChannel
    {
        private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
        public readonly List<byte[]> Sent = new();
        public readonly TaskCompletionSource Receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Sending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SecondSending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<ReadOnlyMemory<byte>>? OnSend { get; set; }
        public bool BlockSend { get; init; }
        public int BlockOnSendNumber { get; init; } = 1;
        public Exception? SendFailure { get; init; }
        public int DisposeCount { get; private set; }
        public void Enqueue(byte[] packet) => Assert.True(_incoming.Writer.TryWrite(packet));
        public async ValueTask SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Sent.Add(packet.ToArray());
            Sending.TrySetResult();
            if (Sent.Count == 2) SecondSending.TrySetResult();
            if (SendFailure is not null)
                throw SendFailure;
            if (BlockSend && Sent.Count == BlockOnSendNumber)
                await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, token);
            OnSend?.Invoke(packet);
        }
        public async ValueTask<int> ReceivePacketAsync(Memory<byte> destination, CancellationToken token)
        {
            Receiving.TrySetResult();
            byte[] packet = await _incoming.Reader.ReadAsync(token);
            packet.AsMemory().CopyTo(destination);
            return packet.Length;
        }
        public void Dispose() => DisposeCount++;
    }

    // Expiry is driven explicitly; no sleep or elapsed-wall-clock assumption in deadline tests.
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();
        private TimeSpan _elapsed;
        public int ActiveTimers => _timers.Count(timer => !timer.Disposed);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            _elapsed += duration;
            foreach (var timer in _timers.ToArray())
                if (!timer.Disposed && timer.Due <= _elapsed)
                    timer.Fire();
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public bool Disposed { get; private set; }
            public TimeSpan Due { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed) return false;
                Due = dueTime == global::System.Threading.Timeout.InfiniteTimeSpan
                    ? TimeSpan.MaxValue : clock._elapsed + dueTime;
                return true;
            }
            public void Fire()
            {
                Due = TimeSpan.MaxValue;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
