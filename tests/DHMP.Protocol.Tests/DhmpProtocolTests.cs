using Xunit;

namespace DHMP.Protocol.Tests;

public sealed class DhmpProtocolTests
{
    [Fact]
    public void HappyPath_ExactPayloadSize_IsAccepted()
    {
        var contract = new DhmpFixedContract(32, 100);
        contract.ValidatePayload(32);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void BlockedPath_NonExactPayloadSize_IsRejected(int length)
    {
        var contract = new DhmpFixedContract(32, 100);
        Assert.Throws<DhmpProtocolException>(() => contract.ValidatePayload(length));
    }

    [Fact]
    public async Task CriticalPath_ArbitraryTransportSegmentation_PreservesFrames()
    {
        var reader = new DhmpFixedFrameReader(new DhmpFixedContract(4, 100));
        var frames = new List<byte[]>();

        await reader.PushAsync(new byte[] { 1 }, m => Capture(frames, m));
        await reader.PushAsync(new byte[] { 2, 3, 4, 5, 6 }, m => Capture(frames, m));
        await reader.PushAsync(new byte[] { 7, 8 }, m => Capture(frames, m));
        reader.Complete();

        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, frames[0]);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, frames[1]);
    }

    [Fact]
    public async Task BlockedPath_IncompleteFinalPayload_IsRejected()
    {
        var reader = new DhmpFixedFrameReader(new DhmpFixedContract(4, 100));
        await reader.PushAsync(new byte[] { 1, 2, 3 }, _ => ValueTask.CompletedTask);
        Assert.Throws<DhmpProtocolException>(() => reader.Complete());
    }

    [Fact]
    public void BlockedPath_PmaxBudget_RejectsBeyondConfiguredRateWindow()
    {
        var budget = new DhmpPmaxBudget(2);
        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.False(budget.TryConsume());
    }

    private static ValueTask Capture(List<byte[]> frames, ReadOnlyMemory<byte> frame)
    {
        frames.Add(frame.ToArray());
        return ValueTask.CompletedTask;
    }
}
