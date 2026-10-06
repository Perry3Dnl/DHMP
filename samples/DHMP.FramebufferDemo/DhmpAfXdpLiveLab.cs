using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DHMP.AfXdp;
using DHMP.Protocol;
using DHMP.RawIpv6;

internal sealed class DhmpAfXdpLiveLab : BackgroundService
{
    private const long PacketsPerWorkerSample = 250_000;
    private const int MaximumWorkers = 16;

    private readonly object _gate = new();

    private int _payloadBytes = 1408;
    private int _workers = Math.Min(MaximumWorkers, Math.Max(1, Environment.ProcessorCount));
    private long _configurationVersion;
    private bool _enabled;

    private long _afXdpPacketsCompleted;
    private long _afXdpPayloadBytesCompleted;
    private long _afXdpTicks;

    private long _rawPacketsCompleted;
    private long _rawPayloadBytesCompleted;
    private long _rawTicks;

    private long _runs;
    private long _failures;
    private bool _isRunning;
    private string _mode = "Unknown";
    private string _detail = "Waiting for the first paired benchmark sample.";

    private readonly string _interfacePrefix =
        Environment.GetEnvironmentVariable("DHMP_AFXDP_INTERFACE_PREFIX")
        ?? "dhmpxdp";

    public int MaxWorkers =>
        Math.Min(
            MaximumWorkers,
            Math.Max(1, Environment.ProcessorCount * 2));

    public void Pause()
    {
        lock (_gate)
        {
            if (!_enabled)
                return;

            _enabled = false;
            _configurationVersion++;
            _isRunning = false;
            _detail = "Paused while another live benchmark owns the host.";
        }
    }

