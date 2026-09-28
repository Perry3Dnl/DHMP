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
 static double guard;
 static PlayerState[] Data(int n){var a=new PlayerState[n];for(int i=0;i<n;i++)a[i]=new PlayerState(i);return a;}
 static void Print(string mode,long msgs,long ticks,double g){double s=(double)ticks/Stopwatch.Frequency;Console.WriteLine($"mode={mode} messages={msgs} payload_GB={msgs*32.0/1e9:F3} wall_s={s:F6} GBps={msgs*32.0/s/1e9:F3} mps={msgs/s/1e6:F3} ns_msg={s*1e9/msgs:F3} guard={g:F3}");}
 [MethodImpl(MethodImplOptions.NoInlining)] static double ReadAll(ReadOnlySpan<PlayerState> x){double s=0;for(int i=0;i<x.Length;i++){ref readonly var p=ref x[i];s+=p.Id+p.X+p.Y+p.Z+p.VX+p.VY+p.VZ+p.Flags;}return s;}
 [MethodImpl(MethodImplOptions.NoInlining)] static double FilterUpdate(ReadOnlySpan<PlayerState> x){double s=0;for(int i=0;i<x.Length;i++){ref readonly var p=ref x[i];if((p.Flags&1)!=0)s+=(p.X+p.VX)*(p.Y+p.VY)+(p.Z+p.VZ);}return s;}
 [MethodImpl(MethodImplOptions.NoInlining)] static double Latest(ReadOnlySpan<PlayerState> x){ref readonly var p=ref x[^1];return p.Id+p.X+p.Y+p.Z+p.Flags;}
 static void Run(string mode,PlayerState[] data,int passes){
  ReadOnlySpan<byte> bytes=MemoryMarshal.AsBytes(data.AsSpan());double g=0;long msgs=(long)data.Length*passes;
  DHMPModelBoundary.Validate<PlayerState>(32);
  for(int w=0;w<3;w++)g+=ReadAll(data);
  long t=Stopwatch.GetTimestamp();
  for(int p=0;p<passes;p++){var models=MemoryMarshal.Cast<byte,PlayerState>(bytes);g+=mode switch{"read-all"=>ReadAll(models),"filter-update"=>FilterUpdate(models),"latest"=>Latest(models),_=>throw new ArgumentException(mode)};}
  long e=Stopwatch.GetTimestamp();guard=g;Print(mode,msgs,e-t,g);
 }
 static void Main(string[] args){int n=args.Length>0?int.Parse(args[0]):10_000_000;int passes=args.Length>1?int.Parse(args[1]):10;var d=Data(n);
  Console.WriteLine($"DHMP .NET max-throughput demo | PlayerState=32 bytes | VectorWidth={Vector<byte>.Count} bytes");
  Run("read-all",d,passes);Run("filter-update",d,passes);Run("latest",d,passes);
 }
}