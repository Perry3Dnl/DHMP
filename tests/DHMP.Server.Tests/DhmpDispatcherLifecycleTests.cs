using DHMP.Server;
using Xunit;

namespace DHMP.Server.Tests;

public sealed class DhmpDispatcherLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(DhmpReceiveDispatchMode.SequentialReject)]
    [InlineData(DhmpReceiveDispatchMode.LatestReplace)]
    public async Task Dispose_WakesIdleConsumerWithoutCallerCancellation(
        DhmpReceiveDispatchMode mode)
    {
        using var dispatcher = new DhmpBoundedReceiveDispatcher(
            mode, 2, (_, _) => ValueTask.CompletedTask);

        Task run = dispatcher.RunAsync(TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);

        dispatcher.Dispose();
        await run.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.False(dispatcher.TryPublish(new byte[] { 1 }));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => dispatcher.RunAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(DhmpReceiveDispatchMode.SequentialReject)]
    [InlineData(DhmpReceiveDispatchMode.LatestReplace)]
    public async Task Dispose_DiscardsPendingWorkButPreservesActiveConsumerBuffer(
        DhmpReceiveDispatchMode mode)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] expected = { 17, 42, 99 };
        int calls = 0;

        using var dispatcher = new DhmpBoundedReceiveDispatcher(
            mode, 2, async (batch, token) =>
            {
                Interlocked.Increment(ref calls);
                entered.SetResult();
                await release.Task.WaitAsync(token);
                Assert.Equal(expected, batch.ToArray());
            });

        Assert.True(dispatcher.TryPublish(expected));
        Task run = dispatcher.RunAsync(TestContext.Current.CancellationToken);

        try
        {
            await entered.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.True(dispatcher.TryPublish(new byte[] { 2 }));
            dispatcher.Dispose();
            dispatcher.Dispose();

            Assert.Equal(0, dispatcher.PendingBatches);
            Assert.False(run.IsCompleted);
            Assert.False(dispatcher.TryPublish(new byte[] { 3 }));
        }
        finally
        {
            release.TrySetResult();
            dispatcher.Dispose();
            await run.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, calls);
        Assert.Equal(1, dispatcher.ConsumedBatches);
    }

    [Theory]
    [InlineData(DhmpReceiveDispatchMode.SequentialReject)]
    [InlineData(DhmpReceiveDispatchMode.LatestReplace)]
    public async Task Dispose_FromConsumerCompletesRunWithoutDeadlock(
        DhmpReceiveDispatchMode mode)
    {
        DhmpBoundedReceiveDispatcher? dispatcher = null;
        dispatcher = new DhmpBoundedReceiveDispatcher(
            mode, 2, (batch, _) =>
            {
                dispatcher!.Dispose();
                Assert.Equal(new byte[] { 7 }, batch.ToArray());
                return ValueTask.CompletedTask;
            });

        using (dispatcher)
        {
            Assert.True(dispatcher.TryPublish(new byte[] { 7 }));
            await dispatcher.RunAsync(TestContext.Current.CancellationToken)
                .WaitAsync(Timeout, TestContext.Current.CancellationToken);
            Assert.Equal(1, dispatcher.ConsumedBatches);
        }
    }

    [Theory]
    [InlineData(DhmpReceiveDispatchMode.SequentialReject)]
    [InlineData(DhmpReceiveDispatchMode.LatestReplace)]
    public async Task ConcurrentPublicationAndRepeatedDisposal_DoNotUseReleasedResources(
        DhmpReceiveDispatchMode mode)
    {
        using var dispatcher = new DhmpBoundedReceiveDispatcher(
            mode, 8, (_, _) => ValueTask.CompletedTask);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = dispatcher.RunAsync(TestContext.Current.CancellationToken);

        Task publisher = Task.Run(async () =>
        {
            await start.Task;
            for (int i = 0; i < 1000; i++)
                dispatcher.TryPublish(new byte[] { 1 });
        }, TestContext.Current.CancellationToken);
        Task disposer = Task.Run(async () =>
        {
            await start.Task;
            for (int i = 0; i < 100; i++)
                dispatcher.Dispose();
        }, TestContext.Current.CancellationToken);

        start.SetResult();
        try
        {
            await Task.WhenAll(publisher, disposer, run)
                .WaitAsync(Timeout, TestContext.Current.CancellationToken);
        }
        finally
        {
            dispatcher.Dispose();
        }

        Assert.Equal(0, dispatcher.PendingBatches);
        Assert.False(dispatcher.TryPublish(new byte[] { 2 }));
        Assert.InRange(dispatcher.ConsumedBatches, 0L, dispatcher.AcceptedBatches);
    }
}
