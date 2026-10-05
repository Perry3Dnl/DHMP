using System.Buffers.Binary;
using System.Diagnostics;
using DHMP.Server;

internal sealed class DhmpDemoLoadEngine : BackgroundService
{
    public const int RecordSize = 16;
    public const double DirtyFractionPerFrame = 0.0025;
    public const int SimulatedFramesPerSecond = 60;
    public const int QueueCapacityRecords = 4 * 1024 * 1024;

    private const int BatchQueueCapacity = 4096;
    private const int ConsumerChunkRecords = 262_144;

    private readonly DhmpServer _server;
    private readonly object _configurationGate = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly WorkBatch[] _batches =
        new WorkBatch[BatchQueueCapacity];

    private int _width = 1280;
    private int _height = 720;
    private string _label = "720p";
    private long _offerCursor;
    private long _generation;

    // SPSC descriptor positions. High-rate records themselves are not queued.
    private long _batchWritePosition;
    private long _batchReadPosition;

    private long _offeredRecords;
    private long _acceptedRecords;
    private long _droppedRecords;
    private long _processedRecords;
    private long _publishedRecords;
    private long _processedRecordBytes;

    public DhmpDemoLoadEngine(DhmpServer server)
    {
        _server = server;
    }

    public void Configure(int width, int height, string label)
    {
        lock (_configurationGate)
        {
            _width = width;
            _height = height;
            _label = label;
        }
    }

    public DhmpDemoSnapshot Snapshot()
    {
        int width;
        int height;
        string label;

        lock (_configurationGate)
        {
            width = _width;
            height = _height;
            label = _label;
        }

        long accepted =
            Interlocked.Read(ref _acceptedRecords);
        long processed =
            Interlocked.Read(ref _processedRecords);
        long queueDepth =
            Math.Max(0, accepted - processed);

        return new DhmpDemoSnapshot(
            _uptime.ElapsedMilliseconds,
            Interlocked.Read(ref _offeredRecords),
            accepted,
            Interlocked.Read(ref _droppedRecords),
            processed,
            Interlocked.Read(ref _publishedRecords),
            Interlocked.Read(ref _processedRecordBytes),
            queueDepth,
            QueueCapacityRecords,
            width,
            height,
            label,
            CalculateTargetRecordsPerSecond(width, height),
            DirtyFractionPerFrame,
            SimulatedFramesPerSecond);
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        Task producer = ProduceAsync(stoppingToken);
        Task consumer = ConsumeAsync(stoppingToken);

        await Task.WhenAll(producer, consumer);
    }

    private async Task ProduceAsync(
        CancellationToken stoppingToken)
    {
        const int SlicesPerSecond = 100;

        TimeSpan targetSlice =
            TimeSpan.FromSeconds(1d / SlicesPerSecond);

        long lastOfferTimestamp =
            Stopwatch.GetTimestamp();

        double fractionalOffer = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            long sliceStarted =
                Stopwatch.GetTimestamp();

            int width;
            int height;

            lock (_configurationGate)
            {
                width = _width;
                height = _height;
            }

            long targetPerSecond =
                CalculateTargetRecordsPerSecond(
                    width,
                    height);

            long now = Stopwatch.GetTimestamp();

            double elapsedOfferSeconds =
                (double)(now - lastOfferTimestamp) /
                Stopwatch.Frequency;

            lastOfferTimestamp = now;

            double exactOffer =
                targetPerSecond *
                elapsedOfferSeconds +
                fractionalOffer;

            long requested =
                Math.Max(0, (long)exactOffer);

            fractionalOffer =
                exactOffer - requested;

            if (requested > 0)
            {
                Interlocked.Add(
                    ref _offeredRecords,
                    requested);

                OfferBatch(
                    requested,
                    width,
                    height);
            }

            TimeSpan elapsed =
                Stopwatch.GetElapsedTime(
                    sliceStarted);

            TimeSpan delay =
                targetSlice - elapsed;

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(
                    delay,
                    stoppingToken);
            }
            else
            {
                await Task.Yield();
            }
        }
    }

    private void OfferBatch(
        long requested,
        int width,
        int height)
    {
        long acceptedTotal =
            Interlocked.Read(ref _acceptedRecords);

        long processedTotal =
            Interlocked.Read(ref _processedRecords);

        long queuedRecords =
            Math.Max(
                0,
                acceptedTotal - processedTotal);

        long freeRecords =
            Math.Max(
                0,
                QueueCapacityRecords - queuedRecords);

        long batchWrite =
            Volatile.Read(ref _batchWritePosition);

        long batchRead =
            Volatile.Read(ref _batchReadPosition);

        bool descriptorAvailable =
            batchWrite - batchRead <
            BatchQueueCapacity;

        int accepted = descriptorAvailable
            ? (int)Math.Min(
                requested,
                freeRecords)
            : 0;

        long dropped =
            requested - accepted;

        long sequenceStart =
            _offerCursor;

        // Advance through the full conceptual offered stream,
        // including records rejected by backpressure.
        _offerCursor += requested;

        if (accepted > 0)
        {
            int slot =
                (int)(
                    batchWrite %
                    BatchQueueCapacity);

            _batches[slot] =
                new WorkBatch(
                    sequenceStart,
                    accepted,
                    width,
                    height);

            Volatile.Write(
                ref _batchWritePosition,
                batchWrite + 1);

            Interlocked.Add(
                ref _acceptedRecords,
                accepted);
        }

        if (dropped > 0)
        {
            Interlocked.Add(
                ref _droppedRecords,
                dropped);
        }
    }

    private async Task ConsumeAsync(
        CancellationToken stoppingToken)
    {
        byte[] record =
            new byte[RecordSize];

        int currentOffset = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            long batchRead =
                Volatile.Read(
                    ref _batchReadPosition);

            long batchWrite =
                Volatile.Read(
                    ref _batchWritePosition);

            if (batchRead >= batchWrite)
            {
                await Task.Yield();
                continue;
            }

            int slot =
                (int)(
                    batchRead %
                    BatchQueueCapacity);

            WorkBatch batch =
                _batches[slot];

            int remaining =
                batch.Count - currentOffset;

            int chunk =
                Math.Min(
                    remaining,
                    ConsumerChunkRecords);

            int published =
                ProcessRange(
                    record,
                    batch,
                    currentOffset,
                    chunk);

            currentOffset += chunk;

            Interlocked.Add(
                ref _processedRecords,
                chunk);

            Interlocked.Add(
                ref _publishedRecords,
                published);

            Interlocked.Add(
                ref _processedRecordBytes,
                (long)chunk * RecordSize);

            if (currentOffset >= batch.Count)
            {
                currentOffset = 0;

                Volatile.Write(
                    ref _batchReadPosition,
                    batchRead + 1);
            }

            await Task.Yield();
        }
    }

    private int ProcessRange(
        byte[] record,
        WorkBatch batch,
        int offset,
        int count)
    {
        int published = 0;
        long pixelCount =
            (long)batch.Width *
            batch.Height;

        for (int index = 0; index < count; index++)
        {
            long position =
                batch.SequenceStart +
                offset +
                index;

            long pixel =
                position % pixelCount;

            ushort x =
                (ushort)(pixel % batch.Width);

            ushort y =
                (ushort)(pixel / batch.Width);

            ulong generation =
                unchecked(
                    (ulong)++_generation);

            Span<byte> span = record;

            BinaryPrimitives.WriteUInt16BigEndian(
                span[..2],
                x);

            BinaryPrimitives.WriteUInt16BigEndian(
                span.Slice(2, 2),
                y);

            BinaryPrimitives.WriteUInt64BigEndian(
                span.Slice(4, 8),
                generation);

            span[12] =
                (byte)(position * 17);

            span[13] =
                (byte)(position * 31);

            span[14] =
                (byte)(position * 47);

            span[15] = 0;

            _server.ProcessPacket(
                span,
                _ => published++);
        }

        return published;
    }

    private static long CalculateTargetRecordsPerSecond(
        int width,
        int height)
    {
        double target =
            (double)width *
            height *
            SimulatedFramesPerSecond *
            DirtyFractionPerFrame;

        return Math.Max(
            1,
            (long)Math.Round(target));
    }

    private readonly record struct WorkBatch(
        long SequenceStart,
        int Count,
        int Width,
        int Height);
}

internal sealed record DhmpDemoSnapshot(
    long UptimeMilliseconds,
    long OfferedRecords,
    long AcceptedRecords,
    long DroppedRecords,
    long ProcessedRecords,
    long PublishedRecords,
    long ProcessedRecordBytes,
    long QueueDepth,
    int QueueCapacityRecords,
    int Width,
    int Height,
    string Label,
    long TargetRecordsPerSecond,
    double DirtyFractionPerFrame,
    int SimulatedFramesPerSecond);

internal sealed record DhmpDemoWorkloadRequest(
    int Width,
    int Height,
    string Label);
