using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.Server;

internal sealed class DhmpFullReportLab
{
    private const int RecordSize = DhmpThroughputLab.RecordSize;
    private const int MaximumPayloadBytes = DhmpThroughputLab.MaximumPayloadBytes;
    private const int WarmupIterations = 20_000;
    private const int MeasuredIterations = 120_000;
    private const int Repetitions = 5;

    private static readonly int[] PacketSizes =
        [16, 256, 1024, 1408, 4096, 16384, 65520];

    private readonly object _gate = new();
    private readonly DhmpThroughputLab _throughputLab;
    private readonly DhmpAfXdpLiveLab _afXdpLab;

    private bool _running;
    private string _phase = "idle";
    private int _completedSteps;
    private int _totalSteps;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _completedUtc;
    private DhmpFullReport? _report;
    private string? _error;

    public DhmpFullReportLab(
        DhmpThroughputLab throughputLab,
        DhmpAfXdpLiveLab afXdpLab)
    {
        _throughputLab = throughputLab;
        _afXdpLab = afXdpLab;
    }

    public DhmpFullReportStatus Status()
    {
        lock (_gate)
        {
            return new DhmpFullReportStatus(
                _running,
                _phase,
                _completedSteps,
                _totalSteps,
                _startedUtc,
                _completedUtc,
                _report,
                _error);
        }
    }

    public bool Start()
    {
        lock (_gate)
        {
            if (_running)
                return false;

            _running = true;
            _phase = "Preparing isolated benchmark host";
            _completedSteps = 0;
            _totalSteps = 38;
            _startedUtc = DateTimeOffset.UtcNow;
            _completedUtc = null;
            _report = null;
            _error = null;
        }

        _throughputLab.Pause();
        _afXdpLab.Pause();

        _ = Task.Run(RunAsync);
        return true;
    }

    private async Task RunAsync()
    {
        try
        {
            // Give paused hosted loops time to observe their configuration version.
            await Task.Delay(300).ConfigureAwait(false);

            var environment = CaptureEnvironment();
            Step("Core / server / client matrix");

            var matrix = new List<DhmpPathBenchmark>();
            foreach (int packetBytes in PacketSizes)
            {
                matrix.Add(RunPathBenchmark(
                    packetBytes,
                    DhmpProcessingMode.Sequential,
                    nativeSmoothing: false));

                Step($"Sequential {packetBytes:N0} B");

                matrix.Add(RunPathBenchmark(
                    packetBytes,
                    DhmpProcessingMode.Latest,
                    nativeSmoothing: false));

                Step($"Latest {packetBytes:N0} B");

                matrix.Add(RunPathBenchmark(
                    packetBytes,
                    DhmpProcessingMode.Latest,
                    nativeSmoothing: true));

                Step($"Latest + Ring-3 {packetBytes:N0} B");
            }

            Step("Worker scaling");
            var scaling = RunScalingBenchmarks();

            Step("Rate-policy overhead");
            var ratePolicies = RunRatePolicyBenchmarks();

            Step("Confirmation overhead");
            var confirmations = RunConfirmationBenchmarks();

            Step("Allocation probes");
            var allocations = RunAllocationBenchmarks();

            Step("Correctness and contract checks");
            var checks = RunCorrectnessChecks();

            Step("Derived summary");
            var summary = BuildSummary(
                matrix,
                scaling,
                allocations,
                checks);

            var report = new DhmpFullReport(
                DateTimeOffset.UtcNow,
                environment,
                summary,
                matrix.ToArray(),
                scaling,
                ratePolicies,
                confirmations,
                allocations,
                checks,
                new[]
                {
                    "Core processor ceiling is a software processing ceiling, not physical wire throughput.",
                    "Logical payload GB/s represents bytes processed in-memory by the benchmark path.",
                    "65,520-byte payload rows are large logical/in-memory batches and are not typical MTU-sized wire packets.",
                    "1,408-byte rows are included as the network-oriented payload reference used by the raw IPv6 / AF_XDP lab.",
                    "Packet rate is measured as complete benchmark packet transactions per second.",
                    "Records/s is the number of 16-byte application records represented by those packet transactions.",
                    "Latest publishes one newest record per packet; Sequential publishes every complete record.",
                    "Latest + Ring-3 retains receive-side N-2/N-1/N while publishing only newest N."
                });

            lock (_gate)
            {
                _report = report;
                _phase = "Complete";
                _running = false;
                _completedSteps = _totalSteps;
                _completedUtc = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _running = false;
                _phase = "Failed";
                _completedUtc = DateTimeOffset.UtcNow;
                _error = exception.ToString();
            }
        }
    }

    private void Step(string phase)
    {
        lock (_gate)
        {
            _phase = phase;
            _completedSteps = Math.Min(
                _totalSteps,
                _completedSteps + 1);
        }
    }

    private static DhmpReportEnvironment CaptureEnvironment() =>
        new(
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            Stopwatch.Frequency,
            RecordSize,
            MaximumPayloadBytes);

    private static DhmpPathBenchmark RunPathBenchmark(
        int packetBytes,
        DhmpProcessingMode mode,
        bool nativeSmoothing)
    {
        var wire = new DhmpWireContract(RecordSize);
        var policy = new DhmpReceivePolicy(
            mode,
            MaximumPayloadBytes,
            nativeSmoothing);

        byte[] packet =
            GC.AllocateUninitializedArray<byte>(packetBytes);

        int published = 0;
        Action<ReadOnlySpan<byte>> publish =
            span => published += span.Length / RecordSize;

        var processor = new DhmpPacketProcessor(
            wire,
            policy);

        var server = new DhmpServer(
            wire,
            policy);

        var sender = new ReportSender(
            server,
            publish);

        var client = new DhmpClient(
            sender,
            wire,
            new DhmpSendPolicy(
                long.MaxValue,
                MaximumPayloadBytes,
                DhmpRatePolicy.Unlimited));

        for (int i = 0; i < WarmupIterations; i++)
        {
            processor.Process(packet, publish);
            server.ProcessPacket(packet, publish);
            client.SendBatchAsync(packet).GetAwaiter().GetResult();
        }

        double[] processorNs = new double[Repetitions];
        double[] serverNs = new double[Repetitions];
        double[] clientNs = new double[Repetitions];

        for (int repetition = 0; repetition < Repetitions; repetition++)
        {
            processorNs[repetition] =
                MeasureNanosecondsPerCall(
                    MeasuredIterations,
                    () => processor.Process(packet, publish));

            serverNs[repetition] =
                MeasureNanosecondsPerCall(
                    MeasuredIterations,
                    () => server.ProcessPacket(packet, publish));

            clientNs[repetition] =
                MeasureNanosecondsPerCall(
                    MeasuredIterations,
                    () => client.SendBatchAsync(packet)
                        .GetAwaiter()
                        .GetResult());
        }

        GC.KeepAlive(published);

        int recordsPerPacket =
            packetBytes / RecordSize;

        double clientMedian =
            Median(clientNs);

        double packetRate =
            1_000_000_000d /
            clientMedian;

        return new DhmpPathBenchmark(
            packetBytes,
            recordsPerPacket,
            mode.ToString(),
            nativeSmoothing,
            Stats(processorNs),
            Stats(serverNs),
            Stats(clientNs),
            packetRate,
            packetRate * recordsPerPacket,
            packetRate * packetBytes / 1_000_000_000d,
            mode == DhmpProcessingMode.Latest
                ? 1
                : recordsPerPacket);
    }

    private static DhmpWorkerScalingBenchmark[] RunScalingBenchmarks()
    {
        int[] candidates =
            [1, 2, 4, 8, Math.Min(16, Environment.ProcessorCount)];

        int[] workers =
            candidates
                .Where(value => value > 0)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();

        var results =
            new List<DhmpWorkerScalingBenchmark>();

        foreach (int workerCount in workers)
        {
            const int packetBytes = 1408;
            const int iterationsPerWorker = 100_000;

            var startGate =
                new ManualResetEventSlim(false);

            Task<long>[] tasks =
                new Task<long>[workerCount];

            for (int worker = 0; worker < workerCount; worker++)
            {
                tasks[worker] = Task.Run(() =>
                {
                    var wire =
                        new DhmpWireContract(RecordSize);

                    var server =
                        new DhmpServer(
                            wire,
                            new DhmpReceivePolicy(
                                DhmpProcessingMode.Sequential,
                                MaximumPayloadBytes));

                    Action<ReadOnlySpan<byte>> publish =
                        static _ => { };

                    var sender =
                        new ReportSender(
                            server,
                            publish);

                    var client =
                        new DhmpClient(
                            sender,
                            wire,
                            new DhmpSendPolicy(
                                long.MaxValue,
                                MaximumPayloadBytes,
                                DhmpRatePolicy.Unlimited));

                    byte[] packet =
                        GC.AllocateUninitializedArray<byte>(
                            packetBytes);

                    for (int i = 0; i < 5_000; i++)
                    {
                        client.SendBatchAsync(packet)
                            .GetAwaiter()
                            .GetResult();
                    }

                    startGate.Wait();

                    long started =
                        Stopwatch.GetTimestamp();

                    for (int i = 0;
                         i < iterationsPerWorker;
                         i++)
                    {
                        client.SendBatchAsync(packet)
                            .GetAwaiter()
                            .GetResult();
                    }

                    return Stopwatch.GetTimestamp() -
                        started;
                });
            }

            startGate.Set();
            Task.WaitAll(tasks);

            long maxTicks =
                tasks.Max(task => task.Result);

            double seconds =
                (double)maxTicks /
                Stopwatch.Frequency;

            long packets =
                (long)workerCount *
                iterationsPerWorker;

            double pps =
                packets /
                seconds;

            results.Add(
                new DhmpWorkerScalingBenchmark(
                    workerCount,
                    packetBytes,
                    pps,
                    pps * (packetBytes / RecordSize),
                    pps * packetBytes / 1_000_000_000d,
                    seconds));
        }

        return results.ToArray();
    }

    private static DhmpRatePolicyBenchmark[] RunRatePolicyBenchmarks()
    {
        var results =
            new List<DhmpRatePolicyBenchmark>();

        foreach (DhmpRatePolicy ratePolicy in new[]
                 {
                     DhmpRatePolicy.Unlimited,
                     DhmpRatePolicy.RejectWindow,
                     DhmpRatePolicy.SmoothPacing
                 })
        {
            const int packetBytes = 256;

            var wire =
                new DhmpWireContract(RecordSize);

            var server =
                new DhmpServer(
                    wire,
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        MaximumPayloadBytes));

            Action<ReadOnlySpan<byte>> publish =
                static _ => { };

            var sender =
                new ReportSender(
                    server,
                    publish);

            var client =
                new DhmpClient(
                    sender,
                    wire,
                    new DhmpSendPolicy(
                        long.MaxValue,
                        MaximumPayloadBytes,
                        ratePolicy));

            byte[] packet =
                GC.AllocateUninitializedArray<byte>(
                    packetBytes);

            for (int i = 0; i < WarmupIterations; i++)
            {
                client.SendBatchAsync(packet)
                    .GetAwaiter()
                    .GetResult();
            }

            double[] samples =
                new double[Repetitions];

            for (int repetition = 0;
                 repetition < Repetitions;
                 repetition++)
            {
                samples[repetition] =
                    MeasureNanosecondsPerCall(
                        MeasuredIterations,
                        () => client.SendBatchAsync(packet)
                            .GetAwaiter()
                            .GetResult());
            }

            double median = Median(samples);

            results.Add(
                new DhmpRatePolicyBenchmark(
                    ratePolicy.ToString(),
                    packetBytes,
                    Stats(samples),
                    1_000_000_000d / median));
        }

        return results.ToArray();
    }

    private static DhmpConfirmationBenchmark[] RunConfirmationBenchmarks()
    {
        var results =
            new List<DhmpConfirmationBenchmark>();

        foreach (DhmpStressConfirmationMode mode in Enum.GetValues<DhmpStressConfirmationMode>())
        {
            const int packetBytes = 1408;

            var wire =
                new DhmpWireContract(RecordSize);

            var server =
                new DhmpServer(
                    wire,
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        MaximumPayloadBytes));

            var sender =
                new ConfirmationReportSender(
                    server,
                    mode);

            var client =
                new DhmpClient(
                    sender,
                    wire,
                    new DhmpSendPolicy(
                        long.MaxValue,
                        MaximumPayloadBytes,
                        DhmpRatePolicy.Unlimited));

            byte[] packet =
                GC.AllocateUninitializedArray<byte>(
                    packetBytes);

            FillPacket(packet);

            for (int i = 0; i < WarmupIterations; i++)
            {
                client.SendBatchAsync(packet)
                    .GetAwaiter()
                    .GetResult();
            }

            double[] samples =
                new double[Repetitions];

            for (int repetition = 0;
                 repetition < Repetitions;
                 repetition++)
            {
                samples[repetition] =
                    MeasureNanosecondsPerCall(
                        MeasuredIterations,
                        () => client.SendBatchAsync(packet)
                            .GetAwaiter()
                            .GetResult());
            }

            double median = Median(samples);

            results.Add(
                new DhmpConfirmationBenchmark(
                    mode.ToString(),
                    packetBytes,
                    Stats(samples),
                    1_000_000_000d / median,
                    mode == DhmpStressConfirmationMode.None
                        ? 0
                        : mode == DhmpStressConfirmationMode.FullEcho
                            ? packetBytes
                            : ((packetBytes / RecordSize + 1) / 2) * RecordSize));
        }

        return results.ToArray();
    }

    private static DhmpAllocationBenchmark[] RunAllocationBenchmarks()
    {
        var results =
            new List<DhmpAllocationBenchmark>();

        foreach ((DhmpProcessingMode mode, bool smoothing) in new[]
                 {
                     (DhmpProcessingMode.Sequential, false),
                     (DhmpProcessingMode.Latest, false),
                     (DhmpProcessingMode.Latest, true)
                 })
        {
            const int packetBytes = 1408;

            var wire =
                new DhmpWireContract(RecordSize);

            var policy =
                new DhmpReceivePolicy(
                    mode,
                    MaximumPayloadBytes,
                    smoothing);

            var processor =
                new DhmpPacketProcessor(
                    wire,
                    policy);

            var server =
                new DhmpServer(
                    wire,
                    policy);

            byte[] packet =
                GC.AllocateUninitializedArray<byte>(
                    packetBytes);

            Action<ReadOnlySpan<byte>> publish =
                static _ => { };

            var sender =
                new ReportSender(
                    server,
                    publish);

            var client =
                new DhmpClient(
                    sender,
                    wire,
                    new DhmpSendPolicy(
                        long.MaxValue,
                        MaximumPayloadBytes,
                        DhmpRatePolicy.Unlimited));

            double processorBytes =
                MeasureAllocatedBytesPerCall(
                    () => processor.Process(
                        packet,
                        publish));

            double serverBytes =
                MeasureAllocatedBytesPerCall(
                    () => server.ProcessPacket(
                        packet,
                        publish));

            double fullPathBytes =
                MeasureAllocatedBytesPerCall(
                    () => client.SendBatchAsync(packet)
                        .GetAwaiter()
                        .GetResult());

            results.Add(
                new DhmpAllocationBenchmark(
                    mode.ToString(),
                    smoothing,
                    packetBytes,
                    processorBytes,
                    serverBytes,
                    fullPathBytes));
        }

        return results.ToArray();
    }

    private static DhmpCorrectnessCheck[] RunCorrectnessChecks()
    {
        var checks =
            new List<DhmpCorrectnessCheck>();

        foreach ((DhmpProcessingMode mode, bool smoothing) in new[]
                 {
                     (DhmpProcessingMode.Sequential, false),
                     (DhmpProcessingMode.Latest, false),
                     (DhmpProcessingMode.Latest, true)
                 })
        {
            const int packetBytes = 1408;
            int recordsPerPacket =
                packetBytes / RecordSize;

            var server =
                new DhmpServer(
                    new DhmpWireContract(RecordSize),
                    new DhmpReceivePolicy(
                        mode,
                        MaximumPayloadBytes,
                        smoothing));

            int published = 0;

            server.ProcessPacket(
                new byte[packetBytes],
                span =>
                    published +=
                        span.Length / RecordSize);

            int expected =
                mode == DhmpProcessingMode.Latest
                    ? 1
                    : recordsPerPacket;

            bool publicationOk =
                published == expected;

            bool ringOk =
                !smoothing ||
                server.NativeSmoothingRecordCount ==
                    DhmpLatestStateWindow.Capacity;

            checks.Add(
                new DhmpCorrectnessCheck(
                    $"{mode}{(smoothing ? " + Ring-3" : string.Empty)} publication",
                    publicationOk && ringOk,
                    $"published={published}, expected={expected}, ring={server.NativeSmoothingRecordCount}"));
        }

        return checks.ToArray();
    }

    private static DhmpReportSummary BuildSummary(
        IReadOnlyList<DhmpPathBenchmark> matrix,
        IReadOnlyList<DhmpWorkerScalingBenchmark> scaling,
        IReadOnlyList<DhmpAllocationBenchmark> allocations,
        IReadOnlyList<DhmpCorrectnessCheck> checks)
    {
        DhmpPathBenchmark fastestCore =
            matrix.MinBy(row => row.ProcessorNanoseconds.Median)!;

        DhmpPathBenchmark fastestEndToEnd =
            matrix.MaxBy(row => row.PacketRate)!;

        DhmpWorkerScalingBenchmark bestScaling =
            scaling.MaxBy(row => row.PacketRate)!;

        return new DhmpReportSummary(
            fastestCore.ProcessorNanoseconds.Median,
            1_000_000_000d /
                fastestCore.ProcessorNanoseconds.Median,
            fastestEndToEnd.PacketRate,
            fastestEndToEnd.PacketBytes,
            fastestEndToEnd.ReceiveMode,
            fastestEndToEnd.NativeSmoothing,
            bestScaling.PacketRate,
            bestScaling.Workers,
            bestScaling.PacketBytes,
            allocations.Max(row => row.FullPathBytesPerCall),
            checks.All(check => check.Passed));
    }

    private static double MeasureNanosecondsPerCall(
        int iterations,
        Action action)
    {
        long started =
            Stopwatch.GetTimestamp();

        for (int i = 0; i < iterations; i++)
            action();

        long elapsed =
            Stopwatch.GetTimestamp() -
            started;

        return
            (double)elapsed /
            Stopwatch.Frequency *
            1_000_000_000d /
            iterations;
    }

    private static double MeasureAllocatedBytesPerCall(Action action)
    {
        const int warmup = 10_000;
        const int iterations = 100_000;

        for (int i = 0; i < warmup; i++)
            action();

        long before =
            GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < iterations; i++)
            action();

        long after =
            GC.GetAllocatedBytesForCurrentThread();

        return
            (double)(after - before) /
            iterations;
    }

    private static DhmpSampleStats Stats(double[] values)
    {
        double median =
            Median(values);

        double average =
            values.Average();

        double variance =
            values.Sum(value =>
                Math.Pow(
                    value - average,
                    2)) /
            values.Length;

        double stdDev =
            Math.Sqrt(variance);

        return new DhmpSampleStats(
            median,
            values.Min(),
            values.Max(),
            average,
            average == 0
                ? 0
                : stdDev / average * 100d);
    }

    private static double Median(double[] values)
    {
        double[] copy =
            (double[])values.Clone();

        Array.Sort(copy);

        int middle =
            copy.Length / 2;

        return (copy.Length & 1) == 1
            ? copy[middle]
            : (copy[middle - 1] +
               copy[middle]) / 2d;
    }

    private static void FillPacket(byte[] packet)
    {
        for (int offset = 0;
             offset < packet.Length;
             offset += RecordSize)
        {
            BitConverter.TryWriteBytes(
                packet.AsSpan(offset, 8),
                (long)(offset / RecordSize));

            BitConverter.TryWriteBytes(
                packet.AsSpan(offset + 8, 8),
                ~(long)(offset / RecordSize));
        }
    }

    private sealed class ReportSender : IDhmpPacketSender
    {
        private readonly DhmpServer _server;
        private readonly Action<ReadOnlySpan<byte>> _publish;

        public ReportSender(
            DhmpServer server,
            Action<ReadOnlySpan<byte>> publish)
        {
            _server = server;
            _publish = publish;
        }

        public int MaximumPayloadBytes =>
            DhmpFullReportLab.MaximumPayloadBytes;

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _server.ProcessPacket(
                payload.Span,
                _publish);

            if (_server.NativeSmoothingEnabled)
            {
                Span<byte> window =
                    stackalloc byte[
                        DhmpLatestStateWindow.Capacity *
                        RecordSize];

                _server.CopyNativeSmoothingWindow(
                    window);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConfirmationReportSender : IDhmpPacketSender
    {
        private readonly DhmpServer _forwardServer;
        private readonly DhmpStressConfirmationMode _mode;
        private readonly DhmpServer _returnServer;
        private readonly byte[] _scratch =
            new byte[MaximumPayloadBytes];

        public ConfirmationReportSender(
            DhmpServer forwardServer,
            DhmpStressConfirmationMode mode)
        {
            _forwardServer = forwardServer;
            _mode = mode;
            _returnServer =
                new DhmpServer(
                    new DhmpWireContract(RecordSize),
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        MaximumPayloadBytes));
        }

        public int MaximumPayloadBytes =>
            MaximumPayloadBytes;

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _forwardServer.ProcessPacket(
                payload.Span,
                static _ => { });

            if (_mode == DhmpStressConfirmationMode.ApplicationId)
            {
                int ids =
                    payload.Length /
                    RecordSize;

                int returnBytes =
                    ((ids + 1) / 2) *
                    RecordSize;

                Span<byte> confirmation =
                    _scratch.AsSpan(
                        0,
                        returnBytes);

                for (int index = 0;
                     index < ids;
                     index++)
                {
                    payload.Span
                        .Slice(
                            index * RecordSize,
                            8)
                        .CopyTo(
                            confirmation.Slice(
                                index * 8,
                                8));
                }

                confirmation[(ids * 8)..].Clear();

                _returnServer.ProcessPacket(
                    confirmation,
                    static _ => { });
            }
            else if (_mode == DhmpStressConfirmationMode.FullEcho)
            {
                _returnServer.ProcessPacket(
                    payload.Span,
                    static _ => { });
            }

            return ValueTask.CompletedTask;
        }
    }
}

internal sealed record DhmpFullReportStatus(
    bool Running,
    string Phase,
    int CompletedSteps,
    int TotalSteps,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? CompletedUtc,
    DhmpFullReport? Report,
    string? Error);

internal sealed record DhmpFullReport(
    DateTimeOffset GeneratedUtc,
    DhmpReportEnvironment Environment,
    DhmpReportSummary Summary,
    DhmpPathBenchmark[] PathMatrix,
    DhmpWorkerScalingBenchmark[] WorkerScaling,
    DhmpRatePolicyBenchmark[] RatePolicies,
    DhmpConfirmationBenchmark[] ConfirmationModes,
    DhmpAllocationBenchmark[] Allocations,
    DhmpCorrectnessCheck[] CorrectnessChecks,
    string[] InterpretationNotes);

internal sealed record DhmpReportEnvironment(
    string MachineName,
    string OS,
    string Architecture,
    string Runtime,
    int LogicalProcessors,
    bool ServerGC,
    string GCLatencyMode,
    long StopwatchFrequency,
    int RecordSize,
    int MaximumPayloadBytes);

internal sealed record DhmpReportSummary(
    double FastestCoreNanosecondsPerPacket,
    double FastestCorePacketCeiling,
    double FastestSinglePathPacketRate,
    int FastestSinglePathPacketBytes,
    string FastestSinglePathReceiveMode,
    bool FastestSinglePathNativeSmoothing,
    double BestAggregatePacketRate,
    int BestAggregateWorkers,
    int BestAggregatePacketBytes,
    double WorstMeasuredFullPathAllocationBytesPerCall,
    bool AllCorrectnessChecksPassed);

internal sealed record DhmpPathBenchmark(
    int PacketBytes,
    int RecordsPerPacket,
    string ReceiveMode,
    bool NativeSmoothing,
    DhmpSampleStats ProcessorNanoseconds,
    DhmpSampleStats ServerNanoseconds,
    DhmpSampleStats ClientNanoseconds,
    double PacketRate,
    double LogicalRecordsPerSecond,
    double LogicalPayloadGigabytesPerSecond,
    int PublishedRecordsPerPacket);

internal sealed record DhmpWorkerScalingBenchmark(
    int Workers,
    int PacketBytes,
    double PacketRate,
    double LogicalRecordsPerSecond,
    double LogicalPayloadGigabytesPerSecond,
    double ElapsedSeconds);

internal sealed record DhmpRatePolicyBenchmark(
    string RatePolicy,
    int PacketBytes,
    DhmpSampleStats ClientNanoseconds,
    double PacketRate);

internal sealed record DhmpConfirmationBenchmark(
    string ConfirmationMode,
    int PacketBytes,
    DhmpSampleStats RoundTripNanoseconds,
    double PacketRate,
    int ReturnBytesPerForwardPacket);

internal sealed record DhmpAllocationBenchmark(
    string ReceiveMode,
    bool NativeSmoothing,
    int PacketBytes,
    double ProcessorBytesPerCall,
    double ServerBytesPerCall,
    double FullPathBytesPerCall);

internal sealed record DhmpCorrectnessCheck(
    string Name,
    bool Passed,
    string Detail);

internal sealed record DhmpSampleStats(
    double Median,
    double Min,
    double Max,
    double Average,
    double CoefficientOfVariationPercent);
