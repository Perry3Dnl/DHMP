using System.Diagnostics;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.Server;

internal sealed class DhmpThroughputLab : BackgroundService
{
    public const int RecordSize = 16;
    public const int MaximumPayloadBytes = 65_520;

    private readonly object _configurationGate = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private int _packetBytes = 65_520;
    private int _workers = Math.Max(1, Environment.ProcessorCount);
    private DhmpProcessingMode _receiveMode = DhmpProcessingMode.Sequential;
    private DhmpRatePolicy _ratePolicy = DhmpRatePolicy.RejectWindow;
    private long _configurationVersion;

    private long _packetsSubmitted;
    private long _packetsAccepted;
    private long _recordsSubmitted;
    private long _recordsPublished;
    private long _bytesSubmitted;
    private long _bytesPublished;
    private long _sendTicks;
    private long _processTicks;
    private long _workerFaults;
    private string _lastWorkerError = string.Empty;

    public void Configure(
        int packetBytes,
        int workers,
        DhmpProcessingMode receiveMode,
        DhmpRatePolicy ratePolicy)
    {
        if (packetBytes < RecordSize ||
            packetBytes > MaximumPayloadBytes ||
            packetBytes % RecordSize != 0)
            throw new ArgumentOutOfRangeException(nameof(packetBytes));

        if (workers <= 0 || workers > 64)
            throw new ArgumentOutOfRangeException(nameof(workers));

        if (receiveMode is not DhmpProcessingMode.Sequential and
            not DhmpProcessingMode.Latest)
            throw new ArgumentOutOfRangeException(nameof(receiveMode));

        if (ratePolicy is not DhmpRatePolicy.RejectWindow and
            not DhmpRatePolicy.SmoothPacing)
            throw new ArgumentOutOfRangeException(nameof(ratePolicy));

        lock (_configurationGate)
        {
            _packetBytes = packetBytes;
            _workers = workers;
            _receiveMode = receiveMode;
            _ratePolicy = ratePolicy;
            _configurationVersion++;
        }
    }

    public DhmpThroughputSnapshot Snapshot()
    {
        int packetBytes;
        int workers;
        DhmpProcessingMode receiveMode;
        DhmpRatePolicy ratePolicy;
        long version;

        lock (_configurationGate)
        {
            packetBytes = _packetBytes;
            workers = _workers;
            receiveMode = _receiveMode;
            ratePolicy = _ratePolicy;
            version = _configurationVersion;
        }

        return new DhmpThroughputSnapshot(
            _uptime.ElapsedMilliseconds,
            Interlocked.Read(ref _packetsSubmitted),
            Interlocked.Read(ref _packetsAccepted),
            Interlocked.Read(ref _recordsSubmitted),
            Interlocked.Read(ref _recordsPublished),
            Interlocked.Read(ref _bytesSubmitted),
            Interlocked.Read(ref _bytesPublished),
            Interlocked.Read(ref _sendTicks),
            Interlocked.Read(ref _processTicks),
            Stopwatch.Frequency,
            packetBytes,
            packetBytes / RecordSize,
            workers,
            Environment.ProcessorCount,
            version,
            RecordSize,
            receiveMode.ToString(),
            ratePolicy.ToString(),
            receiveMode == DhmpProcessingMode.Latest
                ? 1
                : packetBytes / RecordSize,
            Interlocked.Read(ref _workerFaults),
            _lastWorkerError);
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int workers;
            long version;

            lock (_configurationGate)
            {
                workers = _workers;
                version = _configurationVersion;
            }

            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken);

            Task[] tasks = new Task[workers];

            for (int index = 0; index < workers; index++)
            {
                int workerId = index;

                tasks[index] = RunWorkerGuardedAsync(
                    workerId,
                    version,
                    linked.Token);
            }

            Task allWorkers =
                Task.WhenAll(tasks);

            while (!stoppingToken.IsCancellationRequested &&
                   !allWorkers.IsCompleted)
            {
                await Task.Delay(
                    100,
                    stoppingToken);

                lock (_configurationGate)
                {
                    if (_configurationVersion != version ||
                        _workers != workers)
                    {
                        linked.Cancel();
                        break;
                    }
                }
            }

