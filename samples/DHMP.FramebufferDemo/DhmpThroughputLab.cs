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
    private DhmpRatePolicy _ratePolicy = DhmpRatePolicy.Unlimited;
    private bool _nativeSmoothing;
    private DhmpStressConfirmationMode _confirmationMode = DhmpStressConfirmationMode.None;
    private long _configurationVersion;

    private WorkerMetrics[] _workerMetrics = [];
    private double _coreProcessNanosecondsPerPacket;
    private AllocationBreakdown _allocationBreakdown;
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
            not DhmpRatePolicy.SmoothPacing and
            not DhmpRatePolicy.Unlimited)
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
        double coreProcessNanosecondsPerPacket;
        AllocationBreakdown allocationBreakdown;

        lock (_configurationGate)
        {
            packetBytes = _packetBytes;
            workers = _workers;
            receiveMode = _receiveMode;
            ratePolicy = _ratePolicy;
            nativeSmoothing = _nativeSmoothing;
            confirmationMode = _confirmationMode;
            version = _configurationVersion;
            coreProcessNanosecondsPerPacket =
                _coreProcessNanosecondsPerPacket;
            allocationBreakdown =
                _allocationBreakdown;
        }

        WorkerMetrics[] metrics =
            Volatile.Read(ref _workerMetrics);

        long packetsSubmitted = 0;
        long packetsAccepted = 0;
        long recordsSubmitted = 0;
        long recordsPublished = 0;
        long bytesSubmitted = 0;
        long bytesPublished = 0;
        long sendTicks = 0;
        long processTicks = 0;
        long confirmationRecordsReturned = 0;
        long confirmationBytesReturned = 0;

        foreach (WorkerMetrics worker in metrics)
        {
            packetsSubmitted += Volatile.Read(ref worker.PacketsSubmitted);
            packetsAccepted += Volatile.Read(ref worker.PacketsAccepted);
            recordsSubmitted += Volatile.Read(ref worker.RecordsSubmitted);
            recordsPublished += Volatile.Read(ref worker.RecordsPublished);
            bytesSubmitted += Volatile.Read(ref worker.BytesSubmitted);
            bytesPublished += Volatile.Read(ref worker.BytesPublished);
            sendTicks += Volatile.Read(ref worker.SendTicks);
            processTicks += Volatile.Read(ref worker.ProcessTicks);
            confirmationRecordsReturned += Volatile.Read(ref worker.ConfirmationRecordsReturned);
            confirmationBytesReturned += Volatile.Read(ref worker.ConfirmationBytesReturned);
        }

        return new DhmpThroughputSnapshot(
            _uptime.ElapsedMilliseconds,
            packetsSubmitted,
            packetsAccepted,
            recordsSubmitted,
            recordsPublished,
            bytesSubmitted,
            bytesPublished,
            sendTicks,
            processTicks,
            Stopwatch.Frequency,
            coreProcessNanosecondsPerPacket,
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            allocationBreakdown.ProcessorBytesPerPacket,
            allocationBreakdown.ServerBytesPerPacket,
            allocationBreakdown.ClientBytesPerPacket,
            allocationBreakdown.FullPathBytesPerPacket,
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
            confirmationRecordsReturned,
            confirmationBytesReturned,
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
            int packetBytes;
            DhmpProcessingMode receiveMode;
            long version;

            lock (_configurationGate)
            {
                workers = _workers;
                packetBytes = _packetBytes;
                receiveMode = _receiveMode;
                version = _configurationVersion;
            }

            double coreNs =
                MeasureCoreProcessor(
                    packetBytes,
                    receiveMode);

            AllocationBreakdown allocationBreakdown =
                MeasureAllocationBreakdown(
                    receiveMode);

            lock (_configurationGate)
            {
                if (_configurationVersion == version)
                {
                    _coreProcessNanosecondsPerPacket =
                        coreNs;
                    _allocationBreakdown =
                        allocationBreakdown;
                }
            }

            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    stoppingToken);

            Task[] tasks = new Task[workers];
            var metrics = new WorkerMetrics[workers];

            for (int index = 0; index < workers; index++)
                metrics[index] = new WorkerMetrics();

            Volatile.Write(
                ref _workerMetrics,
                metrics);

            for (int index = 0; index < workers; index++)
            {
                int workerId = index;
                WorkerMetrics workerMetrics = metrics[index];

                tasks[index] = Task.Run(
                    () => RunWorkerGuardedAsync(
                        workerId,
                        version,
                        workerMetrics,
                        linked.Token),
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
        WorkerMetrics metrics,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunWorkerAsync(
                workerId,
                configurationVersion,
                metrics,
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
        WorkerMetrics metrics,
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
            metrics);

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

        const int ConfigurationCheckMask =
            4096 - 1;

        int packetsSinceConfigurationCheck = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            long started = Stopwatch.GetTimestamp();

            await client.SendBatchAsync(
                packet,
                cancellationToken);

            metrics.SendTicks +=
                Stopwatch.GetTimestamp() - started;

            metrics.PacketsSubmitted++;
            metrics.RecordsSubmitted +=
                recordsPerPacket;
            metrics.BytesSubmitted +=
                packetBytes;

            packetsSinceConfigurationCheck++;

            if ((packetsSinceConfigurationCheck &
                 ConfigurationCheckMask) == 0)
            {
                lock (_configurationGate)
                {
                    if (_configurationVersion != configurationVersion)
                        break;
                }
            }
        }
    }

    private static double MeasureCoreProcessor(
        int packetBytes,
        DhmpProcessingMode receiveMode)
    {
        const int WarmupIterations = 100_000;
        const int MeasuredIterations = 2_000_000;

        var processor =
            new DhmpPacketProcessor(
                new DhmpWireContract(RecordSize),
                new DhmpReceivePolicy(
                    receiveMode,
                    MaximumPayloadBytes));

        byte[] packet =
            GC.AllocateUninitializedArray<byte>(
                packetBytes);

        long guard = 0;

        Action<ReadOnlySpan<byte>> publish =
            span =>
            {
                guard +=
                    span.Length;
            };

        for (int index = 0;
             index < WarmupIterations;
             index++)
        {
            processor.Process(
                packet,
                publish);
        }

        long started =
            Stopwatch.GetTimestamp();

        for (int index = 0;
             index < MeasuredIterations;
             index++)
        {
            processor.Process(
                packet,
                publish);
        }

        long elapsed =
            Stopwatch.GetTimestamp() - started;

        GC.KeepAlive(
            guard);

        return
            (double)elapsed /
            Stopwatch.Frequency *
            1_000_000_000d /
            MeasuredIterations;
    }

    private static AllocationBreakdown MeasureAllocationBreakdown(
        DhmpProcessingMode receiveMode)
    {
        const int ProbePacketBytes = 256;
        const int WarmupIterations = 10_000;
        const int MeasuredIterations = 250_000;

        var wire =
            new DhmpWireContract(
                RecordSize);

        var receivePolicy =
            new DhmpReceivePolicy(
                receiveMode,
                MaximumPayloadBytes);

        byte[] packet =
            GC.AllocateUninitializedArray<byte>(
                ProbePacketBytes);

        Action<ReadOnlySpan<byte>> publish =
            static _ => { };

        var processor =
            new DhmpPacketProcessor(
                wire,
                receivePolicy);

        double processorBytes =
            MeasureAllocatedBytesPerCall(
                WarmupIterations,
                MeasuredIterations,
                () => processor.Process(
                    packet,
                    publish));

        var server =
            new DhmpServer(
                wire,
                receivePolicy);

        double serverBytes =
            MeasureAllocatedBytesPerCall(
                WarmupIterations,
                MeasuredIterations,
                () => server.ProcessPacket(
                    packet,
                    publish));

        var nullSender =
            new AllocationProbeSender();

        var client =
            new DhmpClient(
                nullSender,
                wire,
                new DhmpSendPolicy(
                    long.MaxValue,
                    MaximumPayloadBytes,
                    DhmpRatePolicy.Unlimited));

        double clientBytes =
            MeasureAllocatedBytesPerCall(
                WarmupIterations,
                MeasuredIterations,
                () => client.SendBatchAsync(
                        packet)
                    .GetAwaiter()
                    .GetResult());

        var metrics =
            new WorkerMetrics();

        var fullSender =
            new InMemoryPacketSender(
                server,
                RecordSize,
                DhmpStressConfirmationMode.None,
                metrics);

        var fullClient =
            new DhmpClient(
                fullSender,
                wire,
                new DhmpSendPolicy(
                    long.MaxValue,
                    MaximumPayloadBytes,
                    DhmpRatePolicy.Unlimited));

        double fullPathBytes =
            MeasureAllocatedBytesPerCall(
                WarmupIterations,
                MeasuredIterations,
                () => fullClient.SendBatchAsync(
                        packet)
                    .GetAwaiter()
                    .GetResult());

        return new AllocationBreakdown(
            processorBytes,
            serverBytes,
            clientBytes,
            fullPathBytes);
    }

    private static double MeasureAllocatedBytesPerCall(
        int warmupIterations,
        int measuredIterations,
        Action action)
    {
        for (int index = 0;
             index < warmupIterations;
             index++)
        {
            action();
        }

        long before =
            GC.GetAllocatedBytesForCurrentThread();

        for (int index = 0;
             index < measuredIterations;
             index++)
        {
            action();
        }

        long after =
            GC.GetAllocatedBytesForCurrentThread();

        return
            (double)(after - before) /
            measuredIterations;
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

    private sealed class AllocationProbeSender : IDhmpPacketSender
    {
        public int MaximumPayloadBytes =>
            MaximumPayloadBytes;

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
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
        private readonly WorkerMetrics _metrics;
        private readonly Action<ReadOnlySpan<byte>> _publishBatch;
        private readonly Action<ReadOnlySpan<byte>> _confirmBatch;
        private int _publishedRecordsCurrent;
        private int _confirmationExpectedBytes;
        private ReadOnlyMemory<byte> _fullEchoExpected;

        public InMemoryPacketSender(
            DhmpServer server,
            int recordSize,
            DhmpStressConfirmationMode confirmationMode,
            WorkerMetrics metrics)
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
            _metrics = metrics;
            _publishBatch = PublishBatch;
            _confirmBatch = ConfirmBatch;
        }

        public int MaximumPayloadBytes =>
            DhmpThroughputLab.MaximumPayloadBytes;

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _publishedRecordsCurrent = 0;
            int confirmationRecordsReturned = 0;
            int confirmationBytesReturned = 0;
            long started = Stopwatch.GetTimestamp();

            _server.ProcessPacket(
                payload.Span,
                _publishBatch);

            int publishedRecords =
                _publishedRecordsCurrent;

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
                    (ids + 1) / 2;

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

                _confirmationExpectedBytes =
                    returnBytes;

                _fullEchoExpected =
                    ReadOnlyMemory<byte>.Empty;

                _returnServer.ProcessPacket(
                    confirmation,
                    _confirmBatch);

                confirmationRecordsReturned =
                    returnRecords;

                confirmationBytesReturned =
                    returnBytes;
            }
            else if (_confirmationMode == DhmpStressConfirmationMode.FullEcho)
            {
                _confirmationExpectedBytes =
                    payload.Length;

                _fullEchoExpected =
                    payload;

                _returnServer.ProcessPacket(
                    payload.Span,
                    _confirmBatch);

                _fullEchoExpected =
                    ReadOnlyMemory<byte>.Empty;

                confirmationRecordsReturned =
                    payload.Length /
                    _recordSize;

                confirmationBytesReturned =
                    payload.Length;
            }

            long ticks =
                Stopwatch.GetTimestamp() - started;

            _metrics.PacketsAccepted++;
            _metrics.RecordsPublished +=
                publishedRecords;
            _metrics.BytesPublished +=
                (long)publishedRecords *
                _recordSize;
            _metrics.ProcessTicks +=
                ticks;
            _metrics.ConfirmationRecordsReturned +=
                confirmationRecordsReturned;
            _metrics.ConfirmationBytesReturned +=
                confirmationBytesReturned;

            return ValueTask.CompletedTask;
        }

        private void PublishBatch(
            ReadOnlySpan<byte> batch)
        {
            _publishedRecordsCurrent +=
                batch.Length /
                _recordSize;
        }

        private void ConfirmBatch(
            ReadOnlySpan<byte> returned)
        {
            ReadOnlySpan<byte> expected =
                _fullEchoExpected.IsEmpty
                    ? _confirmationScratch.AsSpan(
                        0,
                        _confirmationExpectedBytes)
                    : _fullEchoExpected.Span;

            if (!returned.SequenceEqual(
                    expected))
            {
                throw new InvalidDataException(
                    _fullEchoExpected.IsEmpty
                        ? "Application ID confirmation mismatch."
                        : "Full echo confirmation mismatch.");
            }
        }
    }
    private sealed class WorkerMetrics
    {
        public long PacketsSubmitted;
        public long PacketsAccepted;
        public long RecordsSubmitted;
        public long RecordsPublished;
        public long BytesSubmitted;
        public long BytesPublished;
        public long SendTicks;
        public long ProcessTicks;
        public long ConfirmationRecordsReturned;
        public long ConfirmationBytesReturned;
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
    double CoreProcessNanosecondsPerPacket,
    long TotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double ProcessorAllocatedBytesPerPacket,
    double ServerAllocatedBytesPerPacket,
    double ClientAllocatedBytesPerPacket,
    double FullPathAllocatedBytesPerPacket,
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

internal readonly record struct AllocationBreakdown(
    double ProcessorBytesPerPacket,
    double ServerBytesPerPacket,
    double ClientBytesPerPacket,
    double FullPathBytesPerPacket);

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
