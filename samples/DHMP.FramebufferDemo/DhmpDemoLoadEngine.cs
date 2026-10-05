using System.Buffers.Binary;
using System.Diagnostics;
using DHMP.Server;

internal sealed class DhmpDemoLoadEngine : BackgroundService
{
    public const int RecordSize = 16;
    public const double DirtyFractionPerFrame = 0.0025;
    public const int SimulatedFramesPerSecond = 60;

    private readonly DhmpServer _server;
    private readonly object _configurationGate = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private int _width = 1280;
    private int _height = 720;
    private string _label = "720p";
    private long _cursor;
    private long _generation;

    private long _receivedRecords;
    private long _publishedRecords;
    private long _receivedRecordBytes;
    private long _publishedRecordBytes;

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
            _cursor = 0;
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

        long targetRecordsPerSecond = CalculateTargetRecordsPerSecond(width, height);

        return new DhmpDemoSnapshot(
            _uptime.ElapsedMilliseconds,
            Interlocked.Read(ref _receivedRecords),
            Interlocked.Read(ref _publishedRecords),
            Interlocked.Read(ref _receivedRecordBytes),
            Interlocked.Read(ref _publishedRecordBytes),
            width,
            height,
            label,
            targetRecordsPerSecond,
            DirtyFractionPerFrame,
            SimulatedFramesPerSecond);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const int SlicesPerSecond = 50;
        TimeSpan targetSlice =
            TimeSpan.FromSeconds(1d / SlicesPerSecond);

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

            long targetPerSecond = CalculateTargetRecordsPerSecond(width, height);
            long requestedBatch =
                (targetPerSecond + SlicesPerSecond - 1) /
                SlicesPerSecond;

            int batchSize = (int)Math.Clamp(
                requestedBatch,
                1,
                10_000_000);

            ProcessBatch(width, height, batchSize);

            TimeSpan elapsed =
                Stopwatch.GetElapsedTime(sliceStarted);
            TimeSpan delay = targetSlice - elapsed;

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private void ProcessBatch(int width, int height, int count)
    {
        long pixelCount = (long)width * height;
        byte[] record = new byte[RecordSize];
        int published = 0;

        for (int index = 0; index < count; index++)
        {
            long position = Interlocked.Increment(ref _cursor) - 1;
            long pixel = position % pixelCount;
            ushort x = (ushort)(pixel % width);
            ushort y = (ushort)(pixel / width);
            ulong generation = unchecked(
                (ulong)Interlocked.Increment(ref _generation));

            Span<byte> span = record;
            BinaryPrimitives.WriteUInt16BigEndian(span[..2], x);
            BinaryPrimitives.WriteUInt16BigEndian(span.Slice(2, 2), y);
            BinaryPrimitives.WriteUInt64BigEndian(
                span.Slice(4, 8),
                generation);

            span[12] = (byte)(position * 17);
            span[13] = (byte)(position * 31);
            span[14] = (byte)(position * 47);
            span[15] = 0;

            _server.ProcessPacket(
                span,
                _ =>
                {
                    published++;
                });
        }

        Interlocked.Add(ref _receivedRecords, count);
        Interlocked.Add(
            ref _receivedRecordBytes,
            (long)count * RecordSize);
        Interlocked.Add(ref _publishedRecords, published);
        Interlocked.Add(
            ref _publishedRecordBytes,
            (long)published * RecordSize);
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

        return Math.Max(1, (long)Math.Round(target));
    }
}

internal sealed record DhmpDemoSnapshot(
    long UptimeMilliseconds,
    long ReceivedRecords,
    long PublishedRecords,
    long ReceivedRecordBytes,
    long PublishedRecordBytes,
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