    public void Configure(
        int payloadBytes,
        int workers)
    {
        if (payloadBytes <= 0 ||
            payloadBytes > 1408 ||
            payloadBytes % DhmpThroughputLab.RecordSize != 0)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));

        if (workers <= 0 ||
            workers > MaxWorkers)
            throw new ArgumentOutOfRangeException(nameof(workers));

        lock (_gate)
        {
            _enabled = true;
            _payloadBytes = payloadBytes;
            _workers = workers;
            _configurationVersion++;
        }
    }

    public DhmpAfXdpLiveSnapshot Snapshot()
    {
        int payloadBytes;
        int workers;
        long version;
        string mode;
        string detail;
        bool isRunning;

        lock (_gate)
        {
            payloadBytes = _payloadBytes;
            workers = _workers;
            version = _configurationVersion;
            mode = _mode;
            detail = _detail;
            isRunning = _isRunning;
        }

        return new DhmpAfXdpLiveSnapshot(
            Environment.TickCount64,
            Interlocked.Read(ref _afXdpPacketsCompleted),
            Interlocked.Read(ref _afXdpPayloadBytesCompleted),
            Interlocked.Read(ref _afXdpTicks),
            Interlocked.Read(ref _rawPacketsCompleted),
            Interlocked.Read(ref _rawPayloadBytesCompleted),
            Interlocked.Read(ref _rawTicks),
            Stopwatch.Frequency,
            Interlocked.Read(ref _runs),
            Interlocked.Read(ref _failures),
            payloadBytes,
            payloadBytes / DhmpThroughputLab.RecordSize,
            PacketsPerWorkerSample,
            workers,
            MaxWorkers,
            Environment.ProcessorCount,
            _interfacePrefix,
            mode,
            isRunning,
            detail,
            version);
    }

    public async Task<DhmpAfXdpComparisonSample> RunComparisonSampleAsync(
        int payloadBytes,
        int workers,
        long packetsPerWorker,
        CancellationToken cancellationToken = default)
    {
        if (payloadBytes <= 0 ||
            payloadBytes > 1408 ||
            payloadBytes % DhmpThroughputLab.RecordSize != 0)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));

        if (workers <= 0 ||
            workers > MaxWorkers)
            throw new ArgumentOutOfRangeException(nameof(workers));

        if (packetsPerWorker <= 0)
            throw new ArgumentOutOfRangeException(nameof(packetsPerWorker));

        PhaseResult raw =
            await RunRawPhaseAsync(
                payloadBytes,
                workers,
                cancellationToken,
                packetsPerWorker).ConfigureAwait(false);

        AfXdpPhaseResult afXdp =
            await RunAfXdpPhaseAsync(
                payloadBytes,
                workers,
                cancellationToken,
                packetsPerWorker).ConfigureAwait(false);

        double rawSeconds =
            (double)raw.ElapsedTicks /
            Stopwatch.Frequency;

        double afXdpSeconds =
            (double)afXdp.ElapsedTicks /
            Stopwatch.Frequency;

        return new DhmpAfXdpComparisonSample(
            payloadBytes,
            workers,
            raw.PacketsCompleted / rawSeconds,
            raw.PayloadBytesCompleted / rawSeconds / 1_000_000_000d,
            afXdp.PacketsCompleted / afXdpSeconds,
            afXdp.PayloadBytesCompleted / afXdpSeconds / 1_000_000_000d,
            afXdp.Mode);
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int payloadBytes;
            int workers;
            long version;
            long runNumber;
            bool enabled;

            lock (_gate)
            {
                payloadBytes = _payloadBytes;
                workers = _workers;
                version = _configurationVersion;
                enabled = _enabled;

                if (enabled)
                {
                    _isRunning = true;
                }
                else
                {
                    _isRunning = false;
                    _detail = "Idle until the Kernel Bypass lab is explicitly configured.";
                }

                if (_runs == 0 && _failures == 0)
                {
                    _detail =
                        $"Running first paired Raw IPv6 and AF_XDP phases with {workers} parallel workers.";
                }
            }

            if (!enabled)
            {
                await Task.Delay(
                    250,
                    stoppingToken);

                continue;
            }

            runNumber = Interlocked.Read(ref _runs);

            try
            {
                PhaseResult raw;
                AfXdpPhaseResult afXdp;

                if ((runNumber & 1) == 0)
                {
                    raw = await RunRawPhaseAsync(
                        payloadBytes,
                        workers,
                        stoppingToken);

                    afXdp = await RunAfXdpPhaseAsync(
                        payloadBytes,
                        workers,
                        stoppingToken);
                }
                else
                {
                    afXdp = await RunAfXdpPhaseAsync(
                        payloadBytes,
                        workers,
                        stoppingToken);

                    raw = await RunRawPhaseAsync(
                        payloadBytes,
                        workers,
                        stoppingToken);
                }

                Interlocked.Add(
                    ref _afXdpPacketsCompleted,
                    afXdp.PacketsCompleted);

                Interlocked.Add(
                    ref _afXdpPayloadBytesCompleted,
                    afXdp.PayloadBytesCompleted);

                Interlocked.Add(
                    ref _afXdpTicks,
                    afXdp.ElapsedTicks);

                Interlocked.Add(
                    ref _rawPacketsCompleted,
                    raw.PacketsCompleted);

                Interlocked.Add(
                    ref _rawPayloadBytesCompleted,
                    raw.PayloadBytesCompleted);

                Interlocked.Add(
                    ref _rawTicks,
                    raw.ElapsedTicks);

                Interlocked.Increment(ref _runs);

                lock (_gate)
                {
                    _mode = afXdp.Mode;
                    _isRunning = false;
                    _detail =
                        $"Completed paired {workers}-worker sample. AF_XDP mode: {afXdp.Mode}.";
                }

                lock (_gate)
                {
                    if (_configurationVersion != version)
                        continue;
                }

                await Task.Yield();
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _failures);

                lock (_gate)
                {
                    _isRunning = false;
                    _detail =
                        $"Worker phase failed: {exception.GetBaseException().Message}";
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    stoppingToken);
            }
        }
    }

    private async Task<PhaseResult> RunRawPhaseAsync(
        int payloadBytes,
        int workers,
        CancellationToken cancellationToken,
        long packetsPerWorker = PacketsPerWorkerSample)
    {
        using var startGate = new ManualResetEventSlim(false);

        Task<WorkerResult>[] tasks =
            Enumerable.Range(0, workers)
                .Select(
                    worker =>
                        Task.Factory.StartNew(
                            () =>
                            {
                                startGate.Wait(cancellationToken);

                                return RunRawIpv6Transmit(
                                    worker,
                                    payloadBytes,
                                    packetsPerWorker);
                            },
                            cancellationToken,
                            TaskCreationOptions.LongRunning,
                            TaskScheduler.Default))
                .ToArray();

        long started = Stopwatch.GetTimestamp();
        startGate.Set();

        WorkerResult[] results =
            await Task.WhenAll(tasks);

        long elapsed =
            Stopwatch.GetTimestamp() - started;

        return new PhaseResult(
            results.Sum(result => result.PacketsCompleted),
            results.Sum(result => result.PayloadBytesCompleted),
            elapsed);
    }

    private async Task<AfXdpPhaseResult> RunAfXdpPhaseAsync(
        int payloadBytes,
        int workers,
        CancellationToken cancellationToken,
        long packetsPerWorker = PacketsPerWorkerSample)
    {
        using var startGate = new ManualResetEventSlim(false);

        Task<DhmpAfXdpBenchmarkResult>[] tasks =
            Enumerable.Range(0, workers)
                .Select(
                    worker =>
                        Task.Factory.StartNew(
                            () =>
                            {
                                startGate.Wait(cancellationToken);

                                return DhmpAfXdpBenchmark.RunTransmit(
                                    InterfaceName(worker),
                                    payloadBytes,
                                    packetsPerWorker,
                                    queueId: 0,
                                    preferZeroCopy: true);
                            },
                            cancellationToken,
                            TaskCreationOptions.LongRunning,
                            TaskScheduler.Default))
                .ToArray();

        long started = Stopwatch.GetTimestamp();
        startGate.Set();

        DhmpAfXdpBenchmarkResult[] results =
            await Task.WhenAll(tasks);

        long elapsed =
            Stopwatch.GetTimestamp() - started;

        DhmpAfXdpBenchmarkResult? failed =
            results.FirstOrDefault(
                result => !result.Supported);

        if (failed is not null)
        {
            throw new InvalidOperationException(
                failed.Detail);
        }

        string[] modes =
            results
                .Select(result => result.Mode.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        string mode =
            modes.Length == 1
                ? modes[0]
                : string.Join("/", modes);

        return new AfXdpPhaseResult(
            results.Sum(result => result.PacketsCompleted),
            results.Sum(result => result.PayloadBytesCompleted),
            elapsed,
            mode);
    }

    private WorkerResult RunRawIpv6Transmit(
        int worker,
        int payloadBytes,
        long packets)
    {
        string interfaceName =
            InterfaceName(worker);

        NetworkInterface networkInterface =
            NetworkInterface.GetAllNetworkInterfaces()
                .SingleOrDefault(
                    candidate =>
                        string.Equals(
                            candidate.Name,
                            interfaceName,
                            StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Benchmark interface '{interfaceName}' was not found.");

        IPv6InterfaceProperties? ipv6 =
            networkInterface
                .GetIPProperties()
                .GetIPv6Properties();

        if (ipv6 is null)
        {
            throw new InvalidOperationException(
                $"Benchmark interface '{interfaceName}' has no IPv6 properties.");
        }

        var multicast =
            IPAddress.Parse("ff02::1");

        multicast.ScopeId =
            ipv6.Index;

        using Socket sender =
            DhmpLinuxRawIpv6Socket.Open(
                DhmpProtocol.ExperimentalIpv6DataNextHeader);

        sender.Bind(
            new IPEndPoint(
                RawLocalAddress(worker),
                0));

        sender.SendBufferSize =
            16 * 1024 * 1024;

        byte[] payload =
            GC.AllocateUninitializedArray<byte>(
                payloadBytes);

        var contract =
            new DhmpWireContract(
                DhmpThroughputLab.RecordSize);

        contract.ValidatePacket(
            payload.Length,
            payloadBytes);

        EndPoint target =
            new IPEndPoint(
                multicast,
                0);

        for (long packet = 0;
             packet < packets;
             packet++)
        {
            BitConverter.TryWriteBytes(
                payload.AsSpan(0, 8),
                packet);

            sender.SendTo(
                payload,
                SocketFlags.None,
                target);
        }

        return new WorkerResult(
            packets,
            packets * (long)payloadBytes);
    }

    private string InterfaceName(int worker) =>
        $"{_interfacePrefix}{worker}";

    private static IPAddress RawLocalAddress(int worker) =>
        IPAddress.Parse(
            $"fd42:6468:6d70:{worker + 1:x}::1");

    private sealed record WorkerResult(
        long PacketsCompleted,
        long PayloadBytesCompleted);

    private sealed record PhaseResult(
        long PacketsCompleted,
        long PayloadBytesCompleted,
        long ElapsedTicks);

    private sealed record AfXdpPhaseResult(
        long PacketsCompleted,
        long PayloadBytesCompleted,
        long ElapsedTicks,
        string Mode);
}

internal sealed record DhmpAfXdpLiveSnapshot(
    long TimestampMilliseconds,
    long AfXdpPacketsCompleted,
    long AfXdpPayloadBytesCompleted,
    long AfXdpBenchmarkTicks,
    long RawPacketsCompleted,
    long RawPayloadBytesCompleted,
    long RawBenchmarkTicks,
    long StopwatchFrequency,
    long Runs,
    long Failures,
    int PayloadBytes,
    int RecordsPerPacket,
    long PacketsPerWorkerSample,
    int Workers,
    int MaxWorkers,
    int LogicalProcessors,
    string InterfacePrefix,
    string Mode,
    bool IsRunning,
    string Detail,
    long ConfigurationVersion);

internal sealed record DhmpAfXdpConfigureRequest(
    int PayloadBytes,
    int Workers);

internal sealed record DhmpAfXdpComparisonSample(
    int PayloadBytes,
    int Workers,
    double RawPacketRate,
    double RawPayloadGigabytesPerSecond,
    double AfXdpPacketRate,
    double AfXdpPayloadGigabytesPerSecond,
    string Mode);