            try
            {
                await allWorkers;
            }
            catch (OperationCanceledException)
                when (!stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Interlocked.Increment(
                    ref _workerFaults);

                _lastWorkerError =
                    exception.GetBaseException().Message;

                linked.Cancel();

                try
                {
                    await Task.Delay(
                        250,
                        stoppingToken);
                }
                catch (OperationCanceledException)
                    when (stoppingToken.IsCancellationRequested)
                {
                }
            }
        }
    }

    private async Task RunWorkerGuardedAsync(
        int workerId,
        long configurationVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunWorkerAsync(
                workerId,
                configurationVersion,
                cancellationToken);
        }
        catch (DhmpProtocolException exception)
            when (exception.Message.Contains(
                "send budget exhausted",
                StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(
                ref _workerFaults);

            _lastWorkerError =
                "DHMP send budget exhausted during stress run.";

            throw;
        }
    }

    private async Task RunWorkerAsync(
        int workerId,
        long configurationVersion,
        CancellationToken cancellationToken)
    {
        int packetBytes;
        DhmpProcessingMode receiveMode;
        DhmpRatePolicy ratePolicy;

        lock (_configurationGate)
        {
            packetBytes = _packetBytes;
            receiveMode = _receiveMode;
            ratePolicy = _ratePolicy;
        }

        var wire = new DhmpWireContract(RecordSize);
        var receivePolicy = new DhmpReceivePolicy(
            receiveMode,
            MaximumPayloadBytes);

        var server = new DhmpServer(
            wire,
            receivePolicy);

        var sender = new InMemoryPacketSender(
            server,
            RecordSize,
            AddAccepted);

        var sendPolicy = new DhmpSendPolicy(
            long.MaxValue,
            MaximumPayloadBytes,
            ratePolicy);

        var client = new DhmpClient(
            sender,
            wire,
            sendPolicy);

        byte[] packet =
            GC.AllocateUninitializedArray<byte>(packetBytes);

        FillPacket(packet, workerId);

        int recordsPerPacket =
            packetBytes / RecordSize;

        long localPackets = 0;
        long localRecords = 0;
        long localBytes = 0;
        long localSendTicks = 0;

        const int FlushPackets = 256;

        while (!cancellationToken.IsCancellationRequested)
        {
            long started = Stopwatch.GetTimestamp();

            await client.SendBatchAsync(
                packet,
                cancellationToken);

            localSendTicks +=
                Stopwatch.GetTimestamp() - started;

            localPackets++;
            localRecords += recordsPerPacket;
            localBytes += packetBytes;

            if (localPackets >= FlushPackets)
            {
                FlushSubmitted(
                    localPackets,
                    localRecords,
                    localBytes,
                    localSendTicks);

                localPackets = 0;
                localRecords = 0;
                localBytes = 0;
                localSendTicks = 0;

                lock (_configurationGate)
                {
                    if (_configurationVersion != configurationVersion)
                        break;
                }

                await Task.Yield();
            }
        }

        FlushSubmitted(
            localPackets,
            localRecords,
            localBytes,
            localSendTicks);
    }

    private void AddAccepted(
        int packetBytes,
        int publishedRecords,
        long processingTicks)
    {
        Interlocked.Increment(ref _packetsAccepted);
        Interlocked.Add(
            ref _recordsPublished,
            publishedRecords);
        Interlocked.Add(
            ref _bytesPublished,
            (long)publishedRecords * RecordSize);
        Interlocked.Add(
            ref _processTicks,
            processingTicks);
    }

    private void FlushSubmitted(
        long packets,
        long records,
        long bytes,
        long ticks)
    {
        if (packets == 0)
            return;

        Interlocked.Add(
            ref _packetsSubmitted,
            packets);
        Interlocked.Add(
            ref _recordsSubmitted,
            records);
        Interlocked.Add(
            ref _bytesSubmitted,
            bytes);
        Interlocked.Add(
            ref _sendTicks,
            ticks);
    }

    private static void FillPacket(
        byte[] packet,
        int workerId)
    {
        for (int offset = 0; offset < packet.Length; offset += RecordSize)
        {
            long sequence =
                ((long)workerId << 48) |
                (uint)(offset / RecordSize);

            BitConverter.TryWriteBytes(
                packet.AsSpan(offset, 8),
                sequence);

            BitConverter.TryWriteBytes(
                packet.AsSpan(offset + 8, 8),
                ~sequence);
        }
    }

    private sealed class InMemoryPacketSender : IDhmpPacketSender
    {
        private readonly DhmpServer _server;
        private readonly int _recordSize;
        private readonly Action<int, int, long> _accepted;

        public InMemoryPacketSender(
            DhmpServer server,
            int recordSize,
            Action<int, int, long> accepted)
        {
            _server = server;
            _recordSize = recordSize;
            _accepted = accepted;
        }

        public int MaximumPayloadBytes =>
            DhmpThroughputLab.MaximumPayloadBytes;

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int publishedRecords = 0;
            long started = Stopwatch.GetTimestamp();

            _server.ProcessPacket(
                payload.Span,
                batch =>
                {
                    publishedRecords +=
                        batch.Length / _recordSize;
                });

            long ticks =
                Stopwatch.GetTimestamp() - started;

            _accepted(
                payload.Length,
                publishedRecords,
                ticks);

            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record DhmpThroughputSnapshot(
    long UptimeMilliseconds,
    long PacketsSubmitted,
    long PacketsAccepted,
    long RecordsSubmitted,
    long RecordsPublished,
    long BytesSubmitted,
    long BytesPublished,
    long SendTicks,
    long ProcessTicks,
    long StopwatchFrequency,
    int PacketBytes,
    int RecordsPerPacket,
    int Workers,
    int LogicalProcessors,
    long ConfigurationVersion,
    int RecordSize,
    string ReceiveMode,
    string RatePolicy,
    int ExpectedPublishedRecordsPerPacket,
    long WorkerFaults,
    string LastWorkerError);

internal sealed record DhmpThroughputRequest(
    int PacketBytes,
    int Workers,
    string ReceiveMode,
    string RatePolicy);
