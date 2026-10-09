using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.RawIpv6;

internal static class RawComparison
{
    private static readonly IPAddress local = IPAddress.Parse("fd00::2");
    private static readonly IPAddress remote = IPAddress.Parse("fd00::1");
public static void Measure()
{
    const int count = 32768;
    var rows = new List<object>();
    foreach (DhmpRatePolicy ratePolicy in Enum.GetValues<DhmpRatePolicy>())
    foreach (int bytes in new[] {16, 1408})
    {
        byte[] record = Enumerable.Repeat((byte)0x5a, bytes).ToArray();
        using var receiver = DhmpLinuxRawIpv6Socket.Open(253);
        receiver.ReceiveBufferSize = 16 * 1024 * 1024;
        receiver.ReceiveTimeout = 200;
        receiver.Bind(new IPEndPoint(remote, 0));
        long received = 0, malformed = 0;
        using var stop = new CancellationTokenSource();
        Task drain = Task.Factory.StartNew(() => {
            byte[] buffer = new byte[2048];
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    int length = receiver.Receive(buffer);
                    if (length != bytes || !buffer.AsSpan(0, length).SequenceEqual(record)) Interlocked.Increment(ref malformed);
                    Interlocked.Increment(ref received);
                }
                catch (SocketException error) when (error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock) { }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        using var current = new DhmpRawIpv6PacketSender(new DhmpRawIpv6Options(local, remote, 1408, socketBufferBytes: 16 * 1024 * 1024,
            enableExperimentalProtocolNumbers: true, allowUnprotectedPayloads: true));
        var wire = new DhmpWireContract(bytes);
        var policy = new DhmpSendPolicy(long.MaxValue, 1408, ratePolicy);
        var beforeClient = new BeforeClient(current, wire, policy);
        var afterClient = new DhmpClient(current, wire, policy);
        Func<ValueTask>[] sends = [() => beforeClient.SendAsync(record), () => afterClient.SendAsync(record)];
        foreach (var send in sends)
        {
            long before = Interlocked.Read(ref received);
            for (int i = 0; i < count; i++) Complete(send());
            WaitFor(before + count);
        }
        var samples = new List<object>[] {new(), new()};
        for (int round = 0; round < 5; round++)
        for (int step = 0; step < 2; step++)
        {
            int index = round % 2 == 0 ? step : 1 - step;
            long before = Interlocked.Read(ref received);
            long immediate = 0;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                ValueTask pending = sends[index]();
                if (pending.IsCompletedSuccessfully) immediate++;
                Complete(pending);
            }
            clock.Stop();
            WaitFor(before + count);
            samples[index].Add(new { nanosecondsPerAcceptedPacket = clock.Elapsed.TotalNanoseconds / count,
                acceptedPackets = count, receivedPackets = Interlocked.Read(ref received) - before,
                immediateCompletions = immediate, pendingAtObservation = count - immediate });
        }
        stop.Cancel();
        drain.GetAwaiter().GetResult();
        Require(malformed == 0, "Measured payload contents changed.");
        rows.Add(new {bytes, ratePolicy = ratePolicy.ToString(), before = samples[0], after = samples[1]});

        void WaitFor(long target)
        {
            var wait = Stopwatch.StartNew();
            while (Interlocked.Read(ref received) < target && wait.ElapsedMilliseconds < 3000) Thread.Yield();
            Require(Interlocked.Read(ref received) == target, "Measured receiver count mismatch.");
        }
    }
    Console.WriteLine("RAW_CLIENT_SEND_CHECK=" + JsonSerializer.Serialize(new {scope = "Linux raw IPv6 loopback, before/after clients over the same production connected raw sender; kernel acceptance timing with receiver count/content verification, not physical-NIC throughput", rows}));
}

static void Complete(ValueTask pending)
{
    if (pending.IsCompleted) pending.GetAwaiter().GetResult();
    else pending.AsTask().GetAwaiter().GetResult();
}


static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
}
