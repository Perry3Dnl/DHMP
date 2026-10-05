using System.Buffers.Binary;
using System.Diagnostics;
using DHMP.Protocol;
using DHMP.Server;

internal sealed class DhmpScadaDemoEngine : BackgroundService
{
    public const int RecordSize = 16;

    private readonly DhmpServer _server;
    private readonly object _gate = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private int _activeTags = 100_000;
    private string _profile = "regional-grid";
    private long _cursor;
    private long _generation;

    private long _receivedRecords;
    private long _publishedRecords;
    private long _receivedBytes;
    private long _publishedBytes;

    private double _frequencyHz = 50.00;
    private double _northBusKv = 219.8;
    private double _southBusKv = 220.4;
    private double _gridLoadMw = 684.0;
    private double _transformerTempC = 63.0;
    private int _breakerClosed = 1;
    private int _alarmCount = 0;

    public DhmpScadaDemoEngine()
    {
        var wire = new DhmpWireContract(RecordSize);
        var receivePolicy = new DhmpReceivePolicy(
            DhmpProcessingMode.Latest,
            maximumPayloadBytes: RecordSize);
        _server = new DhmpServer(wire, receivePolicy);
    }

    public void Configure(int activeTags, string profile)
    {
        lock (_gate)
        {
            _activeTags = activeTags;
            _profile = profile;
            _cursor = 0;
        }
    }

    public DhmpScadaSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DhmpScadaSnapshot(
                _uptime.ElapsedMilliseconds,
                Interlocked.Read(ref _receivedRecords),
                Interlocked.Read(ref _publishedRecords),
                Interlocked.Read(ref _receivedBytes),
                Interlocked.Read(ref _publishedBytes),
                _activeTags,
                _profile,
                TargetRecordsPerSecond(_activeTags),
                _frequencyHz,
                _northBusKv,
                _southBusKv,
                _gridLoadMw,
                _transformerTempC,
                _breakerClosed != 0,
                _alarmCount);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        const int slicesPerSecond = 50;
        TimeSpan targetSlice = TimeSpan.FromSeconds(1d / slicesPerSecond);

        while (!stoppingToken.IsCancellationRequested)
        {
            long started = Stopwatch.GetTimestamp();

            int activeTags;
            lock (_gate)
            {
                activeTags = _activeTags;
            }

            long targetPerSecond = TargetRecordsPerSecond(activeTags);
            int batchSize = (int)Math.Clamp(
                (targetPerSecond + slicesPerSecond - 1) / slicesPerSecond,
                1,
                200_000);

            ProcessBatch(activeTags, batchSize);
            UpdateRepresentativePlantState();

            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            TimeSpan delay = targetSlice - elapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, stoppingToken);
        }
    }

    private void ProcessBatch(int activeTags, int count)
    {
        byte[] record = new byte[RecordSize];
        int published = 0;

        for (int i = 0; i < count; i++)
        {
            long position = Interlocked.Increment(ref _cursor) - 1;
            uint tagId = (uint)(position % activeTags);
            ulong generation = unchecked(
                (ulong)Interlocked.Increment(ref _generation));

            float value = GenerateValue(tagId, generation);

            Span<byte> span = record;
            BinaryPrimitives.WriteUInt32BigEndian(span[..4], tagId);
            BinaryPrimitives.WriteUInt64BigEndian(span.Slice(4, 8), generation);
            BinaryPrimitives.WriteInt32BigEndian(
                span.Slice(12, 4),
                BitConverter.SingleToInt32Bits(value));

            _server.ProcessPacket(span, _ => published++);
        }

        Interlocked.Add(ref _receivedRecords, count);
        Interlocked.Add(ref _receivedBytes, (long)count * RecordSize);
        Interlocked.Add(ref _publishedRecords, published);
        Interlocked.Add(ref _publishedBytes, (long)published * RecordSize);
    }

    private void UpdateRepresentativePlantState()
    {
        double t = _uptime.Elapsed.TotalSeconds;

        lock (_gate)
        {
            _frequencyHz = 50.0 + Math.Sin(t * 0.65) * 0.035;
            _northBusKv = 220.0 + Math.Sin(t * 0.21) * 1.4;
            _southBusKv = 220.0 + Math.Cos(t * 0.18) * 1.1;
            _gridLoadMw = 680.0 + Math.Sin(t * 0.11) * 42.0 + Math.Sin(t * 0.7) * 5.0;
            _transformerTempC = 62.0 + Math.Sin(t * 0.055) * 7.0;
            _breakerClosed = Math.Sin(t * 0.035) > -0.96 ? 1 : 0;
            _alarmCount = _transformerTempC > 67.5 || Math.Abs(_frequencyHz - 50.0) > 0.03 ? 1 : 0;
        }
    }

    private static float GenerateValue(uint tagId, ulong generation)
    {
        double phase = (tagId % 1024) * 0.013 + generation * 0.0007;
        return (float)(Math.Sin(phase) * 100.0 + (tagId % 500));
    }

    private static long TargetRecordsPerSecond(int activeTags)
    {
        // Simulates an average of one fresh state publication per active tag per second.
        return activeTags;
    }
}

internal sealed record DhmpScadaSnapshot(
    long UptimeMilliseconds,
    long ReceivedRecords,
    long PublishedRecords,
    long ReceivedRecordBytes,
    long PublishedRecordBytes,
    int ActiveTags,
    string Profile,
    long TargetRecordsPerSecond,
    double FrequencyHz,
    double NorthBusKv,
    double SouthBusKv,
    double GridLoadMw,
    double TransformerTempC,
    bool BreakerClosed,
    int AlarmCount);

internal sealed record DhmpScadaWorkloadRequest(
    int ActiveTags,
    string Profile);
