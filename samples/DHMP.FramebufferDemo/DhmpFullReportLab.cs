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
    private const long AggregateTargetBytesPerPass = 10_000_000;
    private const double AggregateMinimumSeconds = 0.100;
    private const int AggregateRepetitions = 3;

    private static readonly int[] CanonicalRecordSizes =
        [16, 256, 1024, 1408, 4096, 16384, 65520];

    private static readonly int[] LocalBatchSizes =
        [256, 1024, 1408, 4096, 16384, 65520];

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
            _totalSteps = 98 + CanonicalRecordSizes.Length * 5 * (3 * 3 - 1);
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

            foreach (int recordBytes in CanonicalRecordSizes)
            {
                foreach ((DhmpProcessingMode mode, bool smoothing) in new[]
                         {
                             (DhmpProcessingMode.Sequential, false),
                             (DhmpProcessingMode.UnsafeSequential, false),
                             (DhmpProcessingMode.UnsafeLatest, false),
                             (DhmpProcessingMode.Latest, false),
                             (DhmpProcessingMode.Latest, true)
                         })
                {
                    SetPhase(
                        $"{mode}{(smoothing ? " + Ring-3" : string.Empty)} canonical {recordBytes:N0} B record");

                    matrix.Add(
                        RunCanonicalPathBenchmark(
                            recordBytes,
                            mode,
                            smoothing));

                    CompleteStep();
                }
            }

            var aggregateTiming =
                new List<DhmpAggregateTimingBenchmark>();

            foreach (int recordBytes in CanonicalRecordSizes)
            {
                foreach ((DhmpProcessingMode mode, bool smoothing) in new[]
                         {
                             (DhmpProcessingMode.Sequential, false),
                             (DhmpProcessingMode.UnsafeSequential, false),
                             (DhmpProcessingMode.UnsafeLatest, false),
                             (DhmpProcessingMode.Latest, false),
                             (DhmpProcessingMode.Latest, true)
                         })
                {
                    DhmpAggregateTimingSample? direct = null;
                    foreach (DhmpRatePolicy ratePolicy in Enum.GetValues<DhmpRatePolicy>())
                    foreach (DhmpStressConfirmationMode confirmation in Enum.GetValues<DhmpStressConfirmationMode>())
                    {
                        SetPhase($"{mode}{(smoothing ? " + smoothing" : string.Empty)} / {ratePolicy} / {confirmation}: 3 × ≥100 ms, {recordBytes:N0} B");
                        var result = RunAggregateTimingBenchmark(
                            recordBytes, mode, smoothing, ratePolicy, confirmation, direct);
                        direct = result.DirectReceive;
                        aggregateTiming.Add(result);
                        CompleteStep();
                    }
                }
            }

            var localBatchMatrix =
                new List<DhmpLocalBatchBenchmark>();

            foreach (int batchBytes in LocalBatchSizes)
            {
                SetPhase($"Local Sequential batch {batchBytes:N0} B");
                localBatchMatrix.Add(
                    RunLocalBatchBenchmark(
                        batchBytes,
                        DhmpProcessingMode.Sequential,
                        nativeSmoothing: false));
                CompleteStep();

                SetPhase($"Local Latest batch {batchBytes:N0} B");
                localBatchMatrix.Add(
                    RunLocalBatchBenchmark(
                        batchBytes,
                        DhmpProcessingMode.Latest,
                        nativeSmoothing: false));
                CompleteStep();

                SetPhase($"Local Latest + Ring-3 batch {batchBytes:N0} B");
                localBatchMatrix.Add(
                    RunLocalBatchBenchmark(
                        batchBytes,
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

            Step("Poke exact echo");
            var pokeBenchmarks =
                RunPokeBenchmarks();

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
                aggregateTiming.ToArray(),
                localBatchMatrix.ToArray(),
                scaling,
                ratePolicies,
                confirmations,
                protocolComparisons,
                pokeBenchmarks,
                ring3Consumer,
                allocations,
                checks,
                new[]
                {
                    "Core processor ceiling is a software processing ceiling, not physical wire throughput.",
                    "Canonical pathMatrix rows measure exactly one negotiated record per DHMP packet.",
                    "Canonical serverNanoseconds measures direct receive-destination bookkeeping after transport byte movement: Ring-3 for normal Sequential/Latest, FIFO tail for UnsafeSequential, or one reusable borrowed slot for UnsafeLatest. It does not include transport byte movement.",
                    "Canonical prebufferedServerNanoseconds reports the convenience path for an already-buffered record. Normal Sequential copies into Ring-3 then FIFO, UnsafeSequential copies into FIFO, Latest copies into Ring-3, while UnsafeLatest can synchronously publish the caller-owned record without an additional server copy.",
                    "localBatchMatrix rows are software-only compatibility/batch calls and are never wire packet-rate claims.",
                    "Logical payload GB/s in localBatchMatrix represents bytes processed by local batch APIs, not raw DHMP wire throughput.",
                    "Canonical pathMatrix varies the negotiated record size; every row still contains exactly one record per packet.",
                    "The 1,408-byte canonical row is directly comparable to the 1,408-byte raw IPv6 / AF_XDP transport reference.",
                    "Canonical packet rate is one negotiated record transaction per second.",
                    "aggregateTiming measures every canonical size/mode with all rate policies and confirmation modes. Rate limits are long.MaxValue to measure policy overhead without intentional throttling. FullClient copies supplied bytes into the receive slot and processes the confirmation return; ApplicationId returns one 8-byte application ID padded to a 16-byte record, FullEcho returns the whole record. DirectReceive is shared across send options because those options do not affect receive bookkeeping. Each path has a 50 ms warmup and three >=100 ms samples with sample statistics. This is local software processing, not physical network throughput.",
                    "aggregateTiming repeats complete passes of at least 10,000,000 logical payload bytes under one outer Stopwatch until at least 100 ms has elapsed, then divides actual elapsed nanoseconds by total packet count. A calibration pass groups enough 10 MB passes to target roughly 5 ms between clock reads, preventing Stopwatch polling from dominating very fast large-record modes.",
                    "Sequential, UnsafeSequential, Latest and UnsafeLatest all receive exactly one record per canonical packet.",
                    "Latest and Latest + Native Smoothing use the exact same packet-processing path.",
                    "The Latest sweeper owns exactly three fixed slots and never waits for a grabber.",
                    "Latest grabs the slot fully published when it looks; Native Smoothing grabs exactly N-2/N-1/N after a complete three-slot sweep window exists.",
                    "Plaintext single-peer Latest and Latest + Native Smoothing both receive directly into Ring-3; Native Smoothing changes only the downstream grabber and adds no intermediate receive copy.",
                    "UnsafeSequential is an experimental local receive policy: plaintext fixed-slot receive reserves the FIFO tail itself, so transport writes directly into FIFO-owned memory and Ring-3 is not touched.",
                    "UnsafeSequential preserves FIFO order for records accepted into the process, but a full FIFO stops posting the next socket receive earlier; kernel/network loss under overload is therefore easier to trigger and no reliability claim is implied.",
                    "UnsafeLatest is an experimental single-slot newest-state mode: plaintext fixed-slot receive writes into one reusable server-owned record buffer, publishes it synchronously, and may overwrite it on the next receive. It has no independent Latest grabber, no Ring-3 history, and no Native Smoothing window.",
                    "The Raw IPv6 UnsafeLatest transport hot path resolves its reusable slot once outside the receive loop and uses an internal unchecked synchronous publish after each accepted receive; the guarded Begin/Commit API remains available for misuse detection outside that transport loop.",
                    "Protected raw receive, multi-peer routed raw receive and UDP compatibility currently require intermediate receive/decode/routing buffers before server publication; their copy costs are not presented as part of the copy-free direct-slot processing ceiling.",
                    "Normal Sequential direct receive still transfers each completed Ring-3 record into its FIFO because Sequential owns records beyond the three-slot arrival window; that ownership copy remains in the measured Sequential cost.",
                    "Poke is a pre-handshake exact-echo control primitive. Its Full Report rows measure Span-based local echo processing, not Internet RTT or sustained network throughput.",
                    "Canonical Full Report Sequential rows use a 64-record local FIFO. Local multi-record batch rows size that synthetic FIFO to at least one complete batch so synchronous batch publication cannot self-backpressure before its grabber runs."
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

    private static DhmpReceivePolicy CreateBenchmarkReceivePolicy(
        DhmpProcessingMode mode,
        int maximumPayloadBytes,
        bool nativeSmoothing = false,
        long sequentialBacklogCapacityRecords = 64) =>
        new(
            mode,
            maximumPayloadBytes,
            nativeSmoothing,
            sequentialBacklogCapacityRecords:
                sequentialBacklogCapacityRecords);

    private static DhmpPathBenchmark RunCanonicalPathBenchmark(
        int recordBytes,
        DhmpProcessingMode mode,
        bool nativeSmoothing)
    {
        var wire =
            new DhmpWireContract(
                recordBytes);

        var policy =
            CreateBenchmarkReceivePolicy(
                mode,
                MaximumPayloadBytes,
                nativeSmoothing);

        byte[] record =
            GC.AllocateUninitializedArray<byte>(
                recordBytes);

        int published = 0;

        Action<ReadOnlySpan<byte>> publish =
            span => published += span.Length / recordBytes;

        var processor =
            new DhmpPacketProcessor(
                wire,
                policy);

        var server =
            new DhmpServer(
                wire,
                policy);

        var sender =
            new DirectReceiveReportSender(
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

        Action directReceive =
            CreateDirectReceiveAction(
                server,
                publish);

        for (int i = 0; i < WarmupIterations; i++)
        {
            processor.Process(
                record,
                publish);

            directReceive();

            server.ProcessNegotiatedRecord(
                record,
                publish);

            client.SendAsync(record)
                .GetAwaiter()
                .GetResult();
        }

        double[] processorNs =
            new double[Repetitions];

        double[] serverNs =
            new double[Repetitions];

        double[] prebufferedServerNs =
            new double[Repetitions];

        double[] clientNs =
            new double[Repetitions];

        for (int repetition = 0;
             repetition < Repetitions;
             repetition++)
        {
            processorNs[repetition] =
                MeasureNanosecondsPerCall(
                    MeasuredIterations,
                    () => processor.Process(
                        record,
                        publish));

            serverNs[repetition] =
                MeasureNanosecondsPerCall(
                    MeasuredIterations,
                    directReceive);

            prebufferedServerNs[repetition] =
                MeasureNanosecondsPerCall(
                    MeasuredIterations,
                    () => server.ProcessNegotiatedRecord(
                        record,
                        publish));

            clientNs[repetition] =
                MeasureNanosecondsPerCall(
                    MeasuredIterations,
                    () => client.SendAsync(record)
                        .GetAwaiter()
                        .GetResult());
        }

        GC.KeepAlive(published);

        double clientMedian =
            Median(clientNs);

        double packetRate =
            1_000_000_000d /
            clientMedian;

        return new DhmpPathBenchmark(
            recordBytes,
            1,
            mode.ToString(),
            nativeSmoothing,
            Stats(processorNs),
            Stats(serverNs),
            Stats(prebufferedServerNs),
            Stats(clientNs),
            packetRate,
            packetRate,
            packetRate * recordBytes / 1_000_000_000d,
            1);
    }

    private static DhmpAggregateTimingBenchmark RunAggregateTimingBenchmark(
        int recordBytes,
        DhmpProcessingMode mode,
        bool nativeSmoothing,
        DhmpRatePolicy ratePolicy,
        DhmpStressConfirmationMode confirmation,
        DhmpAggregateTimingSample? sharedDirect)
    {
        var wire = new DhmpWireContract(recordBytes);
        var server = new DhmpServer(wire,
            CreateBenchmarkReceivePolicy(mode, MaximumPayloadBytes, nativeSmoothing));
        long published = 0;
        Action<ReadOnlySpan<byte>> publish = span => published += span.Length / recordBytes;
        Action directReceive = CreateDirectReceiveAction(server, publish);
        var sender = new AggregateReportSender(server, publish, confirmation);
        var client = new DhmpClient(sender, wire,
            new DhmpSendPolicy(long.MaxValue, MaximumPayloadBytes, ratePolicy));
        byte[] record = new byte[recordBytes];
        record.AsSpan().Fill(0x5a);
        Action fullClient = () => client.SendAsync(record).GetAwaiter().GetResult();
        long packetsPerPass = (AggregateTargetBytesPerPass + recordBytes - 1L) / recordBytes;
        var direct = sharedDirect ?? MeasureAggregateTiming(directReceive, recordBytes, packetsPerPass);
        var full = MeasureAggregateTiming(fullClient, recordBytes, packetsPerPass);
        if (sender.TotalPackets == 0 || sender.LastPublishedByte != record[0])
            throw new InvalidOperationException("Aggregate path did not publish the supplied record.");
        GC.KeepAlive(published);
        return new DhmpAggregateTimingBenchmark(recordBytes, mode.ToString(), nativeSmoothing,
            ratePolicy.ToString(), confirmation.ToString(), sender.ReturnBytesPerPacket,
            AggregateTargetBytesPerPass, packetsPerPass, direct, full);
    }

    private static DhmpAggregateTimingSample MeasureAggregateTiming(
        Action action, int recordBytes, long packetsPerPass)
    {
        // Time-based warmup also gives tiered compilation time to settle.
        long warmupStart = Stopwatch.GetTimestamp();
        do
        {
            for (int i = 0; i < 10_000; i++) action();
        } while (Stopwatch.GetElapsedTime(warmupStart).TotalSeconds < 0.050);

        var samples = new DhmpAggregateTimingSample[AggregateRepetitions];
        var nanoseconds = new double[AggregateRepetitions];
        long packets = 0, bytes = 0;
        double milliseconds = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            var sample = MeasureAggregateTimingPass(action, recordBytes, packetsPerPass);
            samples[i] = sample;
            nanoseconds[i] = sample.NanosecondsPerPacket;
            packets += sample.TotalPackets;
            bytes += sample.TotalLogicalBytes;
            milliseconds += sample.ElapsedMilliseconds;
        }
        double packetRate = packets / (milliseconds / 1_000d);
        return new DhmpAggregateTimingSample(packets, bytes, samples[0].PassesPerClockCheck,
            milliseconds, milliseconds * 1_000_000d / packets, packetRate,
            packetRate * recordBytes / 1_000_000_000d, Stats(nanoseconds), samples);
    }

    private static DhmpAggregateTimingSample MeasureAggregateTimingPass(
        Action action,
        int recordBytes,
        long packetsPerPass)
    {
        long minimumTicks =
            Math.Max(
                1L,
                (long)Math.Ceiling(
                    Stopwatch.Frequency *
                    AggregateMinimumSeconds));

        // Calibrate how many complete 10 MB passes should run between
        // Stopwatch reads. Large records can make a 10 MB pass extremely
        // short; checking the clock after every such pass would itself become
        // measurable noise. Aim for roughly 5 ms of work per clock check.
        long calibrationStarted =
            Stopwatch.GetTimestamp();

        for (long packet = 0;
             packet < packetsPerPass;
             packet++)
        {
            action();
        }

        long calibrationTicks =
            Math.Max(
                1L,
                Stopwatch.GetTimestamp() -
                calibrationStarted);

        long targetCheckTicks =
            Math.Max(
                1L,
                Stopwatch.Frequency /
                200L);

        long passesPerClockCheck =
            Math.Max(
                1L,
                (targetCheckTicks +
                 calibrationTicks - 1L) /
                calibrationTicks);

        long packetsPerClockCheck =
            checked(
                packetsPerPass *
                passesPerClockCheck);

        long totalPackets = 0;

        long started =
            Stopwatch.GetTimestamp();

        long elapsed;

        do
        {
            for (long pass = 0;
                 pass < passesPerClockCheck;
                 pass++)
            {
                for (long packet = 0;
                     packet < packetsPerPass;
                     packet++)
                {
                    action();
                }
            }

            totalPackets +=
                packetsPerClockCheck;

            elapsed =
                Stopwatch.GetTimestamp() -
                started;
        }
        while (elapsed < minimumTicks);

        double elapsedSeconds =
            (double)elapsed /
            Stopwatch.Frequency;

        double elapsedNanoseconds =
            elapsedSeconds *
            1_000_000_000d;

        double nanosecondsPerPacket =
            elapsedNanoseconds /
            totalPackets;

        double packetRate =
            totalPackets /
            elapsedSeconds;

        long totalLogicalBytes =
            checked(
                totalPackets *
                (long)recordBytes);

        return new DhmpAggregateTimingSample(
            totalPackets,
            totalLogicalBytes,
            passesPerClockCheck,
            elapsedSeconds * 1_000d,
            nanosecondsPerPacket,
            packetRate,
            packetRate *
            recordBytes /
            1_000_000_000d);
    }

    private static DhmpLocalBatchBenchmark RunLocalBatchBenchmark(
        int batchBytes,
        DhmpProcessingMode mode,
        bool nativeSmoothing)
    {
        int recordsPerBatch =
            batchBytes / RecordSize;

        // DhmpServer.ProcessPacket() preserves its synchronous batch callback
        // contract by sweeping every record into Sequential FIFO first and
        // draining that FIFO only after the complete input batch was accepted.
        // A 1,408-byte local batch contains 88 x 16-byte records, so the old
        // fixed 64-record synthetic FIFO deadlocked on record 65: the producer
        // waited for space while the same call had not reached its drain phase.
        // Size only this benchmark FIFO to one whole local batch. Production
        // defaults and all DHMP core code remain unchanged.
        long benchmarkBacklogRecords =
            Math.Max(
                64L,
                recordsPerBatch);

        var wire =
            new DhmpWireContract(
                RecordSize);

        var policy =
            CreateBenchmarkReceivePolicy(
                mode,
                MaximumPayloadBytes,
                nativeSmoothing,
                benchmarkBacklogRecords);

        byte[] batch =
            GC.AllocateUninitializedArray<byte>(
                batchBytes);

        int published = 0;

        Action<ReadOnlySpan<byte>> publish =
            span => published += span.Length / RecordSize;

        var processor =
            new DhmpPacketProcessor(
                wire,
                policy);

        var server =
            new DhmpServer(
                wire,
                policy);

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

        int warmupIterations =
            ScaledIterations(
                WarmupIterations,
                batchBytes);

        int measuredIterations =
            ScaledIterations(
                MeasuredIterations,
                batchBytes);

        for (int i = 0; i < warmupIterations; i++)
        {
            processor.Process(
                batch,
                publish);

            server.ProcessPacket(
                batch,
                publish);

            client.SendBatchAsync(batch)
                .GetAwaiter()
                .GetResult();
        }

        double[] processorNs =
            new double[Repetitions];

        double[] serverNs =
            new double[Repetitions];

        double[] clientNs =
            new double[Repetitions];

        for (int repetition = 0;
             repetition < Repetitions;
             repetition++)
        {
            processorNs[repetition] =
                MeasureNanosecondsPerCall(
                    measuredIterations,
                    () => processor.Process(
                        batch,
                        publish));

            serverNs[repetition] =
                MeasureNanosecondsPerCall(
                    measuredIterations,
                    () => server.ProcessPacket(
                        batch,
                        publish));

            clientNs[repetition] =
                MeasureNanosecondsPerCall(
                    measuredIterations,
                    () => client.SendBatchAsync(batch)
                        .GetAwaiter()
                        .GetResult());
        }

        GC.KeepAlive(published);

        double clientMedian =
            Median(clientNs);

        double batchRate =
            1_000_000_000d /
            clientMedian;

        return new DhmpLocalBatchBenchmark(
            batchBytes,
            recordsPerBatch,
            mode.ToString(),
            nativeSmoothing,
            Stats(processorNs),
            Stats(serverNs),
            Stats(clientNs),
            batchRate,
            batchRate * recordsPerBatch,
            batchRate * batchBytes / 1_000_000_000d,
            mode == DhmpProcessingMode.Latest
                ? 1
                : recordsPerBatch);
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
            const int packetBytes = ReferencePacketBytes;
            const int iterationsPerWorker = 500_000;

            var startGate =
                new ManualResetEventSlim(false);

            Task<long>[] tasks =
                new Task<long>[workerCount];

            for (int worker = 0; worker < workerCount; worker++)
            {
                tasks[worker] = Task.Run(() =>
                {
                    var wire =
                        new DhmpWireContract(packetBytes);

                    var server =
                        new DhmpServer(
                            wire,
                            CreateBenchmarkReceivePolicy(
                                DhmpProcessingMode.Sequential,
                                MaximumPayloadBytes));

                    Action<ReadOnlySpan<byte>> publish =
                        static _ => { };

                    var sender =
                        new DirectReceiveReportSender(
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
                        client.SendAsync(packet)
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
                        client.SendAsync(packet)
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
                    pps,
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
                    "DHMP direct-slot processing ceiling",
                    $"{workers} workers, client validation → direct receive slot → server",
                    packetBytes,
                    dhmp.PacketRate,
                    dhmp.LogicalPayloadGigabytesPerSecond,
                    100_000L * workers,
                    false,
                    "Measured by this Full Report run. Transport byte movement is excluded because the fixed-slot receiver writes directly into the mode-owned destination: Ring-3 for normal Sequential/Latest, FIFO tail for UnsafeSequential, or one reusable slot for UnsafeLatest. This is a logical software-processing ceiling, not physical wire or memory throughput."));
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
            const int packetBytes = ReferencePacketBytes;

            var wire =
                new DhmpWireContract(packetBytes);

            var server =
                new DhmpServer(
                    wire,
                    CreateBenchmarkReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        MaximumPayloadBytes));

            Action<ReadOnlySpan<byte>> publish =
                static _ => { };

            var sender =
                new DirectReceiveReportSender(
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
                client.SendAsync(packet)
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
                        () => client.SendAsync(packet)
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
                    CreateBenchmarkReceivePolicy(
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

    private static DhmpPokeBenchmark[] RunPokeBenchmarks()
    {
        int[] packetSizes =
        [
            DhmpPokeCodec.MinimumPacketSize,
            DhmpPokeCodec.FullEchoPacketSize
        ];

        var results =
            new List<DhmpPokeBenchmark>(
                packetSizes.Length);

        foreach (int packetBytes in packetSizes)
        {
            const ulong token =
                0x44484D50504F4B45UL;

            byte[] request =
                GC.AllocateUninitializedArray<byte>(
                    packetBytes);

            byte[] echo =
                GC.AllocateUninitializedArray<byte>(
                    packetBytes);

            DhmpPokeCodec.Encode(
                token,
                request);

            void ExactEcho()
            {
                if (!DhmpPokeCodec.TryReadToken(
                        request,
                        out ulong requestToken) ||
                    requestToken != token)
                    throw new InvalidOperationException(
                        "Poke request failed validation.");

                request.AsSpan()
                    .CopyTo(
                        echo);

                if (!DhmpPokeCodec.TryReadToken(
                        echo,
                        out ulong echoedToken) ||
                    echoedToken != token ||
                    !echo.AsSpan()
                        .SequenceEqual(
                            request))
                {
                    throw new InvalidOperationException(
                        "Poke full echo modified the packet.");
                }
            }

            for (int i = 0;
                 i < WarmupIterations;
                 i++)
                ExactEcho();

            double[] samples =
                new double[Repetitions];

            for (int repetition = 0;
                 repetition < Repetitions;
                 repetition++)
            {
                samples[repetition] =
                    MeasureNanosecondsPerCall(
                        MeasuredIterations,
                        ExactEcho);
            }

            double allocationBytes =
                MeasureAllocatedBytesPerCall(
                    ExactEcho);

            DhmpSampleStats stats =
                Stats(
                    samples);

            double echoesPerSecond =
                1_000_000_000d /
                stats.Median;

            results.Add(
                new DhmpPokeBenchmark(
                    packetBytes,
                    stats,
                    echoesPerSecond,
                    echoesPerSecond *
                        packetBytes *
                        2d /
                        1_000_000_000d,
                    allocationBytes));
        }

        return results.ToArray();
    }

    private static DhmpRing3ConsumerBenchmark RunRing3ConsumerBenchmark()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(RecordSize),
                CreateBenchmarkReceivePolicy(
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
                    MeasuredIterations,
                    () =>
                    {
                        server.BeginLatestSweep().Clear();
                        server.CommitLatestSweep();
                    });

            latestGrabNs[repetition] =
                MeasureNanosecondsPerCall(
                    MeasuredIterations,
                    () => server.CopyLatest(
                        latestDestination));

            smoothingGrabNs[repetition] =
                MeasureNanosecondsPerCall(
                    MeasuredIterations,
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
                     (DhmpProcessingMode.UnsafeSequential, false),
                     (DhmpProcessingMode.UnsafeLatest, false),
                     (DhmpProcessingMode.Latest, false),
                     (DhmpProcessingMode.Latest, true)
                 })
        {
            const int packetBytes = ReferencePacketBytes;

            var wire =
                new DhmpWireContract(packetBytes);

            var policy =
                CreateBenchmarkReceivePolicy(
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
                new DirectReceiveReportSender(
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
                    () => ProcessDirectReceive(
                        server,
                        publish));

            double fullPathBytes =
                MeasureAllocatedBytesPerCall(
                    () => client.SendAsync(packet)
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
                     (DhmpProcessingMode.UnsafeSequential, false),
                     (DhmpProcessingMode.UnsafeLatest, false),
                     (DhmpProcessingMode.Latest, false),
                     (DhmpProcessingMode.Latest, true)
                 })
        {
            const int packetBytes = ReferencePacketBytes;

            var server =
                new DhmpServer(
                    new DhmpWireContract(packetBytes),
                    CreateBenchmarkReceivePolicy(
                        mode,
                        MaximumPayloadBytes,
                        smoothing));

            int published = 0;

            Memory<byte> receiveSlot =
                server.BeginNegotiatedReceiveSlot();

            receiveSlot.Span.Clear();
            receiveSlot.Span[0] = 0x5A;

            server.CommitNegotiatedReceiveSlot(
                span =>
                    published +=
                        span.Length / packetBytes);

            const int expected = 1;

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
                            packetBytes *
                            DhmpLatestStateWindow.Capacity];

                    grabbed =
                        server.CopyNativeSmoothingWindow(
                            destination);

                    grabberOk =
                        grabbed ==
                            DhmpLatestStateWindow.Capacity &&
                        destination[0] == 1 &&
                        destination[packetBytes] == 2 &&
                        destination[packetBytes * 2] == 3;
                }
                else
                {
                    byte[] destination =
                        new byte[packetBytes];

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

    private static Action CreateDirectReceiveAction(
        DhmpServer server,
        Action<ReadOnlySpan<byte>> publish)
    {
        if (server.ReceivePolicy.Mode ==
            DhmpProcessingMode.UnsafeLatest)
        {
            // Match the real Raw IPv6 hot path: resolve the permanently reused
            // single slot once outside the packet loop, then publish without
            // per-packet mode or active-slot guards.
            _ = server
                .GetUnsafeLatestReceiveMemoryUnchecked();

            return () =>
                server.PublishUnsafeLatestReceiveUnchecked(
                    publish);
        }

        return () =>
            ProcessDirectReceive(
                server,
                publish);
    }

    private static void ProcessDirectReceive(
        DhmpServer server,
        Action<ReadOnlySpan<byte>> publish)
    {
        // In the real plaintext fixed-slot path the socket writes directly
        // into server-owned receive memory: Ring-3 for normal Sequential/
        // Latest or the FIFO tail for UnsafeSequential. UnsafeLatest uses the
        // specialized unchecked action above.
        _ = server.BeginNegotiatedReceiveSlot();

        try
        {
            server.CommitNegotiatedReceiveSlot(
                publish);
        }
        catch
        {
            server.CancelNegotiatedReceiveSlot();
            throw;
        }
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

    private sealed class DirectReceiveReportSender :
        IDhmpPacketSender
    {
        private readonly int _recordSize;
        private readonly Action _directReceive;

        public DirectReceiveReportSender(
            DhmpServer server,
            Action<ReadOnlySpan<byte>> publish)
        {
            _recordSize =
                server.WireContract.RecordSize;

            _directReceive =
                CreateDirectReceiveAction(
                    server,
                    publish);
        }

        public int MaximumPayloadBytes =>
            DhmpFullReportLab.MaximumPayloadBytes;

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (payload.Length !=
                _recordSize)
            {
                throw new DhmpProtocolException(
                    "Direct-slot benchmark sender requires exactly one negotiated record.");
            }

            _directReceive();

            return ValueTask.CompletedTask;
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

            if (payload.Length == RecordSize)
            {
                _server.ProcessNegotiatedRecord(
                    payload.Span,
                    _publish);
            }
            else
            {
                _server.ProcessPacket(
                    payload.Span,
                    _publish);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class AggregateReportSender : IDhmpPacketSender
    {
        private readonly DhmpServer _server;
        private readonly Action<ReadOnlySpan<byte>> _publish;
        private readonly Action<ReadOnlySpan<byte>> _observe;
        private readonly DhmpStressConfirmationMode _confirmation;
        private readonly DhmpServer? _returnServer;
        private readonly byte[] _id = new byte[RecordSize];
        private readonly Memory<byte> _unsafeSlot;
        public long TotalPackets { get; private set; }
        public byte LastPublishedByte { get; private set; }
        public int MaximumPayloadBytes => DhmpFullReportLab.MaximumPayloadBytes;
        public int ReturnBytesPerPacket => _confirmation == DhmpStressConfirmationMode.None
            ? 0 : _confirmation == DhmpStressConfirmationMode.ApplicationId
                ? RecordSize : _server.WireContract.RecordSize;

        public AggregateReportSender(DhmpServer server, Action<ReadOnlySpan<byte>> publish,
            DhmpStressConfirmationMode confirmation)
        {
            _server = server;
            _publish = publish;
            _confirmation = confirmation;
            _observe = span => { LastPublishedByte = span[0]; _publish(span); };
            if (server.ReceivePolicy.Mode == DhmpProcessingMode.UnsafeLatest)
                _unsafeSlot = server.GetUnsafeLatestReceiveMemoryUnchecked();
            if (confirmation != DhmpStressConfirmationMode.None)
                _returnServer = new DhmpServer(
                    new DhmpWireContract(confirmation == DhmpStressConfirmationMode.ApplicationId
                        ? RecordSize : server.WireContract.RecordSize),
                    CreateBenchmarkReceivePolicy(DhmpProcessingMode.Sequential, MaximumPayloadBytes));
        }

        public ValueTask SendPacketAsync(ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (payload.Length != _server.WireContract.RecordSize)
                throw new DhmpProtocolException("Aggregate sender requires one negotiated record.");
            if (_server.ReceivePolicy.Mode == DhmpProcessingMode.UnsafeLatest)
            {
                payload.Span.CopyTo(_unsafeSlot.Span);
                _server.PublishUnsafeLatestReceiveUnchecked(_observe);
            }
            else
            {
                Memory<byte> slot = _server.BeginNegotiatedReceiveSlot();
                try
                {
                    payload.Span.CopyTo(slot.Span);
                    _server.CommitNegotiatedReceiveSlot(_observe);
                }
                catch
                {
                    _server.CancelNegotiatedReceiveSlot();
                    throw;
                }
            }
            if (_confirmation == DhmpStressConfirmationMode.ApplicationId)
            {
                // One application-owned 8-byte ID, padded to a 16-byte return record.
                payload.Span[..8].CopyTo(_id);
                _returnServer!.ProcessNegotiatedRecord(_id, static _ => { });
            }
            else if (_confirmation == DhmpStressConfirmationMode.FullEcho)
                _returnServer!.ProcessNegotiatedRecord(payload.Span, static _ => { });
            TotalPackets++;
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
                    CreateBenchmarkReceivePolicy(
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
    DhmpAggregateTimingBenchmark[] AggregateTiming,
    DhmpLocalBatchBenchmark[] LocalBatchMatrix,
    DhmpWorkerScalingBenchmark[] WorkerScaling,
    DhmpRatePolicyBenchmark[] RatePolicies,
    DhmpConfirmationBenchmark[] ConfirmationModes,
    DhmpProtocolComparisonBenchmark[] ProtocolComparisons,
    DhmpPokeBenchmark[] PokeBenchmarks,
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
    DhmpSampleStats PrebufferedServerNanoseconds,
    DhmpSampleStats ClientNanoseconds,
    double PacketRate,
    double LogicalRecordsPerSecond,
    double LogicalPayloadGigabytesPerSecond,
    int PublishedRecordsPerPacket);

internal sealed record DhmpAggregateTimingBenchmark(
    int PacketBytes,
    string ReceiveMode,
    bool NativeSmoothing,
    string RatePolicy,
    string ConfirmationMode,
    int ReturnBytesPerForwardPacket,
    long TargetLogicalBytesPerPass,
    long PacketsPerPass,
    DhmpAggregateTimingSample DirectReceive,
    DhmpAggregateTimingSample FullClient);

internal sealed record DhmpAggregateTimingSample(
    long TotalPackets,
    long TotalLogicalBytes,
    long PassesPerClockCheck,
    double ElapsedMilliseconds,
    double NanosecondsPerPacket,
    double PacketRate,
    double LogicalPayloadGigabytesPerSecond,
    DhmpSampleStats? NanosecondsPerPacketStats = null,
    DhmpAggregateTimingSample[]? Samples = null);

internal sealed record DhmpLocalBatchBenchmark(
    int BatchBytes,
    int RecordsPerBatch,
    string ReceiveMode,
    bool NativeSmoothing,
    DhmpSampleStats ProcessorNanoseconds,
    DhmpSampleStats ServerNanoseconds,
    DhmpSampleStats ClientNanoseconds,
    double BatchRate,
    double LogicalRecordsPerSecond,
    double LogicalPayloadGigabytesPerSecond,
    int PublishedRecordsPerBatch);

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

internal sealed record DhmpPokeBenchmark(
    int PacketBytes,
    DhmpSampleStats RoundTripNanoseconds,
    double EchoesPerSecond,
    double RoundTripGigabytesPerSecond,
    double AllocatedBytesPerEcho);

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

