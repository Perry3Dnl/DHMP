using System.Buffers.Binary;
using DHMP.Protocol;
using DHMP.Security;
using Xunit;

namespace DHMP.Security.Tests;

public sealed class DhmpSessionLifecycleTests
{
    private static readonly Guid SessionId = Guid.Parse("7aab93c6-22ec-4073-9183-c52b43f0cf45");
    private static DhmpPreSharedKey Key() => new(1, Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static DhmpPskChaCha20Poly1305Session Session(DhmpPreSharedKey key, DhmpSecurityRole role)
        => new(key, SessionId, role);
    private static DhmpCongestionFeedback Feedback => new(DhmpCongestionPressure.None, 1000, 0, 1, 0);
    private static DhmpPathProbeMessage Probe => new(DhmpPathProbeType.Request, 1, 1, 0, 0, 0, 0);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentEncodingUsesUniqueMonotonicCounters(bool feedback)
    {
        using var key = Key();
        using var sender = Session(key, DhmpSecurityRole.Initiator);
        using var receiver = Session(key, DhmpSecurityRole.Responder);
        byte[][] packets = new byte[512][];
        Parallel.For(0, packets.Length, i =>
        {
            var packet = new byte[feedback ? 64 : 40];
            if (feedback) sender.EncodeCongestionFeedback(Feedback, packet);
            else sender.Protect(new byte[16], packet);
            packets[i] = packet;
        });
        var ordered = packets.OrderBy(p => BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(feedback ? 24 : 0, 8))).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            Assert.Equal((ulong)i + 1, BinaryPrimitives.ReadUInt64BigEndian(ordered[i].AsSpan(feedback ? 24 : 0, 8)));
            if (feedback) Assert.True(receiver.TryDecodeCongestionFeedback(ordered[i], out _));
            else Assert.True(receiver.TryDecode(ordered[i], new byte[16], out _));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ConcurrentReplayHasExactlyOneWinner(int operation)
    {
        using var key = Key();
        using var sender = Session(key, DhmpSecurityRole.Initiator);
        using var receiver = Session(key, DhmpSecurityRole.Responder);
        byte[] packet = new byte[operation == 0 ? 40 : operation == 1 ? 64 : 80];
        if (operation == 0) sender.Protect(new byte[16], packet);
        else if (operation == 1) sender.EncodeCongestionFeedback(Feedback, packet);
        else sender.EncodePathProbe(Probe, packet);
        int winners = 0;
        Parallel.For(0, 512, iteration =>
        {
            bool accepted = operation switch
            {
                0 => receiver.TryDecode(packet, new byte[16], out _),
                1 => receiver.TryDecodeCongestionFeedback(packet, out _),
                _ => receiver.TryDecodePathProbe(packet, out _)
            };
            if (accepted) Interlocked.Increment(ref winners);
        });
        Assert.Equal(1, winners);
    }

    [Fact]
    public void SessionDisposalRacingDataAndControlAllowsOnlyCompletedOrDisposedOperations()
    {
        using var key = Key();
        using var session = Session(key, DhmpSecurityRole.Initiator);
        Parallel.For(0, 1000, i =>
        {
            try
            {
                switch (i % 5)
                {
                    case 0: session.Protect(new byte[16], new byte[40]); break;
                    case 1: session.EncodeCongestionFeedback(Feedback, new byte[64]); break;
                    case 2: session.EncodePathProbe(Probe, new byte[80]); break;
                    case 3: session.TryDecode(new byte[40], new byte[16], out _); break;
                    case 4: session.Dispose(); break;
                }
            }
            catch (ObjectDisposedException) { }
        });
        Assert.Throws<ObjectDisposedException>(() => session.Protect(new byte[16], new byte[40]));
        Assert.Throws<ObjectDisposedException>(() => session.TryDecodeCongestionFeedback(new byte[64], out _));
        Assert.Throws<ObjectDisposedException>(() => session.TryDecodePathProbe(new byte[80], out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SenderRetirementWaitsForBackendAndDoesNotOwnSessionOrBackend(bool fail)
    {
        using var key = Key();
        using var session = Session(key, DhmpSecurityRole.Initiator);
        var backend = new BlockedSender();
        var sender = new DhmpProtectedPacketSender(backend, session);
        Task sending = sender.SendPacketAsync(new byte[16], TestContext.Current.CancellationToken).AsTask();
        await backend.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task retired = sender.DisposeAsync().AsTask();
        Assert.False(retired.IsCompleted);
        Assert.Same(retired, sender.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await sender.SendPacketAsync(new byte[16], TestContext.Current.CancellationToken));
        Assert.Equal(1, backend.Calls);
        if (fail) backend.Release.SetException(new IOException("backend failed"));
        else backend.Release.SetResult();
        if (fail) await Assert.ThrowsAsync<IOException>(async () => await sending);
        else await sending.WaitAsync(TestContext.Current.CancellationToken);
        await retired.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(backend.Disposed);
        Assert.Equal(40, session.Protect(new byte[16], new byte[40]));
    }

    [Fact]
    public async Task CancelledSendReleasesRetirementLease()
    {
        using var key = Key();
        using var session = Session(key, DhmpSecurityRole.Initiator);
        var backend = new BlockedSender();
        var sender = new DhmpProtectedPacketSender(backend, session);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task sending = sender.SendPacketAsync(new byte[16], cancellation.Token).AsTask();
        await backend.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task retired = sender.DisposeAsync().AsTask();
        Assert.False(retired.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sending);
        await retired.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task CancelledRetirementWaitDoesNotAuthorizeEarlyCleanup()
    {
        using var key = Key();
        using var session = Session(key, DhmpSecurityRole.Initiator);
        var backend = new BlockedSender();
        var sender = new DhmpProtectedPacketSender(backend, session);
        Task sending = sender.SendPacketAsync(new byte[16], TestContext.Current.CancellationToken).AsTask();
        await backend.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task retired = sender.DisposeAsync().AsTask();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await retired.WaitAsync(cancellation.Token));
        Assert.False(retired.IsCompleted);
        backend.Release.SetResult();
        await sending.WaitAsync(TestContext.Current.CancellationToken);
        await retired.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RetirementWaitsForEveryAdmittedSend()
    {
        using var key = Key();
        using var session = Session(key, DhmpSecurityRole.Initiator);
        var backend = new MultipleSender();
        var sender = new DhmpProtectedPacketSender(backend, session);
        Task first = sender.SendPacketAsync(new byte[16], TestContext.Current.CancellationToken).AsTask();
        Task second = sender.SendPacketAsync(new byte[16], TestContext.Current.CancellationToken).AsTask();
        Task retired = sender.DisposeAsync().AsTask();
        backend.First.SetResult();
        await first.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(retired.IsCompleted);
        backend.Second.SetResult();
        await second.WaitAsync(TestContext.Current.CancellationToken);
        await retired.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvalidAndAlreadyCancelledSendsDoNotLeakLeasesOrSubmitPackets()
    {
        using var key = Key();
        using var session = Session(key, DhmpSecurityRole.Initiator);
        var backend = new BlockedSender();
        var sender = new DhmpProtectedPacketSender(backend, session);
        await Assert.ThrowsAsync<DhmpProtocolException>(async () => await sender.SendPacketAsync(ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sender.SendPacketAsync(new byte[16], cancellation.Token));
        await sender.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, backend.Calls);
    }

    private sealed class MultipleSender : IDhmpPacketSender
    {
        private int _calls;
        public int MaximumPayloadBytes => 128;
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask SendPacketAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            TaskCompletionSource completion = Interlocked.Increment(ref _calls) == 1 ? First : Second;
            await completion.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class BlockedSender : IDhmpPacketSender, IDisposable
    {
        public int MaximumPayloadBytes => 128;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }
        public async ValueTask SendPacketAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Calls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
        public void Dispose() => Disposed = true;
    }
}
