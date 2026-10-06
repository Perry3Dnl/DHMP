using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

internal static class ProtocolComparisonBenchmarks
{
    private const int TcpIterations = 20_000;
    private const int UdpIterations = 10_000;
    private const int HttpIterations = 1_000;
    private const int HttpWarmupIterations = 100;

    public static async Task<DhmpProtocolComparisonBenchmark[]> RunAsync(
        int payloadBytes,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DhmpProtocolComparisonBenchmark>
        {
            await RunTcpAsync(payloadBytes, cancellationToken).ConfigureAwait(false),
            await RunUdpAsync(payloadBytes, cancellationToken).ConfigureAwait(false),
            await RunHttpAsync(payloadBytes, cancellationToken).ConfigureAwait(false)
        };

        return results.ToArray();
    }

    private static async Task<DhmpProtocolComparisonBenchmark> RunTcpAsync(
        int payloadBytes,
        CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Server.DualMode = false;
        listener.Start();

        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        Task<Socket> acceptTask =
            listener.AcceptSocketAsync(cancellationToken).AsTask();

        using var sender =
            new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);

        sender.NoDelay = true;
        sender.SendBufferSize = 16 * 1024 * 1024;

        await sender.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);

        using Socket receiver =
            await acceptTask.ConfigureAwait(false);

        receiver.NoDelay = true;
        receiver.ReceiveBufferSize = 16 * 1024 * 1024;

        byte[] payload =
            GC.AllocateUninitializedArray<byte>(payloadBytes);

        long targetBytes =
            (long)TcpIterations * payloadBytes;

        Task<long> drainTask =
            Task.Run(
                () =>
                {
                    byte[] buffer =
                        GC.AllocateUninitializedArray<byte>(64 * 1024);

                    long received = 0;

                    while (received < targetBytes)
                    {
                        int count =
                            receiver.Receive(
                                buffer,
                                SocketFlags.None);

                        if (count <= 0)
                            break;

                        received += count;
                    }

                    return received;
                },
                cancellationToken);

        long started = Stopwatch.GetTimestamp();

        for (int index = 0; index < TcpIterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int sent = 0;

            while (sent < payload.Length)
            {
                sent += sender.Send(
                    payload,
                    sent,
                    payload.Length - sent,
                    SocketFlags.None);
            }
        }

        long elapsedTicks =
            Stopwatch.GetTimestamp() - started;

        sender.Shutdown(SocketShutdown.Send);

        long receivedBytes =
            await drainTask.ConfigureAwait(false);

        double seconds =
            (double)elapsedTicks / Stopwatch.Frequency;

        double packetRate =
            TcpIterations / seconds;

        listener.Stop();

        return new DhmpProtocolComparisonBenchmark(
            "TCP/IPv6",
            "IPv6 loopback, 1,408-byte application writes",
            payloadBytes,
            packetRate,
            packetRate * payloadBytes / 1_000_000_000d,
            TcpIterations,
            false,
            $"sender bytes={targetBytes:N0}, receiver bytes={receivedBytes:N0}");
    }

    private static async Task<DhmpProtocolComparisonBenchmark> RunUdpAsync(
        int payloadBytes,
        CancellationToken cancellationToken)
    {
        using var receiver =
            new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);

        receiver.DualMode = false;
        receiver.ReceiveBufferSize = 16 * 1024 * 1024;
        receiver.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));

        var endpoint =
            (IPEndPoint)receiver.LocalEndPoint!;

        using var sender =
            new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);

        sender.DualMode = false;
        sender.SendBufferSize = 16 * 1024 * 1024;
        sender.Connect(endpoint);

        byte[] payload =
            GC.AllocateUninitializedArray<byte>(payloadBytes);

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        Task<int> receiveTask =
            Task.Run(
                async () =>
                {
                    byte[] buffer =
                        GC.AllocateUninitializedArray<byte>(payloadBytes + 256);

                    int received = 0;

                    while (received < UdpIterations)
                    {
                        int count =
                            await receiver.ReceiveAsync(
                                buffer,
                                SocketFlags.None,
                                timeout.Token).ConfigureAwait(false);

                        if (count == payloadBytes)
                            received++;
                    }

                    return received;
                },
                timeout.Token);

        long started = Stopwatch.GetTimestamp();

        for (int index = 0; index < UdpIterations; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sender.Send(payload, SocketFlags.None);
        }

        long elapsedTicks =
            Stopwatch.GetTimestamp() - started;

        int receivedPackets =
            await receiveTask.ConfigureAwait(false);

        double seconds =
            (double)elapsedTicks / Stopwatch.Frequency;

        double packetRate =
            UdpIterations / seconds;

        return new DhmpProtocolComparisonBenchmark(
            "UDP/IPv6",
            "IPv6 loopback datagrams",
            payloadBytes,
            packetRate,
            packetRate * payloadBytes / 1_000_000_000d,
            UdpIterations,
            false,
            $"received={receivedPackets:N0}/{UdpIterations:N0}");
    }

    private static async Task<DhmpProtocolComparisonBenchmark> RunHttpAsync(
        int payloadBytes,
        CancellationToken cancellationToken)
    {
        using var handler =
            new SocketsHttpHandler
            {
                UseProxy = false,
                MaxConnectionsPerServer = 1,
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan
            };

        using var client =
            new HttpClient(handler)
            {
                BaseAddress = ResolveLocalBaseAddress(),
                Timeout = TimeSpan.FromSeconds(30)
            };

        byte[] payload =
            GC.AllocateUninitializedArray<byte>(payloadBytes);

        for (int index = 0; index < HttpWarmupIterations; index++)
        {
            using var content = new ByteArrayContent(payload);
            using HttpResponseMessage response =
                await client.PostAsync(
                    "/api/report/http-sink",
                    content,
                    cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
        }

        long started = Stopwatch.GetTimestamp();

        for (int index = 0; index < HttpIterations; index++)
        {
            using var content = new ByteArrayContent(payload);
            using HttpResponseMessage response =
                await client.PostAsync(
                    "/api/report/http-sink",
                    content,
                    cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
        }

        long elapsedTicks =
            Stopwatch.GetTimestamp() - started;

        double seconds =
            (double)elapsedTicks / Stopwatch.Frequency;

        double requestRate =
            HttpIterations / seconds;

        return new DhmpProtocolComparisonBenchmark(
            "HTTP/1.1 + TCP/IPv6",
            "Local ASP.NET POST + 204 response",
            payloadBytes,
            requestRate,
            requestRate * payloadBytes / 1_000_000_000d,
            HttpIterations,
            false,
            "Persistent HttpClient connection; application payload excludes HTTP/TCP/IP headers.");
    }

    private static Uri ResolveLocalBaseAddress()
    {
        string configured =
            Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            ?? "http://+:8080";

        string first =
            configured
                .Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .FirstOrDefault()
            ?? "http://+:8080";

        first =
            first
                .Replace("://+:", "://127.0.0.1:", StringComparison.Ordinal)
                .Replace("://*:", "://127.0.0.1:", StringComparison.Ordinal);

        return new Uri(first, UriKind.Absolute);
    }
}
