using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DHMP.AfXdp;
using DHMP.Protocol;
using DHMP.RawIpv6;

internal sealed class DhmpAfXdpLiveLab : BackgroundService
{
    private const long PacketsPerSample = 250_000;

    private readonly object _gate = new();

    private int _payloadBytes = 1200;
    private long _configurationVersion;

    private long _afXdpPacketsCompleted;
    private long _afXdpPayloadBytesCompleted;
    private long _afXdpTicks;

    private long _rawPacketsCompleted;
    private long _rawPayloadBytesCompleted;
    private long _rawTicks;

    private long _runs;
    private long _failures;
    private string _mode = "Probing";
    private string _detail = "AF_XDP capability probe pending.";

    private readonly string _interfaceName =
        Environment.GetEnvironmentVariable("DHMP_AFXDP_INTERFACE")
        ?? "dhmpxdp0";

    private readonly IPAddress _rawLocalAddress =
        IPAddress.Parse(
            Environment.GetEnvironmentVariable("DHMP_AFXDP_RAW_LOCAL")
            ?? "fd42:6468:6d70::1");

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
            PacketsPerSample,
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
            long runNumber;

            lock (_gate)
            {
                payloadBytes = _payloadBytes;
                version = _configurationVersion;
            }

            runNumber = Interlocked.Read(ref _runs);

            try
            {
                lock (_gate)
                {
                    _mode = "Probing";
                    _detail = "Running paired Raw IPv6 and AF_XDP TX samples.";
                }

                RawTxResult raw;
                DhmpAfXdpBenchmarkResult afXdp;

                if ((runNumber & 1) == 0)
                {
                    raw = await Task.Run(
                        () => RunRawIpv6Transmit(
                            payloadBytes,
                            PacketsPerSample),
                        stoppingToken);

                    afXdp = await Task.Run(
                        () => RunAfXdp(
                            payloadBytes,
                            PacketsPerSample),
                        stoppingToken);
                }
                else
                {
                    afXdp = await Task.Run(
                        () => RunAfXdp(
                            payloadBytes,
                            PacketsPerSample),
                        stoppingToken);

                    raw = await Task.Run(
                        () => RunRawIpv6Transmit(
                            payloadBytes,
                            PacketsPerSample),
                        stoppingToken);
                }

                if (!afXdp.Supported)
                {
                    Interlocked.Increment(ref _failures);

                    lock (_gate)
                    {
                        _mode = "Unavailable";
                        _detail = afXdp.Detail;
                    }

                    await Task.Delay(
                        TimeSpan.FromSeconds(2),
                        stoppingToken);
                    continue;
                }

                long afXdpTicks =
                    (long)Math.Round(
                        afXdp.Seconds *
                        Stopwatch.Frequency);

                long rawTicks =
                    (long)Math.Round(
                        raw.Seconds *
                        Stopwatch.Frequency);

                Interlocked.Add(
                    ref _afXdpPacketsCompleted,
                    afXdp.PacketsCompleted);

                Interlocked.Add(
                    ref _afXdpPayloadBytesCompleted,
                    afXdp.PayloadBytesCompleted);

                Interlocked.Add(
                    ref _afXdpTicks,
                    afXdpTicks);

                Interlocked.Add(
                    ref _rawPacketsCompleted,
                    raw.PacketsCompleted);

                Interlocked.Add(
                    ref _rawPayloadBytesCompleted,
                    raw.PayloadBytesCompleted);

                Interlocked.Add(
                    ref _rawTicks,
                    rawTicks);

                Interlocked.Increment(ref _runs);

                lock (_gate)
                {
                    _mode = afXdp.Mode.ToString();
                    _detail =
                        $"{afXdp.Detail} Paired Raw IPv6 TX sample completed.";
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

    private DhmpAfXdpBenchmarkResult RunAfXdp(
        int payloadBytes,
        long packets) =>
        DhmpAfXdpBenchmark.RunTransmit(
            _interfaceName,
            payloadBytes,
            packets,
            queueId: 0,
            preferZeroCopy: true);

    private RawTxResult RunRawIpv6Transmit(
        int payloadBytes,
        long packets)
    {
        NetworkInterface networkInterface =
            NetworkInterface.GetAllNetworkInterfaces()
                .SingleOrDefault(
                    candidate =>
                        string.Equals(
                            candidate.Name,
                            _interfaceName,
                            StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Benchmark interface '{_interfaceName}' was not found.");

        IPv6InterfaceProperties? ipv6 =
            networkInterface
                .GetIPProperties()
                .GetIPv6Properties();

        if (ipv6 is null)
        {
            throw new InvalidOperationException(
                $"Benchmark interface '{_interfaceName}' has no IPv6 properties.");
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
                _rawLocalAddress,
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

        long started =
            Stopwatch.GetTimestamp();

        for (long packet = 0;
             packet < packets;
             packet++)
        {
            BitConverter.TryWriteBytes(
                payload.AsSpan(0, Math.Min(8, payload.Length)),
                packet);

            sender.SendTo(
                payload,
                SocketFlags.None,
                target);
        }

        long finished =
            Stopwatch.GetTimestamp();

        double seconds =
            (finished - started) /
            (double)Stopwatch.Frequency;

        return new RawTxResult(
            packets,
            packets * (long)payloadBytes,
            seconds);
    }

    private sealed record RawTxResult(
        long PacketsCompleted,
        long PayloadBytesCompleted,
        double Seconds);
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
    long PacketsPerSample,
    string InterfaceName,
    string Mode,
    string Detail,
    long ConfigurationVersion);

internal sealed record DhmpAfXdpConfigureRequest(
    int PayloadBytes);
