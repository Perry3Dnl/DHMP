using Dhmp.Server;
using System.Diagnostics;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential,Pack=1)] readonly struct M32 { public readonly ulong A,B,C,D; }
static class Program {
 static ulong sink;
 static void Main(string[] args){int packages=int.Parse(args[0]);int passes=int.Parse(args[1]);var data=new byte[packages*32];for(int i=0;i<data.Length;i++)data[i]=(byte)i;
   int[] pattern=[4093,8191,12287,16381,32749,65521];var sp=new DHMPFixedStreamProcessor(32);long spans=0,cross=0;ulong g=0;long t=Stopwatch.GetTimestamp();
   for(int pass=0;pass<passes;pass++){int pos=0,k=0;while(pos<data.Length){int n=Math.Min(pattern[k++%pattern.Length],data.Length-pos);sp.Process(data.AsSpan(pos,n),
     s=>{DHMPModelBoundary.Consume<M32>(s,32,m=>{g^=(ulong)m.Length;g^=m[0].A;});cross++;},
     s=>{DHMPModelBoundary.Consume<M32>(s,32,m=>g^=(ulong)m.Length);spans++;});pos+=n;}}
   long e=Stopwatch.GetTimestamp();sink=g;long msgs=(long)packages*passes;double sec=(double)(e-t)/Stopwatch.Frequency;
   Console.WriteLine($"benchmark=production-receive-boundary messages={msgs} bytes={msgs*32} publications={spans+cross} crossing_packages={cross} wall_s={sec:F6} logical_GBps={msgs*32.0/sec/1e9:F3} logical_mps={msgs/sec/1e6:F3} ns_msg={sec*1e9/msgs:F3} guard={g}");
 }
}