using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
[StructLayout(LayoutKind.Sequential, Pack=1)] struct Package32 { public uint V0,V1,V2,V3,V4,V5,V6,V7; }
[StructLayout(LayoutKind.Sequential, Pack=1)] struct Model32 { public uint V0,V1,V2,V3,V4,V5,V6,V7; }
static class Program {
 static long _sink;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static Model32 Materialize(in Package32 p)=>Unsafe.As<Package32,Model32>(ref Unsafe.AsRef(in p));
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static ulong Consume(in Model32 m)=>(ulong)m.V0+m.V3+m.V7;
 static Package32 Make(int i)=>new(){V0=(uint)i,V1=1,V2=2,V3=3,V4=4,V5=5,V6=6,V7=(uint)((ulong)i^0x9e3779b9u)};
 static void Direct(Package32[] p){ulong s=0;var sw=Stopwatch.StartNew();for(int i=0;i<p.Length;i++){var m=Materialize(in p[i]);s+=Consume(in m);}sw.Stop();Volatile.Write(ref _sink,(long)s);Console.WriteLine($"mode=dotnet-direct workers=1 messages={p.Length} wall_s={sw.Elapsed.TotalSeconds:F6} mps={p.Length/sw.Elapsed.TotalSeconds/1e6:F3} ns_msg={sw.Elapsed.TotalSeconds*1e9/p.Length:F3} checksum={s}");}
 static void Parallel(Package32[] p,int workers){var sums=new ulong[workers];var ts=new Thread[workers];using var gate=new ManualResetEventSlim(false);int chunk=(p.Length+workers-1)/workers;for(int w=0;w<workers;w++){int id=w,start=w*chunk,end=Math.Min(p.Length,start+chunk);ts[w]=new Thread(()=>{ulong s=0;gate.Wait();for(int i=start;i<end;i++){var m=Materialize(in p[i]);s+=Consume(in m);}sums[id]=s;}){IsBackground=true};ts[w].Start();}long a=GC.GetTotalAllocatedBytes(true);var sw=Stopwatch.StartNew();gate.Set();foreach(var t in ts)t.Join();sw.Stop();ulong sum=0;foreach(var s in sums)sum+=s;Volatile.Write(ref _sink,(long)sum);Console.WriteLine($"mode=dotnet-parallel workers={workers} messages={p.Length} wall_s={sw.Elapsed.TotalSeconds:F6} mps={p.Length/sw.Elapsed.TotalSeconds/1e6:F3} ns_msg={sw.Elapsed.TotalSeconds*1e9/p.Length:F3} allocated_bytes={GC.GetTotalAllocatedBytes(true)-a} checksum={sum}");}
 static void Main(string[] args){int n=args.Length>0?int.Parse(args[0]):10000000;var p=new Package32[n];for(int i=0;i<n;i++)p[i]=Make(i);ulong warm=0;for(int i=0;i<Math.Min(n,200000);i++){var m=Materialize(in p[i]);warm+=Consume(in m);}Volatile.Write(ref _sink,(long)warm);Direct(p);Parallel(p,1);Parallel(p,2);Parallel(p,4);}
}