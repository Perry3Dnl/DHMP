using System.Diagnostics;
using DHMP.AfXdp;

internal sealed class DhmpAfXdpLiveLab : BackgroundService
{
    private readonly object _gate = new();

    private int _payloadBytes = 1200;
    private long _configurationVersion;

    private long _packetsCompleted;
    private long _payloadBytesCompleted;
    private long _benchmarkTicks;
    private long _runs;
    private long _failures;
    private string _mode = "Probing";
    private string _detail = "AF_XDP capability probe pending.";

    private readonly string _interfaceName =
        Environment.GetEnvironmentVariable("DHMP_AFXDP_INTERFACE")
        ?? "dhmpxdp0";

    public void Configure(int payloadBytes)
    {
        if (payloadBytes <= 0 ||
            payloadBytes > 1408 ||
            payloadBytes % DhmpThroughputLab.RecordSize != 0)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));

        lock (_gate)
        {
            _payloadBytes = payloadBytes;
            _configurationVersion++;
        }
    }

    public DhmpAfXdpLiveSnapshot Snapshot()
    {
        int payloadBytes;
        long version;
        string mode;
        string detail;

        lock (_gate)
        {
            payloadBytes = _payloadBytes;
            version = _configurationVersion;
            mode = _mode;
            detail = _detail;
        }

        return new DhmpAfXdpLiveSnapshot(
            Environment.TickCount64,
            Interlocked.Read(ref _packetsCompleted),
            Interlocked.Read(ref _payloadBytesCompleted),
            Interlocked.Read(ref _benchmarkTicks),
            Stopwatch.Frequency,
            Interlocked.Read(ref _runs),
            Interlocked.Read(ref _failures),
            payloadBytes,
            payloadBytes / DhmpThroughputLab.RecordSize,
            _interfaceName,
            mode,
            detail,
            version);
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int payloadBytes;
            long version;

            lock (_gate)
            {
                payloadBytes = _payloadBytes;
                version = _configurationVersion;
            }

            try
            {
                // The benchmark is also the capability check. Avoid opening a
                // short-lived probe socket immediately before the real socket,
                // because some AF_XDP/veth combinations keep queue ownership
                // transiently busy after close.
                lock (_gate)
                {
                    _mode = "Probing";
                    _detail = "Initializing the best available AF_XDP TX mode.";
                }

                long started = Stopwatch.GetTimestamp();

                DhmpAfXdpBenchmarkResult result =
                    await Task.Run(
                        () => DhmpAfXdpBenchmark.RunTransmit(
                            _interfaceName,
                            payloadBytes,
                            packets: 250_000,
                            queueId: 0,
                            preferZeroCopy: true),
                        stoppingToken);

                long elapsed =
                    Stopwatch.GetTimestamp() - started;

                if (!result.Supported)
                {
                    Interlocked.Increment(ref _failures);

                    lock (_gate)
                    {
                        _mode = "Unavailable";
                        _detail = result.Detail;
                    }

                    await Task.Delay(
                        TimeSpan.FromSeconds(2),
                        stoppingToken);
                    continue;
                }

                Interlocked.Add(
                    ref _packetsCompleted,
                    result.PacketsCompleted);

                Interlocked.Add(
                    ref _payloadBytesCompleted,
                    result.PayloadBytesCompleted);

                Interlocked.Add(
                    ref _benchmarkTicks,
                    elapsed);

                Interlocked.Increment(ref _runs);

                lock (_gate)
                {
                    _mode = result.Mode.ToString();
                    _detail = result.Detail;
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
                    _mode = "Unavailable";
                    _detail = exception.GetBaseException().Message;
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    stoppingToken);
            }
        }
    }
}

internal sealed record DhmpAfXdpLiveSnapshot(
    long TimestampMilliseconds,
    long PacketsCompleted,
    long PayloadBytesCompleted,
    long BenchmarkTicks,
    long StopwatchFrequency,
    long Runs,
    long Failures,
    int PayloadBytes,
    int RecordsPerPacket,
    string InterfaceName,
    string Mode,
    string Detail,
    long ConfigurationVersion);

internal sealed record DhmpAfXdpConfigureRequest(
    int PayloadBytes);
