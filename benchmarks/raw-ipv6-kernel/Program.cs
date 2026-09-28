using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

const int Msg=32, Header=40, NextHeader=253;
int messages=args.Length>0?int.Parse(args[0]):5_000_000;
int batch=args.Length>1?int.Parse(args[1]):44;
int payloadBytes=batch*Msg;
if(payloadBytes+Header>1500) throw new ArgumentOutOfRangeException(nameof(batch),"IPv6 packet exceeds 1500-byte test MTU.");

using var rx=new Socket(AddressFamily.InterNetworkV6,SocketType.Raw,(ProtocolType)NextHeader);
using var tx=new Socket(AddressFamily.InterNetworkV6,SocketType.Raw,(ProtocolType)NextHeader);
var ep=new IPEndPoint(IPAddress.IPv6Loopback,0);
rx.Bind(ep);
rx.ReceiveBufferSize=16*1024*1024; tx.SendBufferSize=16*1024*1024;
rx.ReceiveTimeout=2000;
byte[] send=new byte[payloadBytes], recv=new byte[payloadBytes+Header+256];
long offered=0,received=0,guard=0;
using var gate=new ManualResetEventSlim(false);
var receiver=Task.Run(()=>{
 gate.Wait();
 try {
  while(Volatile.Read(ref offered)<messages || received<messages){
   int n;
   try { n=rx.Receive(recv); } catch(SocketException e) when(e.SocketErrorCode==SocketError.TimedOut){ if(Volatile.Read(ref offered)>=messages) break; else continue; }
   var s=recv.AsSpan(0,n);
   // Linux raw IPv6 sockets may deliver payload only; tolerate an IPv6 header if present.
   if(n>=Header && (s[0]>>4)==6){ if(s[6]!=NextHeader) continue; s=s.Slice(Header); }
   int complete=s.Length/Msg; if(complete==0) continue;
   guard+=BinaryPrimitives.ReadInt32LittleEndian(s.Slice((complete-1)*Msg,4));
   received+=complete;
  }
 } catch(Exception e){Console.Error.WriteLine($"RX_ERROR {e.GetType().Name}: {e.Message}"); throw;}
});
gate.Set();
var sw=Stopwatch.StartNew();
for(int seq=0;seq<messages;){
 int n=Math.Min(batch,messages-seq);
 var p=send.AsSpan(0,n*Msg);
 for(int j=0;j<n;j++) BinaryPrimitives.WriteInt32LittleEndian(p.Slice(j*Msg,4),seq+j);
 tx.SendTo(p,SocketFlags.None,ep);
 seq+=n; Volatile.Write(ref offered,seq);
}
receiver.Wait(); sw.Stop();
double sec=sw.Elapsed.TotalSeconds, loss=100.0*(messages-received)/messages;
Console.WriteLine($"RAW_IPV6_CURRENT_V1 batch={batch} offered={messages} received={received} loss_pct={loss:F6} wall_s={sec:F6} offered_mps={messages/sec/1e6:F3} received_mps={received/sec/1e6:F3} received_GBps={received*Msg/sec/1e9:F3} ns_received={(received>0?sec*1e9/received:0):F3} guard={guard}");
