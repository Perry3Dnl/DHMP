using DHMP.Client;
using DHMP.Protocol;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpClientHardeningTests
{
    [Fact]
    public async Task HappyFlow_ExactMaximumBatch_IsEmittedAsSingleRecordPackets()
    {
        var delivered =
            new List<byte[]>();

        var sender =
            new RecordingSender(
                maximumPayloadBytes: 16,
                packet =>
                {
                    delivered.Add(
                        packet.ToArray());
                    return ValueTask.CompletedTask;
                });

        var client =
            new DhmpClient(
                sender,
                new DhmpWireContract(4),
                new DhmpSendPolicy(
                    pmax: 100,
                    maximumPayloadBytes: 16));

        byte[] packet =
            Enumerable.Range(1, 16)
                .Select(i => (byte)i)
                .ToArray();

        await client.SendBatchAsync(
            packet,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, sender.Calls);

        Assert.Equal(
            new[]
            {
                new byte[] { 1, 2, 3, 4 },
                new byte[] { 5, 6, 7, 8 },
                new byte[] { 9, 10, 11, 12 },
                new byte[] { 13, 14, 15, 16 }
            },
            delivered);
    }

    [Fact]
    public async Task CriticalFlow_InvalidRecordAndBatchNeverReachBackend()
    {
        var sender =
            new RecordingSender(
                16,
                _ => ValueTask.CompletedTask);

        var client =
            new DhmpClient(
                sender,
                new DhmpWireContract(4),
                new DhmpSendPolicy(
                    pmax: 100,
                    maximumPayloadBytes: 16));

        await Assert.ThrowsAsync<DhmpProtocolException>(
            () => client.SendAsync(
                new byte[3],
                TestContext.Current.CancellationToken)
                .AsTask());

        await Assert.ThrowsAsync<DhmpProtocolException>(
            () => client.SendBatchAsync(
                new byte[6],
                TestContext.Current.CancellationToken)
                .AsTask());

        await Assert.ThrowsAsync<DhmpProtocolException>(
            () => client.SendBatchAsync(
                new byte[20],
                TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task CriticalFlow_RejectWindow_ConsumesWholeBatchAtomically()
    {
        var sender =
            new RecordingSender(
                64,
                _ => ValueTask.CompletedTask);

        var client =
            new DhmpClient(
                sender,
                new DhmpWireContract(4),
                new DhmpSendPolicy(
                    pmax: 3,
                    maximumPayloadBytes: 64,
                    ratePolicy:
                        DhmpRatePolicy.RejectWindow));

        await client.SendBatchAsync(
            new byte[8],
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DhmpProtocolException>(
            () => client.SendBatchAsync(
                new byte[8],
                TestContext.Current.CancellationToken)
                .AsTask());

        await client.SendAsync(
            new byte[4],
            TestContext.Current.CancellationToken);

        Assert.Equal(3, sender.Calls);
    }

    [Fact]
    public async Task CriticalFlow_CancellationDuringSmoothPacingDoesNotSendDelayedPacket()
    {
        var sender =
            new RecordingSender(
                128,
                _ => ValueTask.CompletedTask);

        var client =
            new DhmpClient(
                sender,
                new DhmpWireContract(4),
                new DhmpSendPolicy(
                    pmax: 10,
                    maximumPayloadBytes: 128,
                    ratePolicy:
                        DhmpRatePolicy.SmoothPacing));

        byte[] tenMessages =
            new byte[40];

        await client.SendBatchAsync(
            tenMessages,
            TestContext.Current.CancellationToken);

        using var cancellation =
            new CancellationTokenSource();

        cancellation.CancelAfter(
            TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SendBatchAsync(
                tenMessages,
                cancellation.Token)
                .AsTask());

        Assert.Equal(10, sender.Calls);
    }

    [Fact]
    public async Task CriticalFlow_BackendFailureIsNotRetriedAndConsumesCurrentAttempt()
    {
        var expected =
            new IOException(
                "backend unavailable");

        var sender =
            new RecordingSender(
                64,
                _ => ValueTask.FromException(
                    expected));

        var client =
            new DhmpClient(
                sender,
                new DhmpWireContract(4),
                new DhmpSendPolicy(
                    pmax: 1,
                    maximumPayloadBytes: 64));

        var actual =
            await Assert.ThrowsAsync<IOException>(
                () => client.SendAsync(
                    new byte[4],
                    TestContext.Current.CancellationToken)
                    .AsTask());

        Assert.Same(expected, actual);
        Assert.Equal(1, sender.Calls);

        await Assert.ThrowsAsync<DhmpProtocolException>(
            () => client.SendAsync(
                new byte[4],
                TestContext.Current.CancellationToken)
                .AsTask());

        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public void CriticalFlow_ClientSetupRejectsNullAndInsufficientSender()
    {
        var wire =
            new DhmpWireContract(4);

        Assert.Throws<ArgumentNullException>(() =>
            new DhmpClient(
                null!,
                wire,
                new DhmpSendPolicy(100)));

        Assert.Throws<ArgumentException>(() =>
            new DhmpClient(
                new RecordingSender(
                    15,
                    _ => ValueTask.CompletedTask),
                wire,
                new DhmpSendPolicy(
                    pmax: 100,
                    maximumPayloadBytes: 16)));
    }

    [Fact]
    public void HappyFlow_AdaptiveControllerMayUseStricterLocalMaximumThanPmax()
    {
        var controller =
            new DhmpAdaptiveRateController(
                maximumMessagesPerSecond: 5_000,
                minimumMessagesPerSecond: 500);

        var client =
            new DhmpClient(
                new RecordingSender(
                    1408,
                    _ => ValueTask.CompletedTask),
                new DhmpWireContract(4),
                new DhmpSendPolicy(
                    pmax: 10_000,
                    maximumPayloadBytes: 1408,
                    ratePolicy:
                        DhmpRatePolicy.SmoothPacing),
                controller);

        Assert.Equal(
            5_000,
            client.CurrentMessagesPerSecond);

        Assert.Same(
            controller,
            client.AdaptiveRateController);
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
