using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DHMP.Protocol;
using DHMP.RawIpv6;

string mode = args.Single();
var local = IPAddress.Parse(mode == "loopback" ? "fd00::2" : "fd01::2");
var remote = IPAddress.Parse(mode == "loopback" ? "fd00::1" : "fd01::1");
if (mode == "loopback")
{
    await CheckContracts();
    Measure();
}
else if (mode == "mtu") await CheckMtu();
else if (mode == "pressure") await CheckPressure();
else throw new ArgumentException("Unknown check mode.");

DhmpRawIpv6Options Options(int buffer = 16 * 1024 * 1024) => new(local, remote, 1408,
    socketBufferBytes: buffer, enableExperimentalProtocolNumbers: true, allowUnprotectedPayloads: true);

async Task CheckContracts()
{
    using var receiver = DhmpLinuxRawIpv6Socket.Open(253);
    receiver.ReceiveBufferSize = 16 * 1024 * 1024;
    receiver.Bind(new IPEndPoint(remote, 0));
    using var sender = new DhmpRawIpv6PacketSender(Options());
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    byte[] buffer = new byte[2048];
    using (IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(2048))
    {
        Memory<byte> record = owner.Memory.Slice(17, 1408);
        record.Span.Fill(0x37);
        await sender.SendPacketAsync(record, lifetime.Token);
        int length = await receiver.ReceiveAsync(buffer, SocketFlags.None, lifetime.Token);
        Require(length == record.Length && buffer.AsSpan(0, length).SequenceEqual(record.Span), "Sliced pooled payload changed.");
    }
    await Expect<DhmpProtocolException>(() => sender.SendPacketAsync(ReadOnlyMemory<byte>.Empty).AsTask());
    await Expect<DhmpProtocolException>(() => sender.SendPacketAsync(new byte[1409]).AsTask());
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        var error = await Expect<OperationCanceledException>(() => sender.SendPacketAsync(new byte[16], cancelled.Token).AsTask());
        Require(error.CancellationToken == cancelled.Token, "Cancellation token changed.");
    }
    // Rejected/cancelled sends must not emit packets. The next received record is the marker.
    byte[] marker = Enumerable.Repeat((byte)0xa7, 16).ToArray();
    await sender.SendPacketAsync(marker, lifetime.Token);
    int markerLength = await receiver.ReceiveAsync(buffer, SocketFlags.None, lifetime.Token);
    Require(markerLength == 16 && buffer.AsSpan(0, 16).SequenceEqual(marker), "Rejected send emitted a packet.");

    using (var dynamic = DhmpRawIpv6PacketSender.ForDynamicPath(Options()))
    {
        var target = (IDhmpPathBudgetTarget)dynamic;
        Require(dynamic.CurrentMaximumPayloadBytes == 1240, "Wrong minimum path budget.");
        await Expect<DhmpProtocolException>(() => dynamic.SendPacketAsync(new byte[1408]).AsTask());
        target.ApplyConfirmedPathBudget(new DhmpIpv6PathBudget(1448));
        await dynamic.SendPacketAsync(new byte[1408], lifetime.Token);
        Require(await receiver.ReceiveAsync(buffer, SocketFlags.None, lifetime.Token) == 1408, "Raised budget failed.");
        target.FallBackToMinimumPathBudget();
        await Expect<DhmpProtocolException>(() => dynamic.SendPacketAsync(new byte[1408]).AsTask());
    }

    const int count = 256;
    Task receive = Task.Run(async () => {
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int length = await receiver.ReceiveAsync(buffer, SocketFlags.None, lifetime.Token);
            Require(length == 16, "Concurrent record length changed.");
            int id = BitConverter.ToInt32(buffer, 0);
            Require(id >= 0 && id < count && seen.Add(id), "Concurrent record missing/duplicated.");
            Require(buffer.AsSpan(4, 12).SequenceEqual(new byte[12]), "Concurrent payload changed.");
        }
    });
    await Task.WhenAll(Enumerable.Range(0, count).Select(async i => {
        byte[] record = new byte[16];
        BitConverter.TryWriteBytes(record, i);
        await sender.SendPacketAsync(record, lifetime.Token);
    }));
    await receive;
    sender.Dispose();
    sender.Dispose();
    await Expect<ObjectDisposedException>(() => sender.SendPacketAsync(marker).AsTask());

    int descriptorsBefore = Directory.GetFiles("/proc/self/fd").Length;
    for (int i = 0; i < 32; i++)
    {
        try
        {
            using var noRoute = new DhmpRawIpv6PacketSender(new DhmpRawIpv6Options(local,
                IPAddress.Parse("fd99::1"), 1408, enableExperimentalProtocolNumbers: true));
            throw new InvalidOperationException("Missing route unexpectedly connected.");
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.NetworkUnreachable or SocketError.HostUnreachable) { }
    }
    Require(Directory.GetFiles("/proc/self/fd").Length <= descriptorsBefore + 2, "Failed Connect leaked descriptors.");
    Console.WriteLine("CONNECTED_CONTRACTS=passed pooled slices, cancellation-before-send, rejected-packet silence, dynamic budgets, concurrent packets, idempotent disposal, missing-route cleanup");
}

async Task CheckMtu()
{
    using (var baseline = new LegacyEndpointSender(Options()))
    {
        await Expect<DhmpPathMtuException>(() => baseline.SendPacketAsync(new byte[1408]).AsTask());
        Console.WriteLine("CONNECTED_PMTU_BASELINE=old endpoint sender rejected the same oversized packet");
    }
    using var sender = new DhmpRawIpv6PacketSender(Options());
    var error = await Expect<DhmpPathMtuException>(() => sender.SendPacketAsync(new byte[1408]).AsTask());
    Require(error.AttemptedPayloadBytes == 1408 && error.PayloadCeilingBytes == 1408 &&
        error.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.MessageSize, "PMTU exception changed.");
    await sender.SendPacketAsync(new byte[1200]);
    Console.WriteLine("CONNECTED_PMTU=passed actual 1280-byte veth path, translated MessageSize, smaller send accepted");
}

async Task CheckPressure()
{
    byte[] record = new byte[1200];
    using (var sender = new DhmpRawIpv6PacketSender(Options(4096)))
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
    {
        ValueTask pending = FindPending(sender, record, cancellation.Token);
        cancellation.Cancel();
        var error = await Expect<OperationCanceledException>(() => pending.AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Require(error.CancellationToken == cancellation.Token, "Pending cancellation token changed.");
    }
    using (var sender = new DhmpRawIpv6PacketSender(Options(4096)))
    {
        ValueTask pending = FindPending(sender, record, CancellationToken.None);
        sender.Dispose();
        try
        {
            await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            throw new InvalidOperationException("Disposed pending send unexpectedly succeeded.");
        }
        catch (ObjectDisposedException) { }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.OperationAborted or SocketError.Interrupted) { }
    }
    Console.WriteLine("CONNECTED_PRESSURE=passed observed pending raw sends, cancellation and concurrent disposal");
}

static ValueTask FindPending(DhmpRawIpv6PacketSender sender, byte[] record, CancellationToken token)
{
    for (int i = 0; i < 4096; i++)
    {
        ValueTask pending = sender.SendPacketAsync(record, token);
        if (!pending.IsCompleted) return pending;
        pending.GetAwaiter().GetResult();
    }
    throw new InvalidOperationException("Lab did not create an outstanding send; pressure coverage is required.");
}

void Measure()
{
    const int count = 32768;
    var rows = new List<object>();
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
        using var current = new DhmpRawIpv6PacketSender(Options());
        using var legacy = new LegacyEndpointSender(Options());
        Func<ValueTask>[] sends = [() => legacy.SendPacketAsync(record), () => current.SendPacketAsync(record)];
        foreach (var send in sends)
        {
            long before = Interlocked.Read(ref received);
            for (int i = 0; i < count; i++) Complete(send());
            WaitFor(before + count);
        }
        var samples = new List<object>[] {new(), new()};
        for (int round = 0; round < 7; round++)
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
        rows.Add(new {bytes, endpoint = samples[0], connected = samples[1]});

        void WaitFor(long target)
        {
            var wait = Stopwatch.StartNew();
            while (Interlocked.Read(ref received) < target && wait.ElapsedMilliseconds < 3000) Thread.Yield();
            Require(Interlocked.Read(ref received) == target, "Measured receiver count mismatch.");
        }
    }
    Console.WriteLine("CONNECTED_SEND_CHECK=" + JsonSerializer.Serialize(new {scope = "Linux raw IPv6 loopback, complete old/new DHMP sender wrappers; kernel acceptance timing with receiver count/content verification, not physical-NIC throughput", rows}));
}

static void Complete(ValueTask pending)
{
    if (pending.IsCompleted) pending.GetAwaiter().GetResult();
    else pending.AsTask().GetAwaiter().GetResult();
}

static async Task<T> Expect<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T error) { return error; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
