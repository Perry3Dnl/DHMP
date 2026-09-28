using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential,Pack=1)]
readonly struct PlayerState {
 public readonly int Id; public readonly float X,Y,Z,VX,VY,VZ; public readonly uint Flags;
 public PlayerState(int i){Id=i;X=i*.01f;Y=i*.02f;Z=i*.03f;VX=1;VY=2;VZ=3;Flags=(uint)i&7;}
}
static class Program {
 const int Size=32; static double sink;
 static PlayerState[] Data(int n){var a=new PlayerState[n];for(int i=0;i<n;i++)a[i]=new PlayerState(i);return a;}
 [MethodImpl(MethodImplOptions.NoInlining)] static double Consume(ReadOnlySpan<PlayerState>x){double s=0;for(int i=0;i<x.Length;i++){ref readonly var q=ref x[i];s+=q.Id+q.X+q.Y+q.Z+q.VX+q.VY+q.VZ+q.Flags;}return s;}
 static void Out(string id,long msgs,long ticks,double g){double s=(double)ticks/Stopwatch.Frequency;Console.WriteLine($"ADAPTER id={id} messages={msgs} wall_s={s:F6} GBps={msgs*Size/s/1e9:F3} mps={msgs/s/1e6:F3} ns_msg={s*1e9/msgs:F3} guard={g:F3}");}
 static void Measure(string id,int n,int passes,Action body){for(int i=0;i<8;i++)body();long t=Stopwatch.GetTimestamp();for(int p=0;p<passes;p++)body();long e=Stopwatch.GetTimestamp();Out(id,(long)n*passes,e-t,sink);}
 static void Main(string[] a){int n=a.Length>0?int.Parse(a[0]):5_000_000,passes=a.Length>1?int.Parse(a[1]):5;var d=Data(n);var bytes=MemoryMarshal.AsBytes(d.AsSpan());Console.WriteLine("DHMP_DOTNET_ADAPTER_V1 PlayerState=32 -- processor excluded");
  Measure("a0-typed-direct",n,passes,()=>sink+=Consume(d));
  Measure("a1-byte-cast-consume",n,passes,()=>sink+=Consume(MemoryMarshal.Cast<byte,PlayerState>(bytes)));
  Action<ReadOnlySpan<PlayerState>> typed=x=>sink+=Consume(x);
  Measure("a2-typed-delegate",n,passes,()=>typed(d));
  Action<ReadOnlySpan<byte>> byteBoundary=b=>typed(MemoryMarshal.Cast<byte,PlayerState>(b));
  Measure("a3-byte-boundary-delegate",n,passes,()=>byteBoundary(bytes));
  var adapter=new Adapter<PlayerState>(Size);
  Measure("a4-validated-adapter",n,passes,()=>adapter.Consume(bytes,typed));
 }
 sealed class Adapter<T> where T:unmanaged {
  readonly int size;
  public Adapter(int packageSize){int actual=Unsafe.SizeOf<T>();if(packageSize!=actual)throw new ArgumentException($"Contract {packageSize} != model {actual}");size=actual;}
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void Consume(ReadOnlySpan<byte>b,Action<ReadOnlySpan<T>> consumer){if(b.Length%size!=0)throw new ArgumentException("Incomplete package span");consumer(MemoryMarshal.Cast<byte,T>(b));}
 }
}