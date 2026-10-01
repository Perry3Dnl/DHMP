using DHMP.Client;
using DHMP.Protocol;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpDynamicPayloadLimitTests
{
    [Fact]
    public async Task Client_RecomputesWholeRecordCeilingAsSenderLimitChanges()
    {
        var sender = new DynamicSender(
            maximumPayloadBytes: 1408,
            currentMaximumPayloadBytes: 1001);

        var client = new DhmpClient(
            sender,
            new DhmpWireContract(32),
            new DhmpSendPolicy(
                pmax: 1000,
                maximumPayloadBytes: 1408));

        Assert.Equal(992, client.CurrentMaximumPayloadBytes);

        await client.SendBatchAsync(
            new byte[992],
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DhmpProtocolException>(() =>
            client.SendBatchAsync(
                new byte[1024],
                TestContext.Current.CancellationToken)
            .AsTask());

        Assert.Equal(1, sender.Calls);

        sender.SetCurrentMaximumPayloadBytes(1210);

        Assert.Equal(1184, client.CurrentMaximumPayloadBytes);

        await client.SendBatchAsync(
            new byte[1184],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, sender.Calls);
    }

    [Fact]
    public async Task Client_FailsClosedWhenLivePathCannotFitOneRecord()
    {
        var sender = new DynamicSender(
            maximumPayloadBytes: 1408,
            currentMaximumPayloadBytes: 31);

        var client = new DhmpClient(
            sender,
            new DhmpWireContract(32),
            new DhmpSendPolicy(
                pmax: 100,
                maximumPayloadBytes: 1408));

        Assert.Equal(0, client.CurrentMaximumPayloadBytes);

        await Assert.ThrowsAsync<DhmpProtocolException>(() =>
            client.SendAsync(
                new byte[32],
                TestContext.Current.CancellationToken)
            .AsTask());

        Assert.Equal(0, sender.Calls);
    }

    private sealed class DynamicSender :
        IDhmpDynamicPacketSender
    {
        private int _currentMaximumPayloadBytes;

        public DynamicSender(
            int maximumPayloadBytes,
            int currentMaximumPayloadBytes)
        {
            MaximumPayloadBytes = maximumPayloadBytes;
            SetCurrentMaximumPayloadBytes(
                currentMaximumPayloadBytes);
        }

        public int MaximumPayloadBytes { get; }

        public int CurrentMaximumPayloadBytes =>
            Volatile.Read(
                ref _currentMaximumPayloadBytes);

        public int Calls { get; private set; }

        public void SetCurrentMaximumPayloadBytes(
            int value)
        {
            if (value < 0 ||
                value > MaximumPayloadBytes)
                throw new ArgumentOutOfRangeException(
                    nameof(value));

            Volatile.Write(
                ref _currentMaximumPayloadBytes,
                value);
        }

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            if (payload.IsEmpty ||
                payload.Length >
                    CurrentMaximumPayloadBytes)
                throw new DhmpProtocolException(
                    "Dynamic test sender rejected payload.");

            Calls++;

            return ValueTask.CompletedTask;
        }
    }
}
