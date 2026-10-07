using DHMP.Client;
using DHMP.Protocol;
using DHMP.Server;
using Xunit;

namespace DHMP.AspNetCore.Tests;

/// <summary>Packet integration through explicit in-memory packet I/O, not a network measurement.</summary>
public sealed class DhmpPacketIntegrationTests
{
    private sealed class TestSender : IDhmpPacketSender
    {
        public int MaximumPayloadBytes { get; init; } = 1408;
        public int Calls { get; private set; }
        public Action<ReadOnlyMemory<byte>>? Deliver { get; init; }
        public Exception? Failure { get; init; }

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> packet,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;

            if (Failure is not null)
                throw Failure;

            Deliver?.Invoke(packet);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task WholeBatch_CrossesSharedWireContract_WithDifferentLocalPolicies()
    {
        var wire = new DhmpWireContract(4);

        var receiver = new DhmpServer(
            wire,
            new DhmpReceivePolicy(
                DhmpProcessingMode.Sequential,
                maximumPayloadBytes: 32));

        var actual =
            new List<byte>();

        int callbacks = 0;

        var sender = new TestSender
        {
            Deliver = packet =>
                receiver.ProcessPacket(packet.Span, batch =>
                {
                    callbacks++;
                    actual.AddRange(
                        batch.ToArray());
                })
        };

        var client = new DhmpClient(
            sender,
            wire,
            new DhmpSendPolicy(
                pmax: 100,
                maximumPayloadBytes: 16));

        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];

        await client.SendBatchAsync(
            data,
            TestContext.Current.CancellationToken);

        Assert.Equal(data, actual.ToArray());
        Assert.Equal(2, callbacks);
        Assert.Equal(2, sender.Calls);
        Assert.Equal(16, client.SendPolicy.MaximumPayloadBytes);
        Assert.Equal(32, receiver.ReceivePolicy.MaximumPayloadBytes);
    }

