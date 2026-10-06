using System.Diagnostics;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.Server;

internal sealed class DhmpThroughputLab : BackgroundService
{
    public const int RecordSize = 64;
    public const int MaximumPayloadBytes = 65_472;

    private readonly object _configurationGate = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    private int _packetBytes = 65_472;
    private int _workers = Math.Max(1, Environment.ProcessorCount);
    private DhmpProcessingMode _receiveMode = DhmpProcessingMode.Sequential;
    private DhmpRatePolicy _ratePolicy = DhmpRatePolicy.RejectWindow;
    private bool _nativeSmoothing;
    private DhmpStressConfirmationMode _confirmationMode = DhmpStressConfirmationMode.None;
    private long _configurationVersion;

    private long _packetsSubmitted;
    private long _packetsAccepted;
    private long _recordsSubmitted;
    private long _recordsPublished;
    private long _bytesSubmitted;
    private long _bytesPublished;
    private long _sendTicks;
    private long _processTicks;
    private long _confirmationRecordsReturned;
    private long _confirmationBytesReturned;
    private long _workerFaults;
    private string _lastWorkerError = string.Empty;

    public void Configure(
        int packetBytes,
        int workers,
        DhmpProcessingMode receiveMode,
        DhmpRatePolicy ratePolicy,
        bool nativeSmoothing,
        DhmpStressConfirmationMode confirmationMode)
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

        if (nativeSmoothing &&
            receiveMode != DhmpProcessingMode.Latest)
            throw new ArgumentException(
                "Native smoothing requires Latest receive mode.",
                nameof(nativeSmoothing));

        if (!Enum.IsDefined(confirmationMode))
            throw new ArgumentOutOfRangeException(nameof(confirmationMode));

        if (confirmationMode != DhmpStressConfirmationMode.None &&
            (receiveMode != DhmpProcessingMode.Sequential || nativeSmoothing))
            throw new ArgumentException(
                "Confirmation modes require Sequential receive mode without native smoothing.",
                nameof(confirmationMode));

        lock (_configurationGate)
        {
            _packetBytes = packetBytes;
            _workers = workers;
            _receiveMode = receiveMode;
            _ratePolicy = ratePolicy;
            _nativeSmoothing = nativeSmoothing;
            _confirmationMode = confirmationMode;
            _configurationVersion++;
        }
    }

    public DhmpThroughputSnapshot Snapshot()
    {
        int packetBytes;
        int workers;
        DhmpProcessingMode receiveMode;
        DhmpRatePolicy ratePolicy;
        bool nativeSmoothing;
        DhmpStressConfirmationMode confirmationMode;
        long version;

        lock (_configurationGate)
        {
            packetBytes = _packetBytes;
            workers = _workers;
            receiveMode = _receiveMode;
            ratePolicy = _ratePolicy;
            nativeSmoothing = _nativeSmoothing;
            confirmationMode = _confirmationMode;
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
            nativeSmoothing,
            confirmationMode.ToString(),
            Interlocked.Read(ref _confirmationRecordsReturned),
            Interlocked.Read(ref _confirmationBytesReturned),
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
        bool nativeSmoothing;
        DhmpStressConfirmationMode confirmationMode;

        lock (_configurationGate)
        {
            packetBytes = _packetBytes;
            receiveMode = _receiveMode;
            ratePolicy = _ratePolicy;
            nativeSmoothing = _nativeSmoothing;
            confirmationMode = _confirmationMode;
        }

        var wire = new DhmpWireContract(RecordSize);
        var receivePolicy = new DhmpReceivePolicy(
            receiveMode,
            MaximumPayloadBytes,
            nativeSmoothing);

        var server = new DhmpServer(
            wire,
            receivePolicy);

        var sender = new InMemoryPacketSender(
            server,
            RecordSize,
            confirmationMode,
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
        long processingTicks,
        int confirmationRecordsReturned,
        int confirmationBytesReturned)
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
        Interlocked.Add(
            ref _confirmationRecordsReturned,
            confirmationRecordsReturned);
        Interlocked.Add(
            ref _confirmationBytesReturned,
            confirmationBytesReturned);
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
        private readonly DhmpStressConfirmationMode _confirmationMode;
        private readonly DhmpServer _returnServer;
        private readonly byte[] _confirmationScratch =
            new byte[DhmpThroughputLab.MaximumPayloadBytes];
        private readonly Action<int, int, long, int, int> _accepted;

        public InMemoryPacketSender(
            DhmpServer server,
            int recordSize,
            DhmpStressConfirmationMode confirmationMode,
            Action<int, int, long, int, int> accepted)
        {
            _server = server;
            _recordSize = recordSize;
            _confirmationMode = confirmationMode;
            _returnServer =
                new DhmpServer(
                    new DhmpWireContract(recordSize),
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        DhmpThroughputLab.MaximumPayloadBytes));
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
            int confirmationRecordsReturned = 0;
            int confirmationBytesReturned = 0;
            long started = Stopwatch.GetTimestamp();

            _server.ProcessPacket(
                payload.Span,
                batch =>
                {
                    publishedRecords +=
                        batch.Length / _recordSize;
                });

            if (_server.NativeSmoothingEnabled)
            {
                Span<byte> smoothingWindow =
                    stackalloc byte[
                        DhmpLatestStateWindow.Capacity *
                        DhmpThroughputLab.RecordSize];

                _server.CopyNativeSmoothingWindow(
                    smoothingWindow);
            }

            if (_confirmationMode == DhmpStressConfirmationMode.ApplicationId)
            {
                int ids =
                    payload.Length /
                    _recordSize;

                int returnRecords =
                    (ids + 7) / 8;

                int returnBytes =
                    returnRecords *
                    _recordSize;

                Span<byte> confirmation =
                    _confirmationScratch.AsSpan(
                        0,
                        returnBytes);

                ReadOnlySpan<byte> records =
                    payload.Span;

                for (int index = 0; index < ids; index++)
                {
                    records.Slice(
                            index * _recordSize,
                            8)
                        .CopyTo(
                            confirmation.Slice(
                                index * 8,
                                8));
                }

                int usedBytes = ids * 8;
                if (usedBytes < returnBytes)
                {
                    confirmation[
                        usedBytes..
                    ].Clear();
                }

                _returnServer.ProcessPacket(
                    confirmation,
                    returned =>
                    {
                        if (!returned.SequenceEqual(
                                _confirmationScratch.AsSpan(
                                    0,
                                    returnBytes)))
                        {
                            throw new InvalidDataException(
                                "Application ID confirmation mismatch.");
                        }
                    });

                confirmationRecordsReturned =
                    returnRecords;

                confirmationBytesReturned =
                    returnBytes;
            }
            else if (_confirmationMode == DhmpStressConfirmationMode.FullEcho)
            {
                _returnServer.ProcessPacket(
                    payload.Span,
                    returned =>
                    {
                        if (!returned.SequenceEqual(
                                payload.Span))
                        {
                            throw new InvalidDataException(
                                "Full echo confirmation mismatch.");
                        }
                    });

                confirmationRecordsReturned =
                    payload.Length /
                    _recordSize;

                confirmationBytesReturned =
                    payload.Length;
            }

            long ticks =
                Stopwatch.GetTimestamp() - started;

            _accepted(
                payload.Length,
                publishedRecords,
                ticks,
                confirmationRecordsReturned,
                confirmationBytesReturned);

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
    bool NativeSmoothing,
    string ConfirmationMode,
    long ConfirmationRecordsReturned,
    long ConfirmationBytesReturned,
    int ExpectedPublishedRecordsPerPacket,
    long WorkerFaults,
    string LastWorkerError);

internal sealed record DhmpThroughputRequest(
    int PacketBytes,
    int Workers,
    string ReceiveMode,
    string RatePolicy,
    bool NativeSmoothing,
    string ConfirmationMode);


internal enum DhmpStressConfirmationMode
{
    None = 0,
    ApplicationId = 1,
    FullEcho = 2
}
