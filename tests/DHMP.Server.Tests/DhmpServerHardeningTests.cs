using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DHMP.Protocol;
using Xunit;

namespace DHMP.Server.Tests;

public sealed class DhmpServerHardeningTests
{
    [Fact]
    public void HappyFlow_ReceiveRegion_LifecycleReturnsToPool()
    {
        var pool =
            new DHMPReceiveRegionPool(
                regionCount: 2,
                regionBytes: 64);

        Assert.True(
            pool.TryAcquire(
                out var region));

        Assert.NotNull(region);
        Assert.Equal(64, region!.Capacity);

        region.Buffer[0] = 0x42;

        Assert.False(
            region.TryBorrow());

        region.Publish();

        Assert.True(
            region.TryBorrow());

        Assert.False(
            region.TryBorrow());

        Assert.Equal(
            (byte)0x42,
            region.Buffer[0]);

        region.Release();

        Assert.True(
            pool.TryAcquire(
                out var reacquired));

        Assert.NotNull(reacquired);

        reacquired!.Release();
    }

    [Fact]
    public void CriticalFlow_ReceivePool_IsStrictlyBounded()
    {
        var pool =
            new DHMPReceiveRegionPool(
                regionCount: 2,
                regionBytes: 32);

        Assert.True(
            pool.TryAcquire(
                out var first));

        Assert.True(
            pool.TryAcquire(
                out var second));

        Assert.False(
            pool.TryAcquire(
                out var exhausted));

        Assert.Null(exhausted);

        first!.Release();

        Assert.True(
            pool.TryAcquire(
                out var replacement));

        Assert.Same(
            first,
            replacement);

        replacement!.Release();
        second!.Release();
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(1, 32)]
    [InlineData(2, 0)]
    [InlineData(2, -1)]
    public void CriticalFlow_ReceivePool_InvalidConfigurationIsRejected(
        int regionCount,
        int regionBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DHMPReceiveRegionPool(
                regionCount,
                regionBytes));
    }

    [Fact]
    public void CriticalFlow_ReceivePool_ConcurrentAcquisitionNeverDoubleLeasesRegion()
    {
        var pool =
            new DHMPReceiveRegionPool(
                regionCount: 8,
                regionBytes: 64);

        var active =
            new ConcurrentDictionary<
                DHMPReceiveRegion,
                byte>();

        int collisions = 0;
        int lifecycleFailures = 0;

        Parallel.For(
            0,
            20_000,
            iteration =>
            {
                DHMPReceiveRegion? region;

                while (!pool.TryAcquire(
                    out region))
                {
                    Thread.Yield();
                }

                if (!active.TryAdd(
                        region!,
                        0))
                    Interlocked.Increment(
                        ref collisions);

                region!.Publish();

                if (!region.TryBorrow())
                    Interlocked.Increment(
                        ref lifecycleFailures);

                active.TryRemove(
                    region,
                    out byte _);

                region.Release();
            });

        Assert.Equal(0, collisions);
        Assert.Equal(0, lifecycleFailures);
        Assert.Empty(active);
    }

    [Fact]
    public void HappyFlow_ModelBoundary_ProvidesTypedZeroCopyRecords()
    {
        var boundary =
            new DhmpModelBoundary<TestModel>(
                Marshal.SizeOf<TestModel>());

        TestModel[] values =
        [
            new TestModel
            {
                A = 10,
                B = 20
            },
            new TestModel
            {
                A = 30,
                B = 40
            }
        ];

        ReadOnlySpan<byte> bytes =
            MemoryMarshal.AsBytes(
                values.AsSpan());

        ReadOnlySpan<TestModel> models =
            boundary.Cast(bytes);

        Assert.Equal(2, models.Length);
        Assert.Equal(10, models[0].A);
        Assert.Equal(40, models[1].B);
    }

    [Fact]
    public void CriticalFlow_ModelBoundary_RejectsSchemaAndPartialRecordMismatch()
    {
        Assert.Throws<ArgumentException>(() =>
            new DhmpModelBoundary<TestModel>(
                packageSize: 1));

        var boundary =
            new DhmpModelBoundary<TestModel>(
                Marshal.SizeOf<TestModel>());

        Assert.Throws<ArgumentException>(() =>
            boundary.Cast(
                new byte[
                    Marshal.SizeOf<TestModel>() + 1]));
    }

    [Fact]
    public void HappyFlow_TypedSpan_ExposesAllModels()
    {
        TestModel[] values =
        [
            new TestModel { A = 1, B = 2 },
            new TestModel { A = 3, B = 4 }
        ];

        ReadOnlySpan<byte> bytes =
            MemoryMarshal.AsBytes(
                values.AsSpan());

        var typed =
            new DHMPTypedSpan<TestModel>(
                bytes);

        Assert.Equal(2, typed.Count);
        Assert.Equal(1, typed.Models[0].A);
        Assert.Equal(4, typed.Models[1].B);
    }

    [Fact]
    public void HappyFlow_Ring3RetainsNewestThreeAcrossPackets()
    {
        var window = new DhmpLatestStateWindow(2);
        window.PublishPacket(new byte[] { 1, 1 });
        window.PublishPacket(new byte[] { 2, 2 });
        window.PublishPacket(new byte[] { 3, 3 });
        window.PublishPacket(new byte[] { 4, 4 });

        Span<byte> destination = stackalloc byte[6];
        int count = window.CopyNewestTo(destination);

        Assert.Equal(3, count);
        Assert.Equal(new byte[] { 2, 2, 3, 3, 4, 4 }, destination.ToArray());
        Assert.Equal(4, window.RecordsObserved);
        Assert.Equal(1, window.RecordsOverwritten);
    }

    [Fact]
    public void HappyFlow_Ring3SkipsObsoleteRecordsInsideLargePacket()
    {
        var window = new DhmpLatestStateWindow(2);
        window.PublishPacket(new byte[] { 1,1, 2,2, 3,3, 4,4, 5,5 });

        Span<byte> destination = stackalloc byte[6];
        int count = window.CopyNewestTo(destination);

        Assert.Equal(3, count);
        Assert.Equal(new byte[] { 3,3, 4,4, 5,5 }, destination.ToArray());
        Assert.Equal(5, window.RecordsObserved);
        Assert.Equal(2, window.RecordsOverwritten);
    }

    [Fact]
    public void HappyFlow_FusedLatestFastPathPreservesArrivalRingAndNewestGrab()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(2),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    maximumPayloadBytes: 16,
                    nativeSmoothing: true));

        byte[]? published = null;

        server.ProcessPacket(
            new byte[] { 1,1, 2,2, 3,3, 4,4 },
            span => published = span.ToArray());

        Assert.Equal(
            new byte[] { 4,4 },
            published);

        Assert.Equal(
            4,
            server.ReceiveSweepRecordsObserved);

        Span<byte> window =
            stackalloc byte[6];

        Assert.Equal(
            3,
            server.CopyNativeSmoothingWindow(window));

        Assert.Equal(
            new byte[] { 2,2, 3,3, 4,4 },
            window.ToArray());

        server.ProcessPacket(
            new byte[] { 5,5 },
            span => published = span.ToArray());

        Assert.Equal(
            new byte[] { 5,5 },
            published);

        Assert.Equal(
            5,
            server.ReceiveSweepRecordsObserved);

        Assert.Equal(
            3,
            server.CopyNativeSmoothingWindow(window));

        Assert.Equal(
            new byte[] { 3,3, 4,4, 5,5 },
            window.ToArray());
    }

    [Fact]
    public void HappyFlow_ServerLatestAndNativeSmoothingSharePacketHotPath()
    {
        var latest =
            new DhmpServer(
                new DhmpWireContract(2),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    maximumPayloadBytes: 16));

        var smoothing =
            new DhmpServer(
                new DhmpWireContract(2),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    maximumPayloadBytes: 16,
                    nativeSmoothing: true));

        byte[]? latestPublished = null;
        byte[]? smoothingPublished = null;
        byte[] packet = [1,1, 2,2, 3,3];

        latest.ProcessPacket(
            packet,
            span => latestPublished = span.ToArray());

        smoothing.ProcessPacket(
            packet,
            span => smoothingPublished = span.ToArray());

        Assert.Equal(
            new byte[] { 3,3 },
            latestPublished);

        Assert.Equal(
            latestPublished,
            smoothingPublished);

        // Every received record enters the shared physical Ring-3 before the
        // grabber chooses Latest or Native Smoothing behavior. This payload
        // contains three records, so the sweeper has observed N/N+1/N+2 and
        // smoothing has a complete three-record window available.
        Assert.Equal(
            3,
            latest.ReceiveSweepRecordsObserved);

        Assert.Equal(
            latest.ReceiveSweepRecordsObserved,
            smoothing.ReceiveSweepRecordsObserved);

        Assert.Equal(
            3,
            smoothing.NativeSmoothingRecordCount);

        Span<byte> smoothingWindow =
            stackalloc byte[6];

        Assert.Equal(
            3,
            smoothing.CopyNativeSmoothingWindow(
                smoothingWindow));

        Assert.Equal(
            packet,
            smoothingWindow.ToArray());
    }

    [Fact]
    public void HappyFlow_LatestGrabberUsesCurrentCompletedSweepWithoutWaitingForThree()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(2),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    maximumPayloadBytes: 16,
                    nativeSmoothing: true));

        Span<byte> first =
            server.BeginLatestSweep();

        first[0] = 1;
        first[1] = 1;
        server.CommitLatestSweep();

        Span<byte> latest =
            stackalloc byte[2];

        Assert.Equal(
            1,
            server.CopyLatest(latest));

        Assert.Equal(
            new byte[] { 1,1 },
            latest.ToArray());

        Span<byte> incompleteWindow =
            stackalloc byte[6];

        Assert.Equal(
            0,
            server.CopyNativeSmoothingWindow(
                incompleteWindow));

        // While the sweeper is filling the next slot, Latest still grabs the
        // last fully published slot immediately.
        Span<byte> second =
            server.BeginLatestSweep();

        second[0] = 2;
        second[1] = 2;

        Assert.Equal(
            1,
            server.CopyLatest(latest));

        Assert.Equal(
            new byte[] { 1,1 },
            latest.ToArray());

        server.CommitLatestSweep();
    }

    [Fact]
    public void HappyFlow_NativeSmoothingZeroCopyGrabberBorrowsRingSlots()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(2),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    maximumPayloadBytes: 16,
                    nativeSmoothing: true));

        foreach (byte value in new byte[] { 1, 2, 3 })
        {
            Span<byte> slot =
                server.BeginLatestSweep();

            slot[0] = value;
            slot[1] = value;
            server.CommitLatestSweep();
        }

        byte[] observed = new byte[6];

        int count =
            server.ConsumeNativeSmoothingWindow(
                (oldest, middle, newest) =>
                {
                    oldest.CopyTo(observed.AsSpan(0, 2));
                    middle.CopyTo(observed.AsSpan(2, 2));
                    newest.CopyTo(observed.AsSpan(4, 2));
                });

        Assert.Equal(3, count);
        Assert.Equal(
            new byte[] { 1,1, 2,2, 3,3 },
            observed);
    }

    [Fact]
    public void HappyFlow_NativeSmoothingGrabberUsesExactlyThreeCompletedSweepSlots()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(2),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    maximumPayloadBytes: 16,
                    nativeSmoothing: true));

        foreach (byte value in new byte[] { 1, 2, 3 })
        {
            Span<byte> slot =
                server.BeginLatestSweep();

            slot[0] = value;
            slot[1] = value;

            server.CommitLatestSweep();
        }

        Span<byte> window =
            stackalloc byte[6];

        Assert.Equal(
            3,
            server.CopyNativeSmoothingWindow(
                window));

        Assert.Equal(
            new byte[] { 1,1, 2,2, 3,3 },
            window.ToArray());

        Span<byte> next =
            server.BeginLatestSweep();

        next[0] = 4;
        next[1] = 4;

        server.CommitLatestSweep();

        Assert.Equal(
            3,
            server.CopyNativeSmoothingWindow(
                window));

        Assert.Equal(
            new byte[] { 2,2, 3,3, 4,4 },
            window.ToArray());
    }

    [Fact]
    public void BoundaryFlow_ServerWithoutNativeSmoothingDoesNotExposeThreeSlotGrab()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(2),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    maximumPayloadBytes: 16));

        Span<byte> slot =
            server.BeginLatestSweep();

        slot[0] = 1;
        slot[1] = 1;

        server.CommitLatestSweep();

        Span<byte> latest =
            stackalloc byte[2];

        Assert.True(
            server.LatestGrabberAvailable);

        Assert.Equal(
            1,
            server.CopyLatest(latest));

        Span<byte> destination =
            stackalloc byte[6];

        Assert.False(
            server.NativeSmoothingEnabled);

        Assert.Equal(
            0,
            server.CopyNativeSmoothingWindow(
                destination));
    }

    [Fact]
    public void CriticalFlow_Ring3ConcurrentReaderNeverReturnsTornRecords()
    {
        const int RecordSize = 16;
        const int WriterIterations = 250_000;
        const int FinalReaderIterations = 1_000;

        var window =
            new DhmpLatestStateWindow(
                RecordSize);

        using var startGate =
            new ManualResetEventSlim(false);

        int writerDone = 0;
        long readerSnapshots = 0;
        Exception? readerFailure = null;

        var writer =
            new Thread(() =>
            {
                byte[] record =
                    new byte[RecordSize];

                startGate.Wait();

                try
                {
                    for (long sequence = 1;
                         sequence <= WriterIterations;
                         sequence++)
                    {
                        BitConverter.TryWriteBytes(
                            record.AsSpan(0, 8),
                            sequence);

                        BitConverter.TryWriteBytes(
                            record.AsSpan(8, 8),
                            ~sequence);

                        window.PublishValidatedPacket(
                            record);
                    }
                }
                finally
                {
                    Volatile.Write(
                        ref writerDone,
                        1);
                }
            });

        var reader =
            new Thread(() =>
            {
                try
                {
                    byte[] snapshot =
                        new byte[
                            RecordSize *
                            DhmpLatestStateWindow.Capacity];

                    startGate.Wait();

                    while (Volatile.Read(ref writerDone) == 0)
                    {
                        ValidateSnapshot(
                            window,
                            snapshot);

                        readerSnapshots++;
                    }

                    for (int iteration = 0;
                         iteration < FinalReaderIterations;
                         iteration++)
                    {
                        ValidateSnapshot(
                            window,
                            snapshot);

                        readerSnapshots++;
                    }
                }
                catch (Exception exception)
                {
                    readerFailure = exception;
                }
            });

        writer.Start();
        reader.Start();
        startGate.Set();

        Assert.True(
            writer.Join(
                TimeSpan.FromSeconds(10)));

        Assert.True(
            reader.Join(
                TimeSpan.FromSeconds(10)));

        Assert.Null(
            readerFailure);

        Assert.True(
            readerSnapshots >= FinalReaderIterations);

        static void ValidateSnapshot(
            DhmpLatestStateWindow window,
            byte[] snapshot)
        {
            int count =
                window.CopyNewestTo(
                    snapshot);

            for (int index = 0;
                 index < count;
                 index++)
            {
                ReadOnlySpan<byte> record =
                    snapshot.AsSpan(
                        index * RecordSize,
                        RecordSize);

                long sequence =
                    BitConverter.ToInt64(
                        record[..8]);

                long inverse =
                    BitConverter.ToInt64(
                        record[8..]);

                if (~sequence != inverse)
                {
                    throw new InvalidOperationException(
                        "Ring-3 returned a torn record.");
                }
            }
        }
    }

    [Fact]
    public void CriticalFlow_ServerSweeperNeverBlocksOnConcurrentNativeSmoothingGrabber()
    {
        const int RecordSize = 16;
        const int WriterIterations = 250_000;
        const int FinalReaderIterations = 1_000;

        var server =
            new DhmpServer(
                new DhmpWireContract(RecordSize),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    maximumPayloadBytes: RecordSize,
                    nativeSmoothing: true));

        using var startGate =
            new ManualResetEventSlim(false);

        int writerDone = 0;
        long readerSnapshots = 0;
        Exception? readerFailure = null;

        var writer =
            new Thread(() =>
            {
                startGate.Wait();

                try
                {
                    for (long sequence = 1;
                         sequence <= WriterIterations;
                         sequence++)
                    {
                        Span<byte> slot =
                            server.BeginLatestSweep();

                        BitConverter.TryWriteBytes(
                            slot[..8],
                            sequence);

                        BitConverter.TryWriteBytes(
                            slot[8..],
                            ~sequence);

                        server.CommitLatestSweep();
                    }
                }
                finally
                {
                    Volatile.Write(
                        ref writerDone,
                        1);
                }
            });

        var reader =
            new Thread(() =>
            {
                try
                {
                    byte[] snapshot =
                        new byte[
                            RecordSize *
                            DhmpLatestStateWindow.Capacity];

                    startGate.Wait();

                    while (Volatile.Read(ref writerDone) == 0)
                    {
                        ValidateSnapshot(
                            server,
                            snapshot);

                        readerSnapshots++;
                    }

                    for (int iteration = 0;
                         iteration < FinalReaderIterations;
                         iteration++)
                    {
                        ValidateSnapshot(
                            server,
                            snapshot);

                        readerSnapshots++;
                    }
                }
                catch (Exception exception)
                {
                    readerFailure = exception;
                }
            });

        writer.Start();
        reader.Start();
        startGate.Set();

        Assert.True(
            writer.Join(
                TimeSpan.FromSeconds(10)));

        Assert.True(
            reader.Join(
                TimeSpan.FromSeconds(10)));

        Assert.Null(
            readerFailure);

        Assert.True(
            readerSnapshots >= FinalReaderIterations);

        static void ValidateSnapshot(
            DhmpServer server,
            byte[] snapshot)
        {
            int count =
                server.CopyNativeSmoothingWindow(
                    snapshot);

            Assert.True(
                count is 0 or
                DhmpLatestStateWindow.Capacity);

            for (int index = 0;
                 index < count;
                 index++)
            {
                ReadOnlySpan<byte> record =
                    snapshot.AsSpan(
                        index * RecordSize,
                        RecordSize);

                long sequence =
                    BitConverter.ToInt64(
                        record[..8]);

                long inverse =
                    BitConverter.ToInt64(
                        record[8..]);

                if (~sequence != inverse)
                {
                    throw new InvalidOperationException(
                        "Native-smoothing grabber returned a torn sweep slot.");
                }
            }
        }
    }

    [Fact]
    public void CriticalFlow_LatestFilter_ResetRestoresInitialAcceptanceState()
    {
        var filter =
            new DhmpLatestGenerationFilter(16);

        Assert.True(
            filter.TryPublish(
                Record(100),
                _ => { }));

        Assert.False(
            filter.TryPublish(
                Record(99),
                _ => { }));

        Assert.Equal(1, filter.AcceptedRecords);
        Assert.Equal(1, filter.StaleRecords);

        filter.Reset();

        Assert.False(filter.HasGeneration);
        Assert.Equal(0UL, filter.LatestGeneration);
        Assert.Equal(0, filter.AcceptedRecords);
        Assert.Equal(0, filter.StaleRecords);

        Assert.True(
            filter.TryPublish(
                Record(1),
                _ => { }));
    }

    [Fact]
    public void CriticalFlow_LatestFilter_InvalidConfigurationAndRecordLengthAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpLatestGenerationFilter(
                recordSize: 7));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpLatestGenerationFilter(
                recordSize: 16,
                generationOffset: 9));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpLatestGenerationFilter(
                recordSize: 16,
                byteOrder:
                    (DhmpGenerationByteOrder)99));

        var filter =
            new DhmpLatestGenerationFilter(16);

        Assert.Throws<ArgumentException>(() =>
            filter.TryPublish(
                new byte[15],
                _ => { }));

        Assert.Throws<ArgumentNullException>(() =>
            filter.TryPublish(
                new byte[16],
                null!));
    }

    [Fact]
    public void CriticalFlow_LatestFilter_CallbackFailureIsNotRetried()
    {
        var filter =
            new DhmpLatestGenerationFilter(16);

        Assert.Throws<InvalidOperationException>(() =>
            filter.TryPublish(
                Record(7),
                _ => throw new InvalidOperationException(
                    "consumer failed")));

        Assert.True(filter.HasGeneration);
        Assert.Equal(7UL, filter.LatestGeneration);
        Assert.Equal(1, filter.AcceptedRecords);

        int callbacks = 0;

        Assert.False(
            filter.TryPublish(
                Record(7),
                _ => callbacks++));

        Assert.Equal(0, callbacks);
        Assert.Equal(1, filter.StaleRecords);
    }

    [Fact]
    public async Task HappyFlow_Dispatcher_CanRestartAfterCleanCancellation()
    {
        int calls = 0;

        using var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.SequentialReject,
                sequentialCapacity: 2,
                (_, _) =>
                {
                    Interlocked.Increment(
                        ref calls);

                    return ValueTask.CompletedTask;
                });

        using (var firstCancel =
               new CancellationTokenSource())
        {
            Task first =
                dispatcher.RunAsync(
                    firstCancel.Token);

            firstCancel.Cancel();

            await first;
        }

        Assert.True(
            dispatcher.TryPublish(
                new byte[] { 1 }));

        using var secondCancel =
            new CancellationTokenSource();

        Task second =
            dispatcher.RunAsync(
                secondCancel.Token);

        await WaitUntilAsync(
            () => Volatile.Read(ref calls) == 1,
            TestContext.Current.CancellationToken);

        secondCancel.Cancel();

        await second;

        Assert.Equal(1, dispatcher.ConsumedBatches);
    }

    [Fact]
    public async Task CriticalFlow_Dispatcher_RejectsSecondConsumerLoop()
    {
        using var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.SequentialReject,
                sequentialCapacity: 1,
                (_, _) => ValueTask.CompletedTask);

        using var cancellation =
            new CancellationTokenSource();

        Task first =
            dispatcher.RunAsync(
                cancellation.Token);

        await Task.Yield();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.RunAsync(
                TestContext.Current.CancellationToken));

        cancellation.Cancel();

        await first;
    }

    [Fact]
    public void CriticalFlow_Dispatcher_ConcurrentSequentialPublishersRespectExactCapacity()
    {
        using var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.SequentialReject,
                sequentialCapacity: 64,
                (_, _) => ValueTask.CompletedTask);

        int accepted = 0;

        Parallel.For(
            0,
            1000,
            i =>
            {
                if (dispatcher.TryPublish(
                        BitConverter.GetBytes(i)))
                    Interlocked.Increment(
                        ref accepted);
            });

        var snapshot =
            dispatcher.GetSnapshot();

        Assert.Equal(64, accepted);
        Assert.Equal(64, snapshot.PendingBatches);
        Assert.Equal(64, snapshot.AcceptedBatches);
        Assert.Equal(936, snapshot.SaturationDrops);
        Assert.True(snapshot.IsSaturated);
    }

    [Fact]
    public void CriticalFlow_Dispatcher_ConcurrentLatestPublishersRemainSingleSlot()
    {
        using var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.LatestReplace,
                sequentialCapacity: 8,
                (_, _) => ValueTask.CompletedTask);

        Parallel.For(
            0,
            1000,
            i => Assert.True(
                dispatcher.TryPublish(
                    BitConverter.GetBytes(i))));

        var snapshot =
            dispatcher.GetSnapshot();

        Assert.Equal(1, snapshot.Capacity);
        Assert.Equal(1, snapshot.PendingBatches);
        Assert.Equal(1000, snapshot.AcceptedBatches);
        Assert.Equal(999, snapshot.ReplacedBatches);
        Assert.Equal(0, snapshot.SaturationDrops);
    }

    [Fact]
    public async Task CriticalFlow_DisposedDispatcherRejectsPublicationAndRun()
    {
        var dispatcher =
            new DhmpBoundedReceiveDispatcher(
                DhmpReceiveDispatchMode.SequentialReject,
                sequentialCapacity: 2,
                (_, _) => ValueTask.CompletedTask);

        Assert.True(
            dispatcher.TryPublish(
                new byte[] { 1 }));

        dispatcher.Dispose();

        Assert.False(
            dispatcher.TryPublish(
                new byte[] { 2 }));

        Assert.Equal(0, dispatcher.PendingBatches);

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => dispatcher.RunAsync(
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CriticalFlow_CongestionAdvisorRejectsInvalidOrRegressingSnapshots()
    {
        Assert.Throws<ArgumentException>(() =>
            DhmpCongestionAdvisor.Evaluate(
                default,
                default));

        var previous =
            new DhmpReceiveDispatchSnapshot(
                DhmpReceiveDispatchMode.SequentialReject,
                Capacity: 4,
                PendingBatches: 1,
                AcceptedBatches: 10,
                ConsumedBatches: 9,
                SaturationDrops: 0,
                ReplacedBatches: 0);

        var regressed =
            previous with
            {
                AcceptedBatches = 9
            };

        Assert.Throws<ArgumentException>(() =>
            DhmpCongestionAdvisor.Evaluate(
                previous,
                regressed));
    }

    [Fact]
    public void BoundaryFlow_CongestionAdvisor_ThreeQuarterQueueIsSoftPressure()
    {
        var previous =
            new DhmpReceiveDispatchSnapshot(
                DhmpReceiveDispatchMode.SequentialReject,
                Capacity: 4,
                PendingBatches: 2,
                AcceptedBatches: 10,
                ConsumedBatches: 8,
                SaturationDrops: 0,
                ReplacedBatches: 0);

        var current =
            new DhmpReceiveDispatchSnapshot(
                DhmpReceiveDispatchMode.SequentialReject,
                Capacity: 4,
                PendingBatches: 3,
                AcceptedBatches: 11,
                ConsumedBatches: 8,
                SaturationDrops: 0,
                ReplacedBatches: 0);

        var feedback =
            DhmpCongestionAdvisor.Evaluate(
                previous,
                current);

        Assert.Equal(
            DhmpCongestionPressure.Soft,
            feedback.Pressure);

        Assert.Equal(
            (ushort)750,
            feedback.RateScalePermille);
    }

    private static byte[] Record(
        ulong generation)
    {
        byte[] record = new byte[16];

        BinaryPrimitives.WriteUInt64BigEndian(
            record,
            generation);

        return record;
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < 1000; i++)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            if (condition())
                return;

            await Task.Delay(
                1,
                cancellationToken);
        }

        throw new TimeoutException(
            "Condition did not become true.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestModel
    {
        public int A;
        public int B;
    }
}