    [Fact]
    public async Task LocalLatestPolicy_DoesNotChangeWireContract()
    {
        var wire = new DhmpWireContract(4);
        var receiver = new DhmpServer(
            wire,
            new DhmpReceivePolicy(DhmpProcessingMode.Latest));

        byte[]? actual = null;

        var sender = new TestSender
        {
            Deliver = packet =>
                receiver.ProcessPacket(
                    packet.Span,
                    batch => actual = batch.ToArray())
        };

        var client = new DhmpClient(
            sender,
            wire,
            new DhmpSendPolicy(100));

        await client.SendBatchAsync(
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 5, 6, 7, 8 }, actual);
        Assert.Equal(wire, client.WireContract);
        Assert.Equal(wire, receiver.WireContract);
    }

    [Fact]
    public async Task InvalidPackets_DoNotReachBackend()
    {
        var sender = new TestSender();
        var client = new DhmpClient(
            sender,
            new DhmpWireContract(4),
            new DhmpSendPolicy(
                pmax: 100,
                maximumPayloadBytes: 8));

        await Assert.ThrowsAsync<DhmpProtocolException>(() =>
            client.SendBatchAsync(
                new byte[3],
                TestContext.Current.CancellationToken).AsTask());

        await Assert.ThrowsAsync<DhmpProtocolException>(() =>
            client.SendBatchAsync(
                new byte[12],
                TestContext.Current.CancellationToken).AsTask());

        await Assert.ThrowsAsync<DhmpProtocolException>(() =>
            client.SendBatchAsync(
                ReadOnlyMemory<byte>.Empty,
                TestContext.Current.CancellationToken).AsTask());

        await Assert.ThrowsAsync<DhmpProtocolException>(() =>
            client.SendAsync(
                new byte[8],
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task ExhaustedLocalSendBudget_DoesNotQueueOrSend()
    {
        var sender = new TestSender();
        var client = new DhmpClient(
            sender,
            new DhmpWireContract(4),
            new DhmpSendPolicy(2));

        await client.SendBatchAsync(
            new byte[8],
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DhmpProtocolException>(() =>
            client.SendAsync(
                new byte[4],
                TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(2, sender.Calls);
    }

    [Fact]
    public async Task CancelledSend_DoesNotReserveBudget()
    {
        var sender = new TestSender();
        var client = new DhmpClient(
            sender,
            new DhmpWireContract(4),
            new DhmpSendPolicy(1));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendAsync(
                new byte[4],
                cancellation.Token).AsTask());

        await client.SendAsync(
            new byte[4],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task SmoothPacing_DelaysInsteadOfRejectingImmediateSecondSend()
    {
        var sender = new TestSender();
        var client = new DhmpClient(
            sender,
            new DhmpWireContract(4),
            new DhmpSendPolicy(
                pmax: 100_000,
                maximumPayloadBytes: 1408,
                ratePolicy: DhmpRatePolicy.SmoothPacing));

        await client.SendAsync(
            new byte[4],
            TestContext.Current.CancellationToken);

        await client.SendAsync(
            new byte[4],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, sender.Calls);
    }

    [Fact]
    public async Task SmoothClient_UsesAdaptiveRateController()
    {
        var sender = new TestSender();

        var controller =
            new DhmpAdaptiveRateController(
                maximumMessagesPerSecond: 100_000,
                minimumMessagesPerSecond: 1_000);

        var client =
            new DhmpClient(
                sender,
                new DhmpWireContract(4),
                new DhmpSendPolicy(
                    pmax: 100_000,
                    maximumPayloadBytes: 1408,
                    ratePolicy: DhmpRatePolicy.SmoothPacing),
                controller);

        controller.ApplyFeedback(
            new DhmpCongestionFeedback(
                DhmpCongestionPressure.Hard,
                500,
                1,
                1,
                1));

        Assert.Equal(
            50_000,
            client.CurrentMessagesPerSecond);

        await client.SendAsync(
            new byte[4],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public void AdaptiveController_RequiresSmoothPacingAndCannotExceedPmax()
    {
        var sender = new TestSender();
        var wire = new DhmpWireContract(4);

        Assert.Throws<ArgumentException>(() =>
            new DhmpClient(
                sender,
                wire,
                new DhmpSendPolicy(
                    pmax: 1000,
                    ratePolicy: DhmpRatePolicy.RejectWindow),
                new DhmpAdaptiveRateController(1000)));

        Assert.Throws<ArgumentException>(() =>
            new DhmpClient(
                sender,
                wire,
                new DhmpSendPolicy(
                    pmax: 1000,
                    ratePolicy: DhmpRatePolicy.SmoothPacing),
                new DhmpAdaptiveRateController(1001)));
    }

    [Fact]
    public async Task BackendFailure_PropagatesWithoutRetry()
    {
        var error = new InvalidOperationException("backend failed");
        var sender = new TestSender { Failure = error };

        var client = new DhmpClient(
            sender,
            new DhmpWireContract(4),
            new DhmpSendPolicy(100));

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendAsync(
                new byte[4],
                TestContext.Current.CancellationToken).AsTask());

        Assert.Same(error, actual);
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public void Setup_RejectsInvalidContractsPoliciesAndBackendLimit()
    {
        var wire = new DhmpWireContract(4);

        Assert.Throws<ArgumentException>(() =>
            new DhmpClient(
                new TestSender(),
                default,
                new DhmpSendPolicy(100)));

        Assert.Throws<ArgumentException>(() =>
            new DhmpClient(
                new TestSender(),
                wire,
                default));

        Assert.Throws<ArgumentException>(() =>
            new DhmpServer(default));

        Assert.Throws<ArgumentException>(() =>
            new DhmpClient(
                new TestSender { MaximumPayloadBytes = 32 },
                wire,
                new DhmpSendPolicy(
                    pmax: 100,
                    maximumPayloadBytes: 64)));
    }

    [Fact]
    public void TypedBoundary_RejectsPartialModels()
    {
        var boundary = new DhmpModelBoundary<int>(4);

        Assert.Equal(2, boundary.Cast(new byte[8]).Length);
        Assert.Throws<ArgumentException>(() =>
            boundary.Cast(new byte[7]));
        Assert.Throws<ArgumentException>(() =>
            new DhmpModelBoundary<int>(3));
    }
}
