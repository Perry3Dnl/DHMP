using System.Buffers.Binary;
using System.Diagnostics;

const int Size=32;
int messages=args.Length>0?int.Parse(args[0]):100_000_000;
int[] batches=[1,4,8,16,32,44,128,512];
Console.WriteLine($"DHMP_MOCK_IP_V1 messages={messages} size={Size} transport=memory packet-boundary");
Console.WriteLine("Processor ceiling only: no socket/kernel/NIC/IP throughput claim.");
foreach(int b in batches){Run(Math.Min(messages,2_000_000),b,false); var r=Run(messages,b,true); Print(r);}

static R Run(int count,int batch,bool measured){
 byte[] packet=new byte[batch*Size]; long guard=0,packets=0; int processed=0;
 long start=Stopwatch.GetTimestamp();
 while(processed<count){
   int n=Math.Min(batch,count-processed);
   // Mock sender writes complete fixed messages into one IP-like payload.
   for(int j=0;j<n;j++) BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(j*Size,4),processed+j);
   // Mock IP delivers one contiguous packet boundary. DHMP Latest consumes newest complete message.
   var payload=packet.AsSpan(0,n*Size);
   guard += BinaryPrimitives.ReadInt32LittleEndian(payload.Slice((n-1)*Size,4));
   processed+=n; packets++;
 }
 long ticks=Stopwatch.GetTimestamp()-start; GC.KeepAlive(guard);
 return new R(count,packets,batch,ticks,guard);
}
static void Print(R r){
 double s=(double)r.Ticks/Stopwatch.Frequency;
 Console.WriteLine($"MOCK_IP_CURRENT_V1 batch={r.Batch} payload_bytes={r.Batch*Size} messages={r.Messages} packets={r.Packets} wall_s={s:F6} logical_mps={r.Messages/s/1e6:F3} logical_GBps={r.Messages*Size/s/1e9:F3} ns_msg={s*1e9/r.Messages:F3} ns_packet={s*1e9/r.Packets:F3} guard={r.Guard}");
}
readonly record struct R(long Messages,long Packets,int Batch,long Ticks,long Guard);
