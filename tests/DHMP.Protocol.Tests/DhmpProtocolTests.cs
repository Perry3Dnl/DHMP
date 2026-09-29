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
        var contract = new DhmpFixedContract(size, 1000, size * 4);
        var processor = new DhmpPacketProcessor(contract);
        byte[] packet = Enumerable.Range(0, size * 4).Select(i => (byte)i).ToArray();
        byte[]? actual = null;
        int calls = 0;
        processor.Process(packet, batch => { calls++; actual = batch.ToArray(); });
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
        var processor = new DhmpPacketProcessor(new DhmpFixedContract(4, 100, 16));
        int calls = 0;
        Assert.Throws<DhmpProtocolException>(() =>
            processor.Process(new byte[length], _ => calls++));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void SeparatePartialPackets_AreNeverReassembled()
    {
        var processor = new DhmpPacketProcessor(new DhmpFixedContract(4, 100));
        int calls = 0;
        Action<ReadOnlySpan<byte>> publish = _ => calls++;
        Assert.Throws<DhmpProtocolException>(() => processor.Process(new byte[] { 1 }, publish));
        Assert.Throws<DhmpProtocolException>(() => processor.Process(new byte[] { 2, 3, 4 }, publish));
        Assert.Equal(0, calls);
        byte[]? actual = null;
        processor.Process(new byte[] { 9, 8, 7, 6 }, s => actual = s.ToArray());
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, actual);
    }

    [Fact]
    public void Latest_SelectsFinalRecordOfThisPacket()
    {
        var processor = new DhmpPacketProcessor(new DhmpFixedContract(2, 100), DhmpProcessingMode.Latest);
        byte[]? actual = null;
        processor.Process(new byte[] { 1, 2, 3, 4, 5, 6 }, s => actual = s.ToArray());
        Assert.Equal(new byte[] { 5, 6 }, actual);
    }

    [Fact]
    public void PublicationBorrowsOriginalStorage()
    {
        byte[] input = [1, 2, 3, 4];
        var processor = new DhmpPacketProcessor(new DhmpFixedContract(4, 100));
        processor.Process(input, span =>
        {
            input[0] = 99;
            Assert.Equal((byte)99, span[0]);
        });
    }

    [Fact]
    public void CallbackFailure_PropagatesWithoutRetry()
    {
        var processor = new DhmpPacketProcessor(new DhmpFixedContract(4, 100));
        var error = new InvalidOperationException("handoff failed");
        int calls = 0;
        var actual = Assert.Throws<InvalidOperationException>(() =>
            processor.Process(new byte[8], _ => { calls++; throw error; }));
        Assert.Same(error, actual);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DefaultContract_IsRejectedAtSetup()
    {
        Assert.Throws<ArgumentException>(() => new DhmpPacketProcessor(default));
    }

    [Theory]
    [InlineData(0, 100, 1408)]
    [InlineData(-1, 100, 1408)]
    [InlineData(4, 0, 1408)]
    [InlineData(4, 100, 3)]
    [InlineData(4, 100, 65536)]
    public void InvalidContract_IsRejected(int size, int pmax, int maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DhmpFixedContract(size, pmax, maximum));
    }

    [Fact]
    public void InvalidMode_IsRejectedAtSetup()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpPacketProcessor(new DhmpFixedContract(4, 100), (DhmpProcessingMode)99));
    }

    [Fact]
    public void PayloadValidation_RequiresOneWholeMessage()
    {
        var contract = new DhmpFixedContract(4, 100);
        contract.ValidatePayload(4);
        Assert.Throws<DhmpProtocolException>(() => contract.ValidatePayload(3));
        Assert.Throws<DhmpProtocolException>(() => contract.ValidatePayload(8));
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
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.TryConsume(0));
    }

    [Fact]
    public void PacketProcessing_AllocatesNothingAfterWarmup()
    {
        var processor = new DhmpPacketProcessor(new DhmpFixedContract(32, 1000));
        var packet = new byte[1408];
        long count = 0;
        Action<ReadOnlySpan<byte>> publish = s => count += s.Length;
        for (int i = 0; i < 1000; i++) processor.Process(packet, publish);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) processor.Process(packet, publish);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(1408L * 2000, count);
    }
}
