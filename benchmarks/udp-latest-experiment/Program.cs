using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

const int Size = 32;
const int DatagramMessages = 1; // deliberately one logical DHMP message per UDP datagram
int messages = args.Length > 0 ? int.Parse(args[0]) : 5_000_000;
messages = Math.Max(messages, 100_000);

Console.WriteLine($"DHMP_UDP_EXPERIMENT_V1 messages={messages} size={Size} datagram_messages={DatagramMessages} loopback=true semantics=latest/fire-and-forget");
Run("raw-udp", messages, dhmp:false, warmup:true);
Run("dhmp-udp-latest", messages, dhmp:true, warmup:true);
var raw = Run("raw-udp", messages, dhmp:false, warmup:false);
var dhmp = Run("dhmp-udp-latest", messages, dhmp:true, warmup:false);
Print("raw-udp", raw);
Print("dhmp-udp-latest", dhmp);

static (long ticks,long received,long offered) Run(string id,int count,bool dhmp,bool warmup)
{
    int n = warmup ? Math.Min(count, 200_000) : count;
    using var rx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    using var tx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    rx.ReceiveBufferSize = 16 * 1024 * 1024;
    tx.SendBufferSize = 16 * 1024 * 1024;
    rx.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    tx.Connect((IPEndPoint)rx.LocalEndPoint!);
    rx.Blocking = false;
    tx.Blocking = false;

    byte[] packet = new byte[Size];
    BitConverter.TryWriteBytes(packet.AsSpan(0,4), 1);
    long received = 0;
    long latest = 0;
    long start = Stopwatch.GetTimestamp();
    int sent = 0;
    Span<byte> buf = stackalloc byte[Size];

    // Nonblocking single-thread pump avoids sender deadlock when the UDP socket buffer fills.
    while (sent < n || received < sent)
    {
        bool progress = false;
        while (sent < n)
        {
            BitConverter.TryWriteBytes(packet.AsSpan(0,4), sent);
            try {
                if (tx.Send(packet) != Size) throw new InvalidOperationException("partial UDP datagram");
                sent++; progress = true;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.NoBufferSpaceAvailable) { break; }
        }

        while (true)
        {
            try {
                int got = rx.Receive(buf);
                if (got != Size) throw new InvalidOperationException($"unexpected UDP datagram size {got}");
                received++; progress = true;
                if (dhmp) latest = BitConverter.ToInt32(buf[..4]); // minimal Latest observation; no ordering/retransmit
                else latest ^= BitConverter.ToInt32(buf[..4]);    // equal minimal payload observation
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { break; }
        }

        // UDP is intentionally fire-and-forget. Once all sends were accepted, allow a bounded drain;
        // don't turn this experiment into a reliable protocol by waiting forever for lost datagrams.
        if (sent == n && received < sent && !progress)
        {
            for (int spin=0; spin<1000 && received<sent; spin++)
            {
                try { int got=rx.Receive(buf); if(got==Size){received++; latest ^= BitConverter.ToInt32(buf[..4]);} }
                catch(SocketException e) when(e.SocketErrorCode==SocketError.WouldBlock){Thread.SpinWait(64);}
            }
            break;
        }
        if (!progress) Thread.SpinWait(32);
    }
    long end=Stopwatch.GetTimestamp();
    GC.KeepAlive(latest);
    return (end-start,received,n);
}

static void Print(string id,(long ticks,long received,long offered) r)
{
    double s=(double)r.ticks/Stopwatch.Frequency;
    double gbps=r.received*Size/s/1e9;
    double mps=r.received/s/1e6;
    double loss=100.0*(r.offered-r.received)/r.offered;
    Console.WriteLine($"UDP_CURRENT id={id} offered={r.offered} received={r.received} loss_pct={loss:F6} wall_s={s:F6} GBps={gbps:F3} mps={mps:F3} ns_received={s*1e9/r.received:F3}");
}
