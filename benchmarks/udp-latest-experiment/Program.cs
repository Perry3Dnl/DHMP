using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

const int Size = 32;
int messages = args.Length > 0 ? int.Parse(args[0]) : 5_000_000;
messages = Math.Max(messages, 100_000);

Console.WriteLine($"DHMP_UDP_EXPERIMENT_V2 messages={messages} size={Size} datagram_messages=1 loopback=true semantics=latest/fire-and-forget");
Console.WriteLine("V2 uses concurrent sender/receiver, a start gate, exact receive accounting, and a bounded post-send drain.");
await RunPair(Math.Min(messages, 200_000), warmup:true);
var raw = await RunCase("raw-udp", messages, dhmp:false);
var dhmp = await RunCase("dhmp-udp-latest", messages, dhmp:true);
Print(raw);
Print(dhmp);

static async Task RunPair(int count, bool warmup)
{
    await RunCase("warmup-raw", count, false, print:false);
    await RunCase("warmup-dhmp", count, true, print:false);
}

static async Task<Result> RunCase(string id, int count, bool dhmp, bool print=true)
{
    using var rx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    using var tx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    rx.ReceiveBufferSize = 16 * 1024 * 1024;
    tx.SendBufferSize = 16 * 1024 * 1024;
    rx.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    tx.Connect((IPEndPoint)rx.LocalEndPoint!);

    using var startGate = new ManualResetEventSlim(false);
    using var senderDone = new ManualResetEventSlim(false);
    long received = 0, guard = 0;
    long firstReceiveTick = 0, lastReceiveTick = 0;
    long sendStart = 0, sendEnd = 0;

    var receiver = Task.Factory.StartNew(() =>
    {
        byte[] buffer = new byte[Size];
        startGate.Wait();
        var idle = Stopwatch.StartNew();
        while (true)
        {
            if (rx.Poll(1000, SelectMode.SelectRead))
            {
                int got = rx.Receive(buffer);
                if (got != Size) throw new InvalidOperationException($"unexpected UDP datagram size {got}");
                long now = Stopwatch.GetTimestamp();
                if (Interlocked.Read(ref firstReceiveTick) == 0) Interlocked.CompareExchange(ref firstReceiveTick, now, 0);
                Interlocked.Exchange(ref lastReceiveTick, now);
                long seq = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0,4));
                guard = dhmp ? seq : (guard ^ seq);
                received++;
                idle.Restart();
            }
            else if (senderDone.IsSet && idle.ElapsedMilliseconds >= 50)
            {
                break;
            }
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    var sender = Task.Factory.StartNew(() =>
    {
        byte[] packet = new byte[Size];
        startGate.Wait();
        sendStart = Stopwatch.GetTimestamp();
        for (int i=0;i<count;i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0,4), i);
            int sent = tx.Send(packet);
            if (sent != Size) throw new InvalidOperationException($"partial UDP datagram send {sent}");
        }
        sendEnd = Stopwatch.GetTimestamp();
        senderDone.Set();
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    startGate.Set();
    await Task.WhenAll(sender, receiver);
    GC.KeepAlive(guard);

    long wallStart = Math.Min(sendStart, firstReceiveTick == 0 ? sendStart : firstReceiveTick);
    long wallEnd = Math.Max(sendEnd, lastReceiveTick == 0 ? sendEnd : lastReceiveTick);
    return new Result(id, count, received, sendEnd-sendStart, wallEnd-wallStart, guard);
}

static void Print(Result r)
{
    double sendS=(double)r.SendTicks/Stopwatch.Frequency;
    double wallS=(double)r.WallTicks/Stopwatch.Frequency;
    double offeredMps=r.Offered/sendS/1e6;
    double recvMps=r.Received/wallS/1e6;
    double recvGBps=r.Received*Size/wallS/1e9;
    double loss=100.0*(r.Offered-r.Received)/r.Offered;
    Console.WriteLine($"UDP_CURRENT_V2 id={r.Id} offered={r.Offered} received={r.Received} loss_pct={loss:F6} send_s={sendS:F6} wall_s={wallS:F6} offered_mps={offeredMps:F3} received_mps={recvMps:F3} received_GBps={recvGBps:F3} guard={r.Guard}");
}
readonly record struct Result(string Id,long Offered,long Received,long SendTicks,long WallTicks,long Guard);
