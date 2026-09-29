using Xunit;

namespace DHMP.Protocol.Tests;

public sealed class DhmpProtocolTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(257)]
    public void CompletePackets_PreserveEveryByteAndBatch(int size)
    {
        var wire = new DhmpWireContract(size);
        var processor = new DhmpPacketProcessor(
            wire,
            new DhmpReceivePolicy(
                DhmpProcessingMode.Sequential,
                size * 4));

        byte[] packet = Enumerable.Range(0, size * 4)
            .Select(i => (byte)i)
            .ToArray();

        byte[]? actual = null;
        int calls = 0;

        processor.Process(packet, batch =>
        {
            calls++;
            actual = batch.ToArray();
        });

        Assert.Equal(1, calls);
        Assert.Equal(packet, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(17)]
    public void InvalidPacket_IsRejectedBeforePublication(int length)
    {
        var processor = new DhmpPacketProcessor(
            new DhmpWireContract(4),
            new DhmpReceivePolicy(
                DhmpProcessingMode.Sequential,
                16));

        int calls = 0;

        Assert.Throws<DhmpProtocolException>(() =>
            processor.Process(new byte[length], _ => calls++));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void SeparatePartialPackets_AreNeverReassembled()
    {
        var processor = new DhmpPacketProcessor(
            new DhmpWireContract(4));

        int calls = 0;
        Action<ReadOnlySpan<byte>> publish = _ => calls++;

        Assert.Throws<DhmpProtocolException>(() =>
            processor.Process(new byte[] { 1 }, publish));
        Assert.Throws<DhmpProtocolException>(() =>
            processor.Process(new byte[] { 2, 3, 4 }, publish));

        Assert.Equal(0, calls);

        byte[]? actual = null;
        processor.Process(
            new byte[] { 9, 8, 7, 6 },
            span => actual = span.ToArray());

        Assert.Equal(new byte[] { 9, 8, 7, 6 }, actual);
    }

    [Fact]
    public void Latest_IsLocalReceivePolicy()
    {
        var wire = new DhmpWireContract(2);
        var sequential = new DhmpPacketProcessor(
            wire,
            new DhmpReceivePolicy(
                DhmpProcessingMode.Sequential));
        var latest = new DhmpPacketProcessor(
            wire,
            new DhmpReceivePolicy(
                DhmpProcessingMode.Latest));

        byte[]? all = null;
        byte[]? newest = null;
        byte[] packet = [1, 2, 3, 4, 5, 6];

        sequential.Process(packet, span => all = span.ToArray());
        latest.Process(packet, span => newest = span.ToArray());

        Assert.Equal(packet, all);
        Assert.Equal(new byte[] { 5, 6 }, newest);
    }

    [Fact]
    public void PublicationBorrowsOriginalStorage()
    {
        byte[] input = [1, 2, 3, 4];
        var processor = new DhmpPacketProcessor(
            new DhmpWireContract(4));

        processor.Process(input, span =>
        {
            input[0] = 99;
            Assert.Equal((byte)99, span[0]);
        });
    }

    [Fact]
    public void CallbackFailure_PropagatesWithoutRetry()
    {
        var processor = new DhmpPacketProcessor(
            new DhmpWireContract(4));

        var error = new InvalidOperationException("handoff failed");
        int calls = 0;

        var actual = Assert.Throws<InvalidOperationException>(() =>
            processor.Process(
                new byte[8],
                _ =>
                {
                    calls++;
                    throw error;
                }));

        Assert.Same(error, actual);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WireContract_ContainsOnlyProtocolCompatibilityValues()
    {
        var wire = new DhmpWireContract(32);

        Assert.Equal(DhmpProtocol.CurrentVersion, wire.Version);
        Assert.Equal(32, wire.RecordSize);
        wire.Validate();
    }

    [Fact]
    public void DefaultWireContract_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            default(DhmpWireContract).Validate());

        Assert.Throws<ArgumentException>(() =>
            new DhmpPacketProcessor(default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void InvalidWireRecordSize_IsRejected(int recordSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpWireContract(recordSize));
    }

    [Fact]
    public void UnsupportedWireVersion_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpWireContract(
                32,
                checked((byte)(DhmpProtocol.CurrentVersion + 1))));
    }

    [Fact]
    public void SameWireContract_AllowsDifferentLocalSendBudgets()
    {
        var wire = new DhmpWireContract(32);
        var lowRate = new DhmpSendPolicy(100, 1024);
        var highRate = new DhmpSendPolicy(100_000, 1408);

        lowRate.Validate(wire);
        highRate.Validate(wire);

        Assert.NotEqual(lowRate.Pmax, highRate.Pmax);
        Assert.Equal(wire, wire);
    }

    [Fact]
    public void SameWireContract_AllowsDifferentLocalReceiveModes()
    {
        var wire = new DhmpWireContract(32);
        var sequential = new DhmpReceivePolicy(
            DhmpProcessingMode.Sequential,
            1024);
        var latest = new DhmpReceivePolicy(
            DhmpProcessingMode.Latest,
            1408);

        sequential.Validate(wire);
        latest.Validate(wire);

        Assert.NotEqual(sequential.Mode, latest.Mode);
    }

    [Theory]
    [InlineData(0, 1408)]
    [InlineData(-1, 1408)]
    [InlineData(100, 0)]
    [InlineData(100, 65536)]
    public void InvalidSendPolicy_IsRejected(
        int pmax,
        int maximumPayloadBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpSendPolicy(
                pmax,
                maximumPayloadBytes));
    }

    [Fact]
    public void SendPolicy_CannotBeSmallerThanOneWireRecord()
    {
        var wire = new DhmpWireContract(64);
        var policy = new DhmpSendPolicy(100, 32);

        Assert.Throws<ArgumentException>(() =>
            policy.Validate(wire));
    }

    [Fact]
    public void DefaultSendPolicy_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            default(DhmpSendPolicy).Validate(
                new DhmpWireContract(4)));
    }

    [Fact]
    public void InvalidReceiveMode_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpReceivePolicy(
                (DhmpProcessingMode)99));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void InvalidReceivePacketLimit_IsRejected(int maximumPayloadBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpReceivePolicy(
                DhmpProcessingMode.Sequential,
                maximumPayloadBytes));
    }

    [Fact]
    public void ReceivePolicy_CannotBeSmallerThanOneWireRecord()
    {
        var wire = new DhmpWireContract(64);
        var policy = new DhmpReceivePolicy(
            DhmpProcessingMode.Sequential,
            32);

        Assert.Throws<ArgumentException>(() =>
            policy.Validate(wire));
    }

    [Fact]
    public void RecordValidation_RequiresExactlyOneRecord()
    {
        var wire = new DhmpWireContract(4);

        wire.ValidateRecord(4);

        Assert.Throws<DhmpProtocolException>(() =>
            wire.ValidateRecord(3));
        Assert.Throws<DhmpProtocolException>(() =>
            wire.ValidateRecord(8));
    }

    [Fact]
    public void PacketValidation_UsesLocalPacketCeiling()
    {
        var wire = new DhmpWireContract(4);

        wire.ValidatePacket(8, 8);

        Assert.Throws<DhmpProtocolException>(() =>
            wire.ValidatePacket(12, 8));
        Assert.Throws<DhmpProtocolException>(() =>
            wire.ValidatePacket(6, 8));
    }

    [Fact]
    public void Budget_ReservesWholeBatchOrNothing()
    {
        var budget = new DhmpPmaxBudget(4);

        Assert.False(budget.TryConsume(5));
        Assert.True(budget.TryConsume(3));
        Assert.False(budget.TryConsume(2));
        Assert.True(budget.TryConsume());
        Assert.False(budget.TryConsume());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            budget.TryConsume(0));
    }

    [Fact]
    public void SmoothPacingSchedule_SpacesLogicalMessages()
    {
        var schedule = new DhmpPacingSchedule(1000);
        long start = 10 * System.Diagnostics.Stopwatch.Frequency;

        Assert.Equal(
            TimeSpan.Zero,
            schedule.GetDelay(100, start));

        schedule.Commit(100, start);

        TimeSpan delay =
            schedule.GetDelay(100, start);

        Assert.InRange(
            delay.TotalMilliseconds,
            99.0,
            101.0);

        Assert.Equal(
            TimeSpan.Zero,
            schedule.GetDelay(
                100,
                start + System.Diagnostics.Stopwatch.Frequency));
    }

    [Fact]
    public void SmoothPacingPolicy_IsLocalAndValidated()
    {
        var wire = new DhmpWireContract(32);
        var policy = new DhmpSendPolicy(
            pmax: 1000,
            maximumPayloadBytes: 1408,
            ratePolicy: DhmpRatePolicy.SmoothPacing);

        policy.Validate(wire);

        Assert.Equal(
            DhmpRatePolicy.SmoothPacing,
            policy.RatePolicy);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpSendPolicy(
                1000,
                1408,
                (DhmpRatePolicy)99));
    }

    [Fact]
    public void AdaptiveRate_HardPressureReducesImmediately()
    {
        var controller =
            new DhmpAdaptiveRateController(
                maximumMessagesPerSecond: 10_000,
                minimumMessagesPerSecond: 500);

        int next =
            controller.ApplyFeedback(
                new DhmpCongestionFeedback(
                    DhmpCongestionPressure.Hard,
                    rateScalePermille: 500,
                    pendingBatches: 4,
                    capacity: 4,
                    lostPendingWork: 10));

        Assert.Equal(5_000, next);
        Assert.Equal(5_000, controller.CurrentMessagesPerSecond);
        Assert.Equal(1, controller.DecreaseCount);
        Assert.Equal(1, controller.FeedbackCount);
    }

    [Fact]
    public void AdaptiveRate_NeverDropsBelowLocalMinimum()
    {
        var controller =
            new DhmpAdaptiveRateController(
                maximumMessagesPerSecond: 1_000,
                minimumMessagesPerSecond: 200);

        for (int i = 0; i < 10; i++)
        {
            controller.ApplyFeedback(
                new DhmpCongestionFeedback(
                    DhmpCongestionPressure.Hard,
                    rateScalePermille: 100,
                    pendingBatches: 1,
                    capacity: 1,
                    lostPendingWork: i + 1));
        }

        Assert.Equal(
            200,
            controller.CurrentMessagesPerSecond);
    }

    [Fact]
    public void AdaptiveRate_NoPressureRecoversGraduallyButNotPastMaximum()
    {
        var controller =
            new DhmpAdaptiveRateController(
                maximumMessagesPerSecond: 1_000,
                minimumMessagesPerSecond: 100,
                recoveryPercent: 10);

        controller.ApplyFeedback(
            new DhmpCongestionFeedback(
                DhmpCongestionPressure.Hard,
                500,
                1,
                1,
                1));

        Assert.Equal(
            500,
            controller.CurrentMessagesPerSecond);

        int recovered =
            controller.ApplyFeedback(
                new DhmpCongestionFeedback(
                    DhmpCongestionPressure.None,
                    1000,
                    0,
                    1,
                    1));

        Assert.Equal(550, recovered);
        Assert.Equal(1, controller.RecoveryCount);

        for (int i = 0; i < 20; i++)
        {
            controller.ApplyFeedback(
                new DhmpCongestionFeedback(
                    DhmpCongestionPressure.None,
                    1000,
                    0,
                    1,
                    1));
        }

        Assert.Equal(
            1_000,
            controller.CurrentMessagesPerSecond);
    }

    [Fact]
    public void CongestionFeedback_ValidatesEvidenceAndScale()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpCongestionFeedback(
                DhmpCongestionPressure.Soft,
                99,
                0,
                1,
                0));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpCongestionFeedback(
                DhmpCongestionPressure.Soft,
                750,
                2,
                1,
                0));

        var feedback =
            new DhmpCongestionFeedback(
                DhmpCongestionPressure.Soft,
                750,
                1,
                4,
                12);

        Assert.Equal(
            (ushort)750,
            feedback.RateScalePermille);
    }

    [Fact]
    public void PacketProcessing_AllocatesNothingAfterWarmup()
    {
        var processor = new DhmpPacketProcessor(
            new DhmpWireContract(32));
        var packet = new byte[1408];

        long count = 0;
        Action<ReadOnlySpan<byte>> publish =
            span => count += span.Length;

        for (int i = 0; i < 1000; i++)
            processor.Process(packet, publish);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++)
            processor.Process(packet, publish);

        long allocated =
            GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1408L * 2000, count);
    }
}
