using System.Threading.Tasks.Sources;
using DHMP.Client;
using DHMP.Protocol;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpUnlimitedSendTests
{
    private static DhmpClient Create(IDhmpPacketSender sender, DhmpRatePolicy ratePolicy) =>
        new(sender, new DhmpWireContract(16), new DhmpSendPolicy(long.MaxValue, 64, ratePolicy));

    [Theory]
    [InlineData(DhmpRatePolicy.Unlimited)]
    [InlineData(DhmpRatePolicy.RejectWindow)]
    [InlineData(DhmpRatePolicy.SmoothPacing)]
    public async Task CompletedSend_ForwardsRecordAndToken_WithoutAllocation(DhmpRatePolicy ratePolicy)
    {
        var sender = new Sender();
        var client = Create(sender, ratePolicy);
        byte[] record = new byte[16];
        using var cancellation = new CancellationTokenSource();
        ValueTask send = client.SendAsync(record, cancellation.Token);
        Assert.True(send.IsCompletedSuccessfully);
        await send;
        Assert.Equal((ReadOnlyMemory<byte>)record, sender.LastRecord);
        Assert.Equal(cancellation.Token, sender.LastToken);
        for (int i = 0; i < 20_000; i++) await client.SendAsync(record, TestContext.Current.CancellationToken);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20_000; i++) await client.SendAsync(record, TestContext.Current.CancellationToken);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(DhmpRatePolicy.Unlimited)]
    [InlineData(DhmpRatePolicy.RejectWindow)]
    [InlineData(DhmpRatePolicy.SmoothPacing)]
    public async Task InvalidRecord_ReturnsFaultedValueTask_WithoutCallingSender(DhmpRatePolicy ratePolicy)
    {
        var sender = new Sender();
        var client = Create(sender, ratePolicy);
        // A synchronous throw here fails this test before the assertion on the returned operation.
        ValueTask send = client.SendAsync(new byte[15], TestContext.Current.CancellationToken);
        Assert.True(send.IsFaulted);
        await Assert.ThrowsAsync<DhmpProtocolException>(() => send.AsTask());
        Assert.Equal(0, sender.Calls);
    }

    [Theory]
    [InlineData(DhmpRatePolicy.Unlimited)]
    [InlineData(DhmpRatePolicy.RejectWindow)]
    [InlineData(DhmpRatePolicy.SmoothPacing)]
    public async Task LiveBudget_IsReadOnEverySend_AndCanRecover(DhmpRatePolicy ratePolicy)
    {
        var sender = new Sender();
        var client = Create(sender, ratePolicy);
        byte[] record = new byte[16];
        await client.SendAsync(record, TestContext.Current.CancellationToken);
        sender.CurrentMaximumPayloadBytes = 15;
        ValueTask blocked = client.SendAsync(record, TestContext.Current.CancellationToken);
        Assert.True(blocked.IsFaulted);
        await Assert.ThrowsAsync<DhmpProtocolException>(() => blocked.AsTask());
        Assert.Equal(1, sender.Calls);
        sender.CurrentMaximumPayloadBytes = 16;
        await client.SendAsync(record, TestContext.Current.CancellationToken);
        Assert.Equal(2, sender.Calls);
    }

    [Theory]
    [InlineData(DhmpRatePolicy.Unlimited, false)]
    [InlineData(DhmpRatePolicy.RejectWindow, false)]
    [InlineData(DhmpRatePolicy.SmoothPacing, false)]
    [InlineData(DhmpRatePolicy.Unlimited, true)]
    [InlineData(DhmpRatePolicy.RejectWindow, true)]
    [InlineData(DhmpRatePolicy.SmoothPacing, true)]
    public async Task BackendFailure_IsDeferred_AndPreservesException(DhmpRatePolicy ratePolicy, bool synchronousThrow)
    {
        var expected = new IOException("sender failed");
        var sender = new Sender { Send = () => synchronousThrow ? throw expected : ValueTask.FromException(expected) };
        ValueTask send = Create(sender, ratePolicy).SendAsync(new byte[16], TestContext.Current.CancellationToken);
        Assert.True(send.IsFaulted);
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => send.AsTask()));
        Assert.Equal(1, sender.Calls);
    }

    [Theory]
    [InlineData(DhmpRatePolicy.Unlimited)]
    [InlineData(DhmpRatePolicy.RejectWindow)]
    [InlineData(DhmpRatePolicy.SmoothPacing)]
    public async Task PreCanceledToken_ReturnsCanceledOperation_WithoutSending(DhmpRatePolicy ratePolicy)
    {
        var sender = new Sender();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        ValueTask send = Create(sender, ratePolicy).SendAsync(new byte[16], cancellation.Token);
        Assert.True(send.IsCanceled);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, sender.Calls);
    }

    [Theory]
    [InlineData(DhmpRatePolicy.Unlimited, false)]
    [InlineData(DhmpRatePolicy.RejectWindow, false)]
    [InlineData(DhmpRatePolicy.SmoothPacing, false)]
    [InlineData(DhmpRatePolicy.Unlimited, true)]
    [InlineData(DhmpRatePolicy.RejectWindow, true)]
    [InlineData(DhmpRatePolicy.SmoothPacing, true)]
    public async Task SenderCancellation_RemainsCanceled_EvenForUncanceledExceptionToken(DhmpRatePolicy ratePolicy, bool tokenCanceled)
    {
        using var cancellation = new CancellationTokenSource();
        if (tokenCanceled) cancellation.Cancel();
        var expected = new OperationCanceledException(cancellation.Token);
        var sender = new Sender { Send = () => throw expected };
        ValueTask send = Create(sender, ratePolicy).SendAsync(new byte[16], TestContext.Current.CancellationToken);
        Assert.True(send.IsCanceled);
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send.AsTask());
        Assert.Equal(cancellation.Token, actual.CancellationToken);
    }

    [Theory]
    [InlineData(DhmpRatePolicy.Unlimited, 0)]
    [InlineData(DhmpRatePolicy.RejectWindow, 0)]
    [InlineData(DhmpRatePolicy.SmoothPacing, 0)]
    [InlineData(DhmpRatePolicy.Unlimited, 1)]
    [InlineData(DhmpRatePolicy.RejectWindow, 1)]
    [InlineData(DhmpRatePolicy.SmoothPacing, 1)]
    [InlineData(DhmpRatePolicy.Unlimited, 2)]
    [InlineData(DhmpRatePolicy.RejectWindow, 2)]
    [InlineData(DhmpRatePolicy.SmoothPacing, 2)]
    public async Task PendingSend_WaitsForBackend_AndPreservesCompletion(DhmpRatePolicy ratePolicy, int completion)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new Sender { Send = () => new ValueTask(pending.Task) };
        Task send = Create(sender, ratePolicy).SendAsync(new byte[16], TestContext.Current.CancellationToken).AsTask();
        Assert.False(send.IsCompleted);
        var expected = new IOException("delayed failure");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (completion == 0) pending.SetResult();
        else if (completion == 1) pending.SetException(expected);
        else pending.SetCanceled(cancellation.Token);
        if (completion == 0) await send;
        else if (completion == 1) Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => send));
        else
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
            Assert.True(send.IsCanceled);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        Assert.Equal(1, sender.Calls);
    }

    [Theory]
    [InlineData(DhmpRatePolicy.Unlimited, false)]
    [InlineData(DhmpRatePolicy.RejectWindow, false)]
    [InlineData(DhmpRatePolicy.SmoothPacing, false)]
    [InlineData(DhmpRatePolicy.Unlimited, true)]
    [InlineData(DhmpRatePolicy.RejectWindow, true)]
    [InlineData(DhmpRatePolicy.SmoothPacing, true)]
    public async Task ValueTaskSource_IsConsumedExactlyOnce(DhmpRatePolicy ratePolicy, bool alreadyCompleted)
    {
        var source = new Source();
        if (alreadyCompleted) source.Complete();
        var sender = new Sender { Send = () => source.Operation };
        ValueTask operation = Create(sender, ratePolicy).SendAsync(new byte[16], TestContext.Current.CancellationToken);
        if (alreadyCompleted) Assert.Equal(1, source.GetResultCalls);
        else { Assert.Equal(0, source.GetResultCalls); source.Complete(); }
        await operation;
        Assert.Equal(1, source.GetResultCalls);
        Assert.False(source.Flags.HasFlag(ValueTaskSourceOnCompletedFlags.UseSchedulingContext));
    }

    [Theory]
    [InlineData(DhmpRatePolicy.Unlimited, false)]
    [InlineData(DhmpRatePolicy.RejectWindow, false)]
    [InlineData(DhmpRatePolicy.SmoothPacing, false)]
    [InlineData(DhmpRatePolicy.Unlimited, true)]
    [InlineData(DhmpRatePolicy.RejectWindow, true)]
    [InlineData(DhmpRatePolicy.SmoothPacing, true)]
    public async Task ValueTaskSourceFailure_IsConsumedExactlyOnce(DhmpRatePolicy ratePolicy, bool alreadyCompleted)
    {
        var expected = new IOException("source failure");
        var source = new Source();
        if (alreadyCompleted) source.Fail(expected);
        var sender = new Sender { Send = () => source.Operation };
        ValueTask operation = Create(sender, ratePolicy).SendAsync(new byte[16], TestContext.Current.CancellationToken);
        if (!alreadyCompleted) source.Fail(expected);
        Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => operation.AsTask()));
        Assert.Equal(1, source.GetResultCalls);
    }

    [Theory]
    [InlineData(DhmpRatePolicy.Unlimited)]
    [InlineData(DhmpRatePolicy.RejectWindow)]
    [InlineData(DhmpRatePolicy.SmoothPacing)]
    public async Task SynchronousBackend_DoesNotLeakAmbientContextChangesToCaller(DhmpRatePolicy ratePolicy)
    {
        var ambient = new AsyncLocal<string?> { Value = "caller" };
        var originalContext = SynchronizationContext.Current;
        var sender = new Sender { Send = () => {
            ambient.Value = "backend";
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            return ValueTask.CompletedTask;
        } };
        ValueTask send = Create(sender, ratePolicy).SendAsync(new byte[16], TestContext.Current.CancellationToken);
        Assert.Equal("caller", ambient.Value);
        Assert.Same(originalContext, SynchronizationContext.Current);
        await send;
    }

    [Fact]
    public async Task RejectWindow_SingleSendExhaustion_DoesNotCallBackendAgain()
    {
        var sender = new Sender();
        var client = new DhmpClient(sender, new DhmpWireContract(16),
            new DhmpSendPolicy(1, 64, DhmpRatePolicy.RejectWindow));
        await client.SendAsync(new byte[16], TestContext.Current.CancellationToken);
        ValueTask blocked = client.SendAsync(new byte[16], TestContext.Current.CancellationToken);
        Assert.True(blocked.IsFaulted);
        await Assert.ThrowsAsync<DhmpProtocolException>(() => blocked.AsTask());
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task SmoothPacing_CancelWhileWaiting_DoesNotConsumeNextSlot()
    {
        var sender = new Sender();
        var client = new DhmpClient(sender, new DhmpWireContract(16),
            new DhmpSendPolicy(1, 64, DhmpRatePolicy.SmoothPacing));
        await client.SendAsync(new byte[16], TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        ValueTask waiting = client.SendAsync(new byte[16], cancellation.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, sender.Calls);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, sender.Calls);
        await client.SendAsync(new byte[16], TestContext.Current.CancellationToken);
        Assert.Equal(2, sender.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SmoothPacing_AfterActualDelay_PreservesBackendCompletion(int completion)
    {
        var backend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new Sender { Send = () => new ValueTask(backend.Task) };
        var client = new DhmpClient(sender, new DhmpWireContract(16),
            new DhmpSendPolicy(5, 64, DhmpRatePolicy.SmoothPacing));
        backend.SetResult();
        await client.SendAsync(new byte[16], TestContext.Current.CancellationToken);
        backend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task waiting = client.SendAsync(new byte[16], TestContext.Current.CancellationToken).AsTask();
        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, sender.Calls);
        var expected = new IOException("failure after pacing");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        if (completion == 0) backend.SetResult();
        else if (completion == 1) backend.SetException(expected);
        else backend.SetCanceled(cancellation.Token);
        if (completion == 0) await waiting;
        else if (completion == 1) Assert.Same(expected, await Assert.ThrowsAsync<IOException>(() => waiting));
        else
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        Assert.Equal(2, sender.Calls);
    }

    private sealed class Sender : IDhmpDynamicPacketSender
    {
        public int MaximumPayloadBytes => 64;
        public int CurrentMaximumPayloadBytes { get; set; } = 64;
        public int Calls { get; private set; }
        public ReadOnlyMemory<byte> LastRecord { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Func<ValueTask> Send { get; init; } = static () => ValueTask.CompletedTask;
        public ValueTask SendPacketAsync(ReadOnlyMemory<byte> record, CancellationToken token = default)
        {
            Calls++;
            LastRecord = record;
            LastToken = token;
            return Send();
        }
    }

    private sealed class Source : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };
        public int GetResultCalls { get; private set; }
        public ValueTaskSourceOnCompletedFlags Flags { get; private set; }
        public ValueTask Operation => new(this, _core.Version);
        public void Complete() => _core.SetResult(true);
        public void Fail(Exception error) => _core.SetException(error);
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void GetResult(short token) { GetResultCalls++; _core.GetResult(token); }
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            Flags = flags;
            _core.OnCompleted(continuation, state, token, flags);
        }
    }
}
