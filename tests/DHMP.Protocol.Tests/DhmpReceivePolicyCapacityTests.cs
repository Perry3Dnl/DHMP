using DHMP.Protocol;
using Xunit;

namespace DHMP.Protocol.Tests;

public sealed class DhmpReceivePolicyCapacityTests
{
    [Fact]
    public void Exact_sequential_capacity_can_bound_local_fifo_without_changing_million_sizing_default()
    {
        var exact =
            new DhmpReceivePolicy(
                DhmpProcessingMode.Sequential,
                maximumPayloadBytes: 1408,
                sequentialBacklogCapacityRecords: 64);

        Assert.Equal(
            64,
            exact.SequentialBacklogCapacityRecords);

        Assert.Equal(
            1,
            exact.SequentialBacklogMillions);

        var standard =
            new DhmpReceivePolicy(
                DhmpProcessingMode.Sequential,
                maximumPayloadBytes: 1408);

        Assert.Equal(
            1_000_000,
            standard.SequentialBacklogCapacityRecords);
    }

    [Fact]
    public void UnsafeSequential_requires_fixed_backpressure_fifo()
    {
        var valid =
            new DhmpReceivePolicy(
                DhmpProcessingMode.UnsafeSequential,
                maximumPayloadBytes: 1408,
                sequentialBacklogCapacityRecords: 64);

        Assert.Equal(
            DhmpProcessingMode.UnsafeSequential,
            valid.Mode);

        Assert.Throws<ArgumentException>(() =>
            new DhmpReceivePolicy(
                DhmpProcessingMode.UnsafeSequential,
                maximumPayloadBytes: 1408,
                sequentialBacklogOverflowPolicy:
                    DhmpSequentialBacklogOverflowPolicy.DropOldest));

        Assert.Throws<ArgumentException>(() =>
            new DhmpReceivePolicy(
                DhmpProcessingMode.UnsafeSequential,
                maximumPayloadBytes: 1408,
                sequentialBacklogOverflowPolicy:
                    DhmpSequentialBacklogOverflowPolicy.Unbounded));
    }

}
