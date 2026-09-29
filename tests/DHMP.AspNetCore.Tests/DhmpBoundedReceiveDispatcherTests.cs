using DHMP.Server;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpBoundedReceiveDispatcherTests
{
    [Fact]
    public async Task LatestReplace_KeepsOnlyNewestPendingBatch()
    {
        using var cancellation =
            new CancellationTokenSource();

        byte[]? consumed = null;

        using var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.LatestReplace,
                sequentialCapacity: 99,
                (batch, _) =>
                {
                    consumed = batch.ToArray();
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                });

        Assert.True(
            dispatcher.TryPublish(
                new byte[] { 1 }));

        Assert.True(
            dispatcher.TryPublish(
                new byte[] { 2 }));

        Assert.True(
            dispatcher.TryPublish(
                new byte[] { 3 }));

        Assert.Equal(1, dispatcher.Capacity);
        Assert.Equal(1, dispatcher.PendingBatches);
        Assert.Equal(3, dispatcher.AcceptedBatches);
        Assert.Equal(2, dispatcher.ReplacedBatches);
        Assert.Equal(0, dispatcher.SaturationDrops);

        await dispatcher.RunAsync(
            cancellation.Token);

        Assert.Equal(
            new byte[] { 3 },
            consumed);

        Assert.Equal(1, dispatcher.ConsumedBatches);
        Assert.Equal(0, dispatcher.PendingBatches);
    }

    [Fact]
    public async Task SequentialReject_PreservesQueuedOrderAndDropsNewWorkWhenFull()
    {
        using var cancellation =
            new CancellationTokenSource();

        var consumed =
            new List<byte>();

        using var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.SequentialReject,
                sequentialCapacity: 2,
                (batch, _) =>
                {
                    consumed.Add(batch.Span[0]);

                    if (consumed.Count == 2)
                        cancellation.Cancel();

                    return ValueTask.CompletedTask;
                });

        Assert.True(
            dispatcher.TryPublish(
                new byte[] { 10 }));

        Assert.True(
            dispatcher.TryPublish(
                new byte[] { 20 }));

        Assert.False(
            dispatcher.TryPublish(
                new byte[] { 30 }));

        Assert.Equal(2, dispatcher.PendingBatches);
        Assert.Equal(2, dispatcher.AcceptedBatches);
        Assert.Equal(1, dispatcher.SaturationDrops);
        Assert.Equal(0, dispatcher.ReplacedBatches);

        await dispatcher.RunAsync(
            cancellation.Token);

        Assert.Equal(
            new byte[] { 10, 20 },
            consumed);

        Assert.Equal(2, dispatcher.ConsumedBatches);
        Assert.Equal(0, dispatcher.PendingBatches);
    }

    [Fact]
    public async Task BorrowedInput_IsCopiedBeforeAsyncConsumption()
    {
        using var cancellation =
            new CancellationTokenSource();

        byte[]? consumed = null;

        using var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.SequentialReject,
                sequentialCapacity: 1,
                (batch, _) =>
                {
                    consumed = batch.ToArray();
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                });

        byte[] borrowed =
            new byte[] { 1, 2, 3, 4 };

        Assert.True(
            dispatcher.TryPublish(borrowed));

        borrowed.AsSpan().Fill(0xff);

        await dispatcher.RunAsync(
            cancellation.Token);

        Assert.Equal(
            new byte[] { 1, 2, 3, 4 },
            consumed);
    }

    [Fact]
    public async Task Dispatcher_CanBePassedDirectlyAsServerPublicationCallback()
    {
        using var cancellation =
            new CancellationTokenSource();

        byte[]? consumed = null;

        using var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.LatestReplace,
                sequentialCapacity: 1,
                (batch, _) =>
                {
                    consumed = batch.ToArray();
                    cancellation.Cancel();
                    return ValueTask.CompletedTask;
                });

        var server =
            new DhmpServer(
                new DHMP.Protocol.DhmpWireContract(4),
                new DHMP.Protocol.DhmpReceivePolicy(
                    DHMP.Protocol.DhmpProcessingMode.Latest,
                    16));

        server.ProcessPacket(
            new byte[] {
                1, 2, 3, 4,
                5, 6, 7, 8
            },
            dispatcher.Publish);

        await dispatcher.RunAsync(
            cancellation.Token);

        Assert.Equal(
            new byte[] { 5, 6, 7, 8 },
            consumed);
    }

    [Fact]
    public async Task ConsumerFailure_PropagatesFromRunLoop()
    {
        using var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.SequentialReject,
                sequentialCapacity: 1,
                (_, _) =>
                    ValueTask.FromException(
                        new InvalidOperationException(
                            "application failed")));

        Assert.True(
            dispatcher.TryPublish(
                new byte[] { 1, 2, 3, 4 }));

        var error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dispatcher.RunAsync(
                    TestContext.Current.CancellationToken));

        Assert.Equal(
            "application failed",
            error.Message);
    }

    [Fact]
    public void Snapshot_ReportsSaturationAndLostPendingWork()
    {
        using var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.SequentialReject,
                sequentialCapacity: 1,
                (_, _) => ValueTask.CompletedTask);

        Assert.True(
            dispatcher.TryPublish(
                new byte[] { 1 }));

        Assert.False(
            dispatcher.TryPublish(
                new byte[] { 2 }));

        var snapshot =
            dispatcher.GetSnapshot();

        Assert.True(snapshot.IsSaturated);
        Assert.Equal(1, snapshot.PendingBatches);
        Assert.Equal(1, snapshot.AcceptedBatches);
        Assert.Equal(1, snapshot.SaturationDrops);
        Assert.Equal(1, snapshot.LostPendingWork);
    }

    [Fact]
    public void ReceivePolicyFactory_MapsLatestAndSequentialExplicitly()
    {
        using var latest =
            DhmpBoundedReceiveDispatcher.FromReceivePolicy(
                new DHMP.Protocol.DhmpReceivePolicy(
                    DHMP.Protocol.DhmpProcessingMode.Latest,
                    64),
                sequentialCapacity: 8,
                (_, _) => ValueTask.CompletedTask);

        using var sequential =
            DhmpBoundedReceiveDispatcher.FromReceivePolicy(
                new DHMP.Protocol.DhmpReceivePolicy(
                    DHMP.Protocol.DhmpProcessingMode.Sequential,
                    64),
                sequentialCapacity: 8,
                (_, _) => ValueTask.CompletedTask);

        Assert.Equal(
            DhmpReceiveDispatchMode.LatestReplace,
            latest.Mode);
        Assert.Equal(1, latest.Capacity);

        Assert.Equal(
            DhmpReceiveDispatchMode.SequentialReject,
            sequential.Mode);
        Assert.Equal(8, sequential.Capacity);
    }

    [Fact]
    public void InvalidConfiguration_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpBoundedReceiveDispatcher(
                (DhmpReceiveDispatchMode)99,
                1,
                (_, _) => ValueTask.CompletedTask));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.SequentialReject,
                0,
                (_, _) => ValueTask.CompletedTask));
    }
}
