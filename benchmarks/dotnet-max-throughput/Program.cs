using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dhmp.Server;

[StructLayout(LayoutKind.Sequential,Pack=1)]
readonly struct PlayerState {
 public readonly int Id; public readonly float X,Y,Z,VX,VY,VZ; public readonly uint Flags;
 public PlayerState(int i){Id=i;X=i*.01f;Y=i*.02f;Z=i*.03f;VX=1;VY=2;VZ=3;Flags=(uint)i&7;}
}
static class Program {
 const int Size=32, Slab=512; static double guard;
 static PlayerState[] Data(int n){var a=new PlayerState[n];for(int i=0;i<n;i++)a[i]=new PlayerState(i);return a;}
 static void Out(string id,string scope,long msgs,long units,long ticks,double g){double s=(double)ticks/Stopwatch.Frequency;Console.WriteLine($"RESULT id={id} scope={scope} messages={msgs} work_units={units} payload_GB={msgs*32.0/1e9:F3} wall_s={s:F6} GBps={msgs*32.0/s/1e9:F3} mps={msgs/s/1e6:F3} ns_msg={s*1e9/msgs:F3} ns_unit={s*1e9/units:F3} guard={g:F3}");}
 [MethodImpl(MethodImplOptions.NoInlining)] static double ReadAll(ReadOnlySpan<PlayerState>x){double s=0;for(int i=0;i<x.Length;i++){ref readonly var q=ref x[i];s+=q.Id+q.X+q.Y+q.Z+q.VX+q.VY+q.VZ+q.Flags;}return s;}
 [MethodImpl(MethodImplOptions.NoInlining)] static double Filter(ReadOnlySpan<PlayerState>x){double s=0;for(int i=0;i<x.Length;i++){ref readonly var q=ref x[i];if((q.Flags&1)!=0)s+=(q.X+q.VX)*(q.Y+q.VY)+(q.Z+q.VZ);}return s;}
 [MethodImpl(MethodImplOptions.NoInlining)] static double Latest(ReadOnlySpan<PlayerState>x){ref readonly var q=ref x[^1];return q.Id+q.X+q.Y+q.Z+q.Flags;}
 static void Direct(string id,PlayerState[] d,int passes,Func<ReadOnlySpan<PlayerState>,double> f,bool latest=false){double g=0;for(int w=0;w<3;w++)g+=ReadAll(d);long t=Stopwatch.GetTimestamp(),units=0;for(int p=0;p<passes;p++)for(int i=0;i<d.Length;i+=Slab){var s=d.AsSpan(i,Math.Min(Slab,d.Length-i));g+=f(s);units+=latest?1:s.Length;}long e=Stopwatch.GetTimestamp();guard=g;Out(id,"dotnet-content",(long)d.Length*passes,units,e-t,g);}
 static void Integrated(PlayerState[] d,int passes){var sp=new DHMPFixedStreamProcessor(Size);var bytes=MemoryMarshal.AsBytes(d.AsSpan());int[] pat=[4093,8191,12287,16381,32749,65521];double g=0;long units=0;DHMPModelBoundary.Validate<PlayerState>(Size);long t=Stopwatch.GetTimestamp();for(int p=0;p<passes;p++){int pos=0,k=0;while(pos<bytes.Length){int n=Math.Min(pat[k++%pat.Length],bytes.Length-pos);sp.Process(bytes.Slice(pos,n),Consume,Consume);pos+=n;}}long e=Stopwatch.GetTimestamp();guard=g;Out("integrated-read-all","dhmp-to-content",(long)d.Length*passes,units,e-t,g);void Consume(ReadOnlySpan<byte>s){var m=MemoryMarshal.Cast<byte,PlayerState>(s);g+=ReadAll(m);units+=m.Length;}}
 static void Main(string[] a){int n=a.Length>0?int.Parse(a[0]):5_000_000,passes=a.Length>1?int.Parse(a[1]):5;var d=Data(n);Console.WriteLine($"DHMP_DOTNET_BENCHMARK_V1 PlayerState={Size} slab={Slab} vector_bytes={Vector<byte>.Count}");Direct("sequential-read-all",d,passes,ReadAll);Direct("content-filter",d,passes,Filter);Direct("latest-per-slab",d,passes,Latest,true);Integrated(d,passes);}
}