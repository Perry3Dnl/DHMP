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
                maximumPayloadBytes: maximumPayloadBytes));
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
