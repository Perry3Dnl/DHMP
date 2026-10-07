using DHMP.Protocol;
using Xunit;

namespace DHMP.Server.Tests;

public sealed class DhmpSequentialBacklogTests
{
    [Fact]
    public void HappyFlow_FixedSequentialBacklogPreservesFifoOrder()
    {
        var backlog =
            new DhmpSequentialBacklog(
                recordSize: 2,
                capacityRecords: 4,
                DhmpSequentialBacklogOverflowPolicy.Backpressure);

        backlog.Enqueue(new byte[] { 1, 1 });
        backlog.Enqueue(new byte[] { 2, 2 });
        backlog.Enqueue(new byte[] { 3, 3 });

        Span<byte> record =
            stackalloc byte[2];

        Assert.True(backlog.TryDequeue(record));
        Assert.Equal(new byte[] { 1, 1 }, record.ToArray());

        Assert.True(backlog.TryDequeue(record));
        Assert.Equal(new byte[] { 2, 2 }, record.ToArray());

        Assert.True(backlog.TryDequeue(record));
        Assert.Equal(new byte[] { 3, 3 }, record.ToArray());

        Assert.False(backlog.TryDequeue(record));
        Assert.Equal(0, backlog.RecordsDropped);
    }

    [Fact]
    public void HappyFlow_DropOldestKeepsNewestRecordsInFifoOrder()
    {
        var backlog =
            new DhmpSequentialBacklog(
                recordSize: 1,
                capacityRecords: 3,
                DhmpSequentialBacklogOverflowPolicy.DropOldest);

        backlog.Enqueue(new byte[] { 1 });
        backlog.Enqueue(new byte[] { 2 });
        backlog.Enqueue(new byte[] { 3 });
        backlog.Enqueue(new byte[] { 4 });

        Span<byte> record =
            stackalloc byte[1];

        Assert.True(backlog.TryDequeue(record));
        Assert.Equal((byte)2, record[0]);

        Assert.True(backlog.TryDequeue(record));
        Assert.Equal((byte)3, record[0]);

        Assert.True(backlog.TryDequeue(record));
        Assert.Equal((byte)4, record[0]);

        Assert.Equal(1, backlog.RecordsDropped);
    }

    [Fact]
    public async Task CriticalFlow_BackpressureWaitsOnlyUntilConsumerFreesCapacity()
    {
        var backlog =
            new DhmpSequentialBacklog(
                recordSize: 1,
                capacityRecords: 1,
                DhmpSequentialBacklogOverflowPolicy.Backpressure);

        backlog.Enqueue(new byte[] { 1 });

        using var entered =
            new ManualResetEventSlim(false);

        Task producer =
            Task.Run(
                () =>
                {
                    entered.Set();
                    backlog.Enqueue(new byte[] { 2 });
                },
                TestContext.Current.CancellationToken);

        entered.Wait(
            TestContext.Current.CancellationToken);

        await Task.Delay(
            20,
            TestContext.Current.CancellationToken);

        Assert.False(producer.IsCompleted);
        Assert.True(backlog.BackpressureWaits > 0);

        byte[] first = new byte[1];

        Assert.True(backlog.TryDequeue(first));
        Assert.Equal((byte)1, first[0]);

        await producer.WaitAsync(
            TestContext.Current.CancellationToken);

        byte[] second = new byte[1];

        Assert.True(backlog.TryDequeue(second));
        Assert.Equal((byte)2, second[0]);
        Assert.Equal(0, backlog.RecordsDropped);
    }

    [Fact]
    public void HappyFlow_UnboundedBacklogGrowsWithoutDropping()
    {
        var backlog =
            new DhmpSequentialBacklog(
                recordSize: 1,
                capacityRecords: 1,
                DhmpSequentialBacklogOverflowPolicy.Unbounded);

        for (int value = 0; value < 32; value++)
            backlog.Enqueue(new byte[] { (byte)value });

        Assert.Equal(32, backlog.Count);
        Assert.Equal(0, backlog.RecordsDropped);

        Span<byte> record =
            stackalloc byte[1];

        for (int value = 0; value < 32; value++)
        {
            Assert.True(backlog.TryDequeue(record));
            Assert.Equal((byte)value, record[0]);
        }
    }

    [Fact]
    public void HappyFlow_ServerSequentialUsesSharedSweeperAndFifoGrabber()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(2),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Sequential,
                    maximumPayloadBytes: 8,
                    sequentialBacklogMillions: 1,
                    sequentialBacklogOverflowPolicy:
                        DhmpSequentialBacklogOverflowPolicy.Backpressure));

        server.ProcessPacketToSequentialBacklog(
            new byte[]
            {
                1, 1,
                2, 2,
                3, 3,
                4, 4
            });

        Assert.True(server.SequentialGrabberAvailable);
        Assert.False(server.LatestGrabberAvailable);
        Assert.Equal(4, server.SequentialBacklogCount);

        Span<byte> record =
            stackalloc byte[2];

        for (byte value = 1; value <= 4; value++)
        {
            Assert.True(
                server.TryDequeueSequential(
                    record));

            Assert.Equal(
                new byte[] { value, value },
                record.ToArray());
        }

        Assert.False(
            server.TryDequeueSequential(
                record));
    }

    [Fact]
    public void HappyFlow_CanonicalProcessPacketUsesSequentialSweeperAndGrabber()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(2),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Sequential,
                    maximumPayloadBytes: 8,
                    sequentialBacklogMillions: 1,
                    sequentialBacklogOverflowPolicy:
                        DhmpSequentialBacklogOverflowPolicy.Backpressure));

        var published =
            new List<byte[]>();

        server.ProcessPacket(
            new byte[]
            {
                1, 1,
                2, 2,
                3, 3,
                4, 4
            },
            span => published.Add(span.ToArray()));

        Assert.Equal(4, server.ReceiveSweepRecordsObserved);
        Assert.Equal(0, server.SequentialBacklogCount);

        Assert.Single(published);

        Assert.Equal(
            new byte[]
            {
                1, 1,
                2, 2,
                3, 3,
                4, 4
            },
            published[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HappyFlow_CanonicalLatestPathUsesSharedSweeper(
        bool nativeSmoothing)
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(2),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    maximumPayloadBytes: 8,
                    nativeSmoothing: nativeSmoothing));

        byte[]? published = null;

        server.ProcessPacket(
            new byte[]
            {
                1, 1,
                2, 2,
                3, 3,
                4, 4
            },
            span => published = span.ToArray());

        Assert.Equal(1, server.ReceiveSweepRecordsObserved);
        Assert.Equal(new byte[] { 4, 4 }, published);
    }

    [Fact]
    public void BoundaryFlow_SequentialAndLatestSweepApisRemainModeSpecific()
    {
        var sequential =
            new DhmpServer(
                new DhmpWireContract(1),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Sequential,
                    maximumPayloadBytes: 1));

        Assert.Throws<InvalidOperationException>(
            () => sequential.BeginLatestSweep());

        var latest =
            new DhmpServer(
                new DhmpWireContract(1),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    maximumPayloadBytes: 1));

        Assert.Throws<InvalidOperationException>(
            () => latest.BeginSequentialSweep());
    }
}
