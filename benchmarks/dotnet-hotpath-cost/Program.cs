using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dhmp.Server;

[StructLayout(LayoutKind.Sequential,Pack=1)]
readonly struct PlayerState {
 public readonly int Id; public readonly float X,Y,Z,VX,VY,VZ; public readonly uint Flags;
 public PlayerState(int i){Id=i;X=i*.01f;Y=i*.02f;Z=i*.03f;VX=1;VY=2;VZ=3;Flags=(uint)i&7;}
}
static class Program {
 const int Size=32; static double guard;
 static PlayerState[] Data(int n){var a=new PlayerState[n];for(int i=0;i<n;i++)a[i]=new PlayerState(i);return a;}
 static void Out(string id,long msgs,long units,long ticks,double g){double s=(double)ticks/Stopwatch.Frequency;Console.WriteLine($"HOTPATH id={id} messages={msgs} work_units={units} wall_s={s:F6} GBps={msgs*32.0/s/1e9:F3} mps={msgs/s/1e6:F3} ns_msg={s*1e9/msgs:F3} ns_unit={s*1e9/units:F3} guard={g:F3}");}
 [MethodImpl(MethodImplOptions.NoInlining)] static double ReadAll(ReadOnlySpan<PlayerState>x){double s=0;for(int i=0;i<x.Length;i++){ref readonly var q=ref x[i];s+=q.Id+q.X+q.Y+q.Z+q.VX+q.VY+q.VZ+q.Flags;}return s;}
 static void Run(string id,PlayerState[] d,int passes,Action<ReadOnlySpan<byte>> body){var b=MemoryMarshal.AsBytes(d.AsSpan());for(int w=0;w<3;w++)body(b);long t=Stopwatch.GetTimestamp();for(int p=0;p<passes;p++)body(b);long e=Stopwatch.GetTimestamp();Out(id,(long)d.Length*passes,(long)d.Length*passes,e-t,guard);}
 static void Main(string[] a){int n=a.Length>0?int.Parse(a[0]):5_000_000,passes=a.Length>1?int.Parse(a[1]):5;var d=Data(n);var bytes=MemoryMarshal.AsBytes(d.AsSpan());Console.WriteLine("DHMP_HOTPATH_COST_V1 PlayerState=32");
  Run("span-touch",d,passes,s=>{guard+=s.Length;});
  Run("cast-only",d,passes,s=>{var x=MemoryMarshal.Cast<byte,PlayerState>(s);guard+=x.Length;});
  Run("cast-read-all",d,passes,s=>{var x=MemoryMarshal.Cast<byte,PlayerState>(s);guard+=ReadAll(x);});
  Action<ReadOnlySpan<byte>> cb=s=>{var x=MemoryMarshal.Cast<byte,PlayerState>(s);guard+=ReadAll(x);};
  Run("delegate-cast-read-all",d,passes,s=>cb(s));
  int[] pat=[4093,8191,12287,16381,32749,65521];var sp=new DHMPFixedStreamProcessor(Size);long units=0;double g=0;
  long t=Stopwatch.GetTimestamp();for(int p=0;p<passes;p++){int pos=0,k=0;while(pos<bytes.Length){int m=Math.Min(pat[k++%pat.Length],bytes.Length-pos);sp.Process(bytes.Slice(pos,m),Consume,Consume);pos+=m;}}long e=Stopwatch.GetTimestamp();guard=g;Out("processor-callback-cast-read-all",(long)d.Length*passes,units,e-t,g);
  void Consume(ReadOnlySpan<byte>s){var x=MemoryMarshal.Cast<byte,PlayerState>(s);g+=ReadAll(x);units+=x.Length;}
 }
}