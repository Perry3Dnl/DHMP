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
    private const int ReferencePacketBytes = 1408;

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
            _totalSteps = 30;
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
            SetPhase("Core / server / client matrix");
            CompleteStep();

            var matrix = new List<DhmpPathBenchmark>();
            foreach (int packetBytes in PacketSizes)
            {
                SetPhase($"Sequential {packetBytes:N0} B");
                matrix.Add(RunPathBenchmark(
                    packetBytes,
                    DhmpProcessingMode.Sequential,
                    nativeSmoothing: false));
                CompleteStep();

                SetPhase($"Latest {packetBytes:N0} B");
                matrix.Add(RunPathBenchmark(
                    packetBytes,
                    DhmpProcessingMode.Latest,
                    nativeSmoothing: false));
                CompleteStep();

                SetPhase($"Latest + Ring-3 {packetBytes:N0} B");
                matrix.Add(RunPathBenchmark(
                    packetBytes,
                    DhmpProcessingMode.Latest,
                    nativeSmoothing: true));
                CompleteStep();
            }

            Step("Worker scaling");
            var scaling = RunScalingBenchmarks();

            Step("Protocol comparison");
            var protocolComparisons =
                await RunProtocolComparisonBenchmarksAsync(
                    scaling).ConfigureAwait(false);

            Step("Rate-policy overhead");
            var ratePolicies = RunRatePolicyBenchmarks();

            Step("Confirmation overhead");
            var confirmations = RunConfirmationBenchmarks();

            Step("Latest sweeper / grabber");
            var ring3Consumer = RunRing3ConsumerBenchmark();

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
                protocolComparisons,
                ring3Consumer,
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
                    "Latest and Latest + Native Smoothing use the exact same packet-processing path.",
                    "The Latest sweeper owns exactly three fixed slots and never waits for a grabber.",
                    "Latest grabs the slot fully published when it looks; Native Smoothing grabs exactly N-2/N-1/N after a complete three-slot sweep window exists.",
                    "Native Smoothing adds no per-packet Ring-3 copy. Sweeper and grabber costs are benchmarked separately."
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
        SetPhase(phase);
        CompleteStep();
    }

    private void SetPhase(string phase)
    {
        lock (_gate)
            _phase = phase;
    }

    private void CompleteStep()
    {
        lock (_gate)
        {
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

        int warmupIterations =
            ScaledIterations(
                WarmupIterations,
                packetBytes);

        int measuredIterations =
            ScaledIterations(
                MeasuredIterations,
                packetBytes);

        for (int i = 0; i < warmupIterations; i++)
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
                    measuredIterations,
                    () => processor.Process(packet, publish));

            serverNs[repetition] =
                MeasureNanosecondsPerCall(
                    measuredIterations,
                    () => server.ProcessPacket(packet, publish));

            clientNs[repetition] =
                MeasureNanosecondsPerCall(
                    measuredIterations,
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

    private static int ScaledIterations(
        int baseIterations,
        int packetBytes)
    {
        if (packetBytes <= ReferencePacketBytes)
            return baseIterations;

        // Keep large-packet benchmarks bounded by roughly the same total byte
        // workload as the 1,408-byte network-oriented reference. Sequential
        // now moves every 16-byte record through its real FIFO path, so a fixed
        // packet-call count would otherwise multiply benchmark work by packet
        // size and make 65,520-byte rows take impractically long.
        long scaled =
            (long)baseIterations *
            ReferencePacketBytes /
            packetBytes;

        return checked((int)Math.Max(2_000L, scaled));
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

    private async Task<DhmpProtocolComparisonBenchmark[]> RunProtocolComparisonBenchmarksAsync(
        IReadOnlyList<DhmpWorkerScalingBenchmark> scaling)
    {
        const int packetBytes = 1408;
        int workers =
            Math.Min(
                4,
                Math.Max(1, _afXdpLab.MaxWorkers));

        var results =
            new List<DhmpProtocolComparisonBenchmark>();

        results.AddRange(
            await ProtocolComparisonBenchmarks.RunAsync(
                packetBytes).ConfigureAwait(false));

        DhmpWorkerScalingBenchmark? dhmp =
            scaling.FirstOrDefault(
                row =>
                    row.Workers == workers &&
                    row.PacketBytes == packetBytes);

        if (dhmp is not null)
        {
            results.Add(
                new DhmpProtocolComparisonBenchmark(
                    "DHMP in-memory full path",
                    $"{workers} workers, client → sender → server",
                    packetBytes,
                    dhmp.PacketRate,
                    dhmp.LogicalPayloadGigabytesPerSecond,
                    100_000L * workers,
                    false,
                    "Measured by this Full Report run. This is a software-path ceiling, not physical wire throughput."));
        }

        try
        {
            DhmpAfXdpComparisonSample network =
                await _afXdpLab.RunComparisonSampleAsync(
                    packetBytes,
                    workers,
                    packetsPerWorker: 50_000).ConfigureAwait(false);

            results.Add(
                new DhmpProtocolComparisonBenchmark(
                    "DHMP Raw IPv6",
                    $"{workers} workers, raw IPv6 transmit",
                    packetBytes,
                    network.RawPacketRate,
                    network.RawPayloadGigabytesPerSecond,
                    50_000L * workers,
                    false,
                    "Measured on the benchmark interfaces using the normal kernel raw-IPv6 path."));

            results.Add(
                new DhmpProtocolComparisonBenchmark(
                    "DHMP + AF_XDP",
                    $"{workers} workers, AF_XDP {network.Mode}",
                    packetBytes,
                    network.AfXdpPacketRate,
                    network.AfXdpPayloadGigabytesPerSecond,
                    50_000L * workers,
                    true,
                    $"Measured AF_XDP mode: {network.Mode}."));
        }
        catch (Exception exception)
        {
            results.Add(
                new DhmpProtocolComparisonBenchmark(
                    "DHMP Raw IPv6 / AF_XDP",
                    $"{workers} workers",
                    packetBytes,
                    0,
                    0,
                    0,
                    true,
                    $"Network comparison unavailable: {exception.GetBaseException().Message}"));
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

    private static DhmpRing3ConsumerBenchmark RunRing3ConsumerBenchmark()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(RecordSize),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Latest,
                    MaximumPayloadBytes,
                    nativeSmoothing: true));

        // Prime one full physical three-slot sweep window. No packet-path
        // observer is involved; these are the slots the grabber reads.
        for (int index = 0;
             index < DhmpLatestStateWindow.Capacity;
             index++)
        {
            Span<byte> slot =
                server.BeginLatestSweep();

            slot.Clear();
            slot[0] = (byte)(index + 1);

            server.CommitLatestSweep();
        }

        byte[] latestDestination =
            new byte[RecordSize];

        byte[] smoothingDestination =
            new byte[
                DhmpLatestStateWindow.Capacity *
                RecordSize];

        for (int i = 0; i < WarmupIterations; i++)
        {
            server.CopyLatest(
                latestDestination);

            server.CopyNativeSmoothingWindow(
                smoothingDestination);
        }

        double[] sweeperNs =
            new double[Repetitions];

        double[] latestGrabNs =
            new double[Repetitions];

        double[] smoothingGrabNs =
            new double[Repetitions];

        for (int repetition = 0;
             repetition < Repetitions;
             repetition++)
        {
            sweeperNs[repetition] =
                MeasureNanosecondsPerCall(
                    measuredIterations,
                    () =>
                    {
                        server.BeginLatestSweep().Clear();
                        server.CommitLatestSweep();
                    });

            latestGrabNs[repetition] =
                MeasureNanosecondsPerCall(
                    measuredIterations,
                    () => server.CopyLatest(
                        latestDestination));

            smoothingGrabNs[repetition] =
                MeasureNanosecondsPerCall(
                    measuredIterations,
                    () => server.CopyNativeSmoothingWindow(
                        smoothingDestination));
        }

        DhmpSampleStats smoothingStats =
            Stats(smoothingGrabNs);

        return new DhmpRing3ConsumerBenchmark(
            RecordSize,
            DhmpLatestStateWindow.Capacity,
            Stats(sweeperNs),
            Stats(latestGrabNs),
            smoothingStats,
            smoothingStats.Median * 60d,
            smoothingStats.Median * 120d);
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

            bool grabberOk = true;
            int grabbed = 0;

            if (mode == DhmpProcessingMode.Latest)
            {
                // The sweeper is a separate stage. Populate exactly the same
                // three slots regardless of which grabber mode is selected.
                for (int index = 0;
                     index < DhmpLatestStateWindow.Capacity;
                     index++)
                {
                    Span<byte> slot =
                        server.BeginLatestSweep();

                    slot.Clear();
                    slot[0] =
                        (byte)(index + 1);

                    server.CommitLatestSweep();
                }

                if (smoothing)
                {
                    byte[] destination =
                        new byte[
                            RecordSize *
                            DhmpLatestStateWindow.Capacity];

                    grabbed =
                        server.CopyNativeSmoothingWindow(
                            destination);

                    grabberOk =
                        grabbed ==
                            DhmpLatestStateWindow.Capacity &&
                        destination[0] == 1 &&
                        destination[RecordSize] == 2 &&
                        destination[RecordSize * 2] == 3;
                }
                else
                {
                    byte[] destination =
                        new byte[RecordSize];

                    grabbed =
                        server.CopyLatest(
                            destination);

                    grabberOk =
                        grabbed == 1 &&
                        destination[0] == 3;
                }
            }

            checks.Add(
                new DhmpCorrectnessCheck(
                    $"{mode}{(smoothing ? " + Native Smoothing" : string.Empty)} publication/grab",
                    publicationOk && grabberOk,
                    $"published={published}, expected={expected}, grabbed={grabbed}, sweepSlots={(mode == DhmpProcessingMode.Latest ? DhmpLatestStateWindow.Capacity : 0)}"));
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


            return ValueTask.CompletedTask;
        }
    }

    private sealed class ConfirmationReportSender : IDhmpPacketSender
    {
        private readonly DhmpServer _forwardServer;
        private readonly DhmpStressConfirmationMode _mode;
        private readonly DhmpServer _returnServer;
        private readonly byte[] _scratch =
            new byte[DhmpFullReportLab.MaximumPayloadBytes];

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
            DhmpFullReportLab.MaximumPayloadBytes;

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
    DhmpProtocolComparisonBenchmark[] ProtocolComparisons,
    DhmpRing3ConsumerBenchmark Ring3Consumer,
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

internal sealed record DhmpProtocolComparisonBenchmark(
    string Protocol,
    string Scope,
    int PacketBytes,
    double PacketRate,
    double PayloadGigabytesPerSecond,
    long Operations,
    bool KernelBypass,
    string Detail);

internal sealed record DhmpRing3ConsumerBenchmark(
    int RecordBytes,
    int SweepSlots,
    DhmpSampleStats SweeperNanoseconds,
    DhmpSampleStats LatestGrabNanoseconds,
    DhmpSampleStats NativeSmoothingGrabNanoseconds,
    double NativeSmoothingNanosecondsPerSecondAt60Hz,
    double NativeSmoothingNanosecondsPerSecondAt120Hz);

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
