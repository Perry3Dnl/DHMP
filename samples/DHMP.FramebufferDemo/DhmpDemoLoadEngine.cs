using System.Buffers.Binary;
using System.Diagnostics;
using DHMP.Server;

internal sealed class DhmpDemoLoadEngine : BackgroundService
{
    public const int RecordSize = 16;
    public const double DirtyFractionPerFrame = 0.0025;
    public const int SimulatedFramesPerSecond = 60;
    public const int QueueCapacityRecords = 4 * 1024 * 1024;

    private readonly DhmpServer _server;
    private readonly object _configurationGate = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly long[] _queue = new long[QueueCapacityRecords];

    private int _width = 1280;
    private int _height = 720;
    private string _label = "720p";
    private long _cursor;
    private long _generation;

    // Single-producer / single-consumer monotonically increasing positions.
    private long _writePosition;
    private long _readPosition;

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

        long write = Volatile.Read(ref _writePosition);
        long read = Volatile.Read(ref _readPosition);
        long queueDepth = Math.Max(0, write - read);

        return new DhmpDemoSnapshot(
            _uptime.ElapsedMilliseconds,
            Interlocked.Read(ref _offeredRecords),
            Interlocked.Read(ref _acceptedRecords),
            Interlocked.Read(ref _droppedRecords),
            Interlocked.Read(ref _processedRecords),
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task producer = ProduceAsync(stoppingToken);
        Task consumer = ConsumeAsync(stoppingToken);

        await Task.WhenAll(producer, consumer);
    }

    private async Task ProduceAsync(CancellationToken stoppingToken)
    {
        const int SlicesPerSecond = 100;
        TimeSpan targetSlice = TimeSpan.FromSeconds(1d / SlicesPerSecond);
        long lastOfferTimestamp = Stopwatch.GetTimestamp();
        double fractionalOffer = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            long sliceStarted = Stopwatch.GetTimestamp();

            int width;
            int height;

            lock (_configurationGate)
            {
                width = _width;
                height = _height;
            }

            long targetPerSecond =
                CalculateTargetRecordsPerSecond(width, height);

            long now = Stopwatch.GetTimestamp();
            double elapsedOfferSeconds =
                (double)(now - lastOfferTimestamp) /
                Stopwatch.Frequency;
            lastOfferTimestamp = now;

            double exactOffer =
                targetPerSecond * elapsedOfferSeconds +
                fractionalOffer;

            long requested =
                Math.Max(0, (long)exactOffer);

            fractionalOffer =
                exactOffer - requested;

            if (requested > 0)
                Interlocked.Add(ref _offeredRecords, requested);

            long write = Volatile.Read(ref _writePosition);
            long read = Volatile.Read(ref _readPosition);
            long depth = Math.Max(0, write - read);
            long free = Math.Max(0, QueueCapacityRecords - depth);
            int accepted = (int)Math.Min(requested, free);
            long dropped = requested - accepted;

            for (int index = 0; index < accepted; index++)
            {
                long sequence = _cursor++;

                int slot =
                    (int)((write + index) % QueueCapacityRecords);

                _queue[slot] = sequence;
            }

            if (accepted > 0)
            {
                Volatile.Write(
                    ref _writePosition,
                    write + accepted);
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

            TimeSpan elapsed =
                Stopwatch.GetElapsedTime(sliceStarted);
            TimeSpan delay = targetSlice - elapsed;

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);
            else
                await Task.Yield();
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        byte[] record = new byte[RecordSize];

        while (!stoppingToken.IsCancellationRequested)
        {
            long read = Volatile.Read(ref _readPosition);
            long write = Volatile.Read(ref _writePosition);

            if (read >= write)
            {
                await Task.Yield();
                continue;
            }

            int width;
            int height;

            lock (_configurationGate)
            {
                width = _width;
                height = _height;
            }

            long pixelCount = (long)width * height;
            long available = write - read;
            int batch = (int)Math.Min(available, 262_144);
            int published = 0;

            for (int index = 0; index < batch; index++)
            {
                int slot =
                    (int)((read + index) % QueueCapacityRecords);
                long position = _queue[slot];

                long pixel = position % pixelCount;
                ushort x = (ushort)(pixel % width);
                ushort y = (ushort)(pixel / width);
                ulong generation =
                    unchecked((ulong)++_generation);

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

                span[12] = (byte)(position * 17);
                span[13] = (byte)(position * 31);
                span[14] = (byte)(position * 47);
                span[15] = 0;

                _server.ProcessPacket(
                    span,
                    _ => published++);
            }

            Volatile.Write(
                ref _readPosition,
                read + batch);

            Interlocked.Add(
                ref _processedRecords,
                batch);
            Interlocked.Add(
                ref _publishedRecords,
                published);
            Interlocked.Add(
                ref _processedRecordBytes,
                (long)batch * RecordSize);

            await Task.Yield();
        }
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
