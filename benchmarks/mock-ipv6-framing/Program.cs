using System.Buffers.Binary;
using System.Diagnostics;

const int Msg=32, IpHeader=40, NextHeader=253;
int messages=args.Length>0?int.Parse(args[0]):100_000_000;
int[] batches=[1,4,8,16,32,44];
Console.WriteLine($"DHMP_MOCK_IPV6_V1 messages={messages} msg={Msg} ipv6_header={IpHeader} next_header={NextHeader}");
Console.WriteLine("In-memory IPv6 framing/parsing ceiling; no socket/kernel/NIC claim.");
foreach(int b in batches){Run(Math.Min(messages,2_000_000),b); var r=Run(messages,b); Print(r);}

static R Run(int count,int batch){
 byte[] frame=new byte[IpHeader+batch*Msg]; long guard=0,packets=0; int done=0;
 long start=Stopwatch.GetTimestamp();
 while(done<count){
  int n=Math.Min(batch,count-done), plen=n*Msg;
  var h=frame.AsSpan(0,IpHeader); h.Clear();
  h[0]=0x60;
  BinaryPrimitives.WriteUInt16BigEndian(h.Slice(4,2),(ushort)plen);
  h[6]=NextHeader; h[7]=64;
  // ::1 -> ::1
  h[23]=1; h[39]=1;
  var p=frame.AsSpan(IpHeader,plen);
  for(int j=0;j<n;j++) BinaryPrimitives.WriteInt32LittleEndian(p.Slice(j*Msg,4),done+j);
  // Minimal fixed IPv6 receive contract: version, payload length, next header, complete DHMP messages.
  if((h[0]>>4)!=6||h[6]!=NextHeader||BinaryPrimitives.ReadUInt16BigEndian(h.Slice(4,2))!=plen||plen%Msg!=0)
    throw new Exception("IPv6 validation failed");
  guard+=BinaryPrimitives.ReadInt32LittleEndian(p.Slice((n-1)*Msg,4));
  done+=n; packets++;
 }
 long ticks=Stopwatch.GetTimestamp()-start; GC.KeepAlive(guard); return new(count,packets,batch,ticks,guard);
}
static void Print(R r){double s=(double)r.Ticks/Stopwatch.Frequency; Console.WriteLine($"MOCK_IPV6_CURRENT_V1 batch={r.Batch} ip_payload={r.Batch*Msg} wire_bytes={IpHeader+r.Batch*Msg} messages={r.Messages} packets={r.Packets} wall_s={s:F6} logical_mps={r.Messages/s/1e6:F3} logical_GBps={r.Messages*Msg/s/1e9:F3} ns_msg={s*1e9/r.Messages:F3} ns_packet={s*1e9/r.Packets:F3} guard={r.Guard}");}
readonly record struct R(long Messages,long Packets,int Batch,long Ticks,long Guard);
