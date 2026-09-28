using System.Buffers.Binary;
using System.Diagnostics;

const int Msg=32, IpHeader=20, Proto=253;
int messages=args.Length>0?int.Parse(args[0]):100_000_000;
int[] batches=[1,4,8,16,32,44];
Console.WriteLine($"DHMP_MOCK_IPV4_V1 messages={messages} msg={Msg} ipv4_header={IpHeader} protocol={Proto}");
Console.WriteLine("In-memory IPv4 framing/parsing ceiling; no socket/kernel/NIC claim.");
foreach(int b in batches){ Run(Math.Min(messages,2_000_000),b); var r=Run(messages,b); Print(r); }

static R Run(int count,int batch){
 byte[] frame=new byte[IpHeader+batch*Msg]; long guard=0,packets=0; int done=0,ident=0;
 long start=Stopwatch.GetTimestamp();
 while(done<count){
  int n=Math.Min(batch,count-done), plen=n*Msg, total=IpHeader+plen;
  var h=frame.AsSpan(0,IpHeader); h.Clear();
  h[0]=0x45; BinaryPrimitives.WriteUInt16BigEndian(h.Slice(2,2),(ushort)total);
  BinaryPrimitives.WriteUInt16BigEndian(h.Slice(4,2),(ushort)ident++);
  BinaryPrimitives.WriteUInt16BigEndian(h.Slice(6,2),0x4000); h[8]=64; h[9]=Proto;
  BinaryPrimitives.WriteUInt32BigEndian(h.Slice(12,4),0x7f000001); BinaryPrimitives.WriteUInt32BigEndian(h.Slice(16,4),0x7f000001);
  BinaryPrimitives.WriteUInt16BigEndian(h.Slice(10,2),Checksum(h));
  var p=frame.AsSpan(IpHeader,plen);
  for(int j=0;j<n;j++) BinaryPrimitives.WriteInt32LittleEndian(p.Slice(j*Msg,4),done+j);
  // Receiver validates minimal fixed IPv4 contract, checksum, then Latest-selects newest complete DHMP message.
  if((h[0]>>4)!=4||(h[0]&15)!=5||h[9]!=Proto||BinaryPrimitives.ReadUInt16BigEndian(h.Slice(2,2))!=total||Checksum(h)!=0) throw new Exception("IPv4 validation failed");
  guard+=BinaryPrimitives.ReadInt32LittleEndian(p.Slice((n-1)*Msg,4));
  done+=n; packets++;
 }
 long ticks=Stopwatch.GetTimestamp()-start; GC.KeepAlive(guard); return new(count,packets,batch,ticks,guard);
}
static ushort Checksum(ReadOnlySpan<byte> h){uint s=0; for(int i=0;i<h.Length;i+=2)s+=BinaryPrimitives.ReadUInt16BigEndian(h.Slice(i,2)); while((s>>16)!=0)s=(s&0xffff)+(s>>16); return (ushort)~s;}
static void Print(R r){double s=(double)r.Ticks/Stopwatch.Frequency; Console.WriteLine($"MOCK_IPV4_CURRENT_V1 batch={r.Batch} ip_payload={r.Batch*Msg} wire_bytes={IpHeader+r.Batch*Msg} messages={r.Messages} packets={r.Packets} wall_s={s:F6} logical_mps={r.Messages/s/1e6:F3} logical_GBps={r.Messages*Msg/s/1e9:F3} ns_msg={s*1e9/r.Messages:F3} ns_packet={s*1e9/r.Packets:F3} guard={r.Guard}");}
readonly record struct R(long Messages,long Packets,int Batch,long Ticks,long Guard);
