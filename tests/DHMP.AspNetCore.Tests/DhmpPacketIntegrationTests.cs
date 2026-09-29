using DHMP.Client;
using DHMP.Protocol;
using DHMP.Server;
using Xunit;

namespace DHMP.AspNetCore.Tests;

/// <summary>Packet contract integration using an explicit in-memory test sender, not a network measurement.</summary>
public sealed class DhmpPacketIntegrationTests
{
    private sealed class TestSender : IDhmpPacketSender
    {
        public int MaximumPayloadBytes { get; init; } = 1408;
        public int Calls { get; private set; }
        public Action<ReadOnlyMemory<byte>>? Deliver { get; init; }
        public Exception? Failure { get; init; }
        public ValueTask SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (Failure is not null) throw Failure;
            Deliver?.Invoke(packet);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task WholeBatch_CrossesExplicitPacketBoundary()
    {
        var contract = new DhmpFixedContract(4, 100);
        var receiver = new DhmpServer(contract);
        byte[]? actual = null;
        int callbacks = 0;
        var sender = new TestSender
        {
            Deliver = packet => receiver.ProcessPacket(packet.Span, batch =>
            {
                callbacks++;
                actual = batch.ToArray();
            })
        };
        var client = new DhmpClient(sender, contract);
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];
        await client.SendBatchAsync(data, TestContext.Current.CancellationToken);
        Assert.Equal(data, actual);
        Assert.Equal(1, callbacks);
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task InvalidPackets_DoNotReachBackend()
    {
        var sender = new TestSender();
        var client = new DhmpClient(sender, new DhmpFixedContract(4, 100, 8));
        await Assert.ThrowsAsync<DhmpProtocolException>(() => client.SendBatchAsync(new byte[3], TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<DhmpProtocolException>(() => client.SendBatchAsync(new byte[12], TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<DhmpProtocolException>(() => client.SendBatchAsync(ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAsync<DhmpProtocolException>(() => client.SendAsync(new byte[8], TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task ExhaustedBudget_DoesNotQueueOrSend()
    {
        var sender = new TestSender();
        var client = new DhmpClient(sender, new DhmpFixedContract(4, 2));
        await client.SendBatchAsync(new byte[8], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DhmpProtocolException>(() => client.SendAsync(new byte[4], TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task CancelledSend_DoesNotReserveBudget()
    {
        var sender = new TestSender();
        var client = new DhmpClient(sender, new DhmpFixedContract(4, 1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendAsync(new byte[4], cancellation.Token).AsTask());
        await client.SendAsync(new byte[4], TestContext.Current.CancellationToken);
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task BackendFailure_PropagatesWithoutRetry()
    {
        var error = new InvalidOperationException("backend failed");
        var sender = new TestSender { Failure = error };
        var client = new DhmpClient(sender, new DhmpFixedContract(4, 100));
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendAsync(new byte[4], TestContext.Current.CancellationToken).AsTask());
        Assert.Same(error, actual);
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public void Setup_RejectsInvalidContractAndBackendLimit()
    {
        Assert.Throws<ArgumentException>(() => new DhmpClient(new TestSender(), default));
        Assert.Throws<ArgumentException>(() => new DhmpServer(default));
        Assert.Throws<ArgumentException>(() =>
            new DhmpClient(new TestSender { MaximumPayloadBytes = 32 }, new DhmpFixedContract(4, 100)));
    }

    [Fact]
    public void TypedBoundary_RejectsPartialModels()
    {
        var boundary = new DhmpModelBoundary<int>(4);
        Assert.Equal(2, boundary.Cast(new byte[8]).Length);
        Assert.Throws<ArgumentException>(() => boundary.Cast(new byte[7]));
        Assert.Throws<ArgumentException>(() => new DhmpModelBoundary<int>(3));
    }
}
