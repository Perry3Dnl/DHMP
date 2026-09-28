using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

const int Size=32;
int messages=args.Length>0?int.Parse(args[0]):5_000_000;
messages=Math.Max(messages,100_000);
int[] batches=[1,4,8,16,32,44]; // 44*32=1408 B payload: conservative MTU-friendly IPv4 UDP payload.

Console.WriteLine($"DHMP_UDP_EXPERIMENT_V3 messages={messages} size={Size} loopback=true semantics=latest/fire-and-forget batches={string.Join(',',batches)}");
Console.WriteLine("Complete fixed 32-byte messages are batched; no DHMP message is fragmented.");
foreach(int b in batches) await RunCase("warmup",Math.Min(messages,200_000),b,true,false);
foreach(int b in batches) {
    var raw=await RunCase("raw-udp",messages,b,false,true);
    var dhmp=await RunCase("dhmp-udp-latest",messages,b,true,true);
    Print(raw); Print(dhmp);
}

static async Task<Result> RunCase(string id,int count,int batch,bool dhmp,bool measured)
{
    using var rx=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
    using var tx=new Socket(AddressFamily.InterNetwork,SocketType.Dgram,ProtocolType.Udp);
    rx.ReceiveBufferSize=16*1024*1024; tx.SendBufferSize=16*1024*1024;
    rx.Bind(new IPEndPoint(IPAddress.Loopback,0)); tx.Connect((IPEndPoint)rx.LocalEndPoint!);
    using var gate=new ManualResetEventSlim(false); using var done=new ManualResetEventSlim(false);
    long received=0, datagrams=0, guard=0, first=0,last=0,ss=0,se=0;
    var receiver=Task.Factory.StartNew(()=>{
        byte[] buf=new byte[batch*Size]; gate.Wait(); var idle=Stopwatch.StartNew();
        while(true) {
            if(rx.Poll(1000,SelectMode.SelectRead)) {
                int got=rx.Receive(buf);
                if(got<=0 || got%Size!=0 || got>buf.Length) throw new InvalidOperationException($"invalid datagram bytes={got}");
                long now=Stopwatch.GetTimestamp(); if(first==0) first=now; last=now; datagrams++;
                int records=got/Size; received+=records;
                // Same payload touch in both paths; DHMP Latest selects newest complete message in this datagram.
                if(dhmp) guard=BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan((records-1)*Size,4));
                else guard^=BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan((records-1)*Size,4));
                idle.Restart();
            } else if(done.IsSet && idle.ElapsedMilliseconds>=50) break;
        }
    },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    var sender=Task.Factory.StartNew(()=>{
        byte[] packet=new byte[batch*Size]; gate.Wait(); ss=Stopwatch.GetTimestamp();
        for(int baseMsg=0;baseMsg<count;) {
            int records=Math.Min(batch,count-baseMsg); int bytes=records*Size;
            for(int j=0;j<records;j++) BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(j*Size,4),baseMsg+j);
            int sent=tx.Send(packet,0,bytes,SocketFlags.None);
            if(sent!=bytes) throw new InvalidOperationException($"partial UDP datagram {sent}/{bytes}");
            baseMsg+=records;
        }
        se=Stopwatch.GetTimestamp(); done.Set();
    },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    gate.Set(); await Task.WhenAll(sender,receiver); GC.KeepAlive(guard);
    long ws=Math.Min(ss,first==0?ss:first), we=Math.Max(se,last==0?se:last);
    return new Result(id,count,received,datagrams,batch,se-ss,we-ws,guard);
}
static void Print(Result r) {
    double sendS=(double)r.SendTicks/Stopwatch.Frequency, wallS=(double)r.WallTicks/Stopwatch.Frequency;
    double loss=100.0*(r.Offered-r.Received)/r.Offered;
    Console.WriteLine($"UDP_CURRENT_V3 id={r.Id} batch={r.Batch} datagram_bytes={r.Batch*Size} offered={r.Offered} received={r.Received} datagrams={r.Datagrams} loss_pct={loss:F6} send_s={sendS:F6} wall_s={wallS:F6} offered_mps={r.Offered/sendS/1e6:F3} received_mps={r.Received/wallS/1e6:F3} received_GBps={r.Received*Size/wallS/1e9:F3} ns_received={wallS*1e9/r.Received:F3} guard={r.Guard}");
}
readonly record struct Result(string Id,long Offered,long Received,long Datagrams,int Batch,long SendTicks,long WallTicks,long Guard);
