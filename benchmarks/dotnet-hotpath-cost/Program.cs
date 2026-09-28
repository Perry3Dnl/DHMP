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
  int[] pat=[4093,8191,12287,16381,32749,65521];
  ProcessorVariants(d,bytes,passes,pat);
 }

 static void ProcessorVariants(PlayerState[] d,ReadOnlySpan<byte> bytes,int passes,int[] pat)
 {
  RunProcessor("processor-v0-eb13-baseline",new ProcessorV0(Size),d,bytes,passes,pat);
  RunProcessor("processor-v1-helper-trim",new ProcessorV1(Size),d,bytes,passes,pat);
  RunProcessor("processor-v2-flat-carry",new ProcessorV2(Size),d,bytes,passes,pat);
  RunProcessor("processor-v3-store-remainder-helper",new ProcessorV3(Size),d,bytes,passes,pat);
  RunProcessor("processor-v4-complete-carry-helper",new ProcessorV4(Size),d,bytes,passes,pat);
 }
 interface IProcessor { void Process(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> cross,Action<ReadOnlySpan<byte>> borrowed); }
 static void RunProcessor(string id,IProcessor sp,PlayerState[] d,ReadOnlySpan<byte> bytes,int passes,int[] pat)
 {
  long units=0;double g=0;long t=Stopwatch.GetTimestamp();
  for(int p=0;p<passes;p++){int pos=0,k=0;while(pos<bytes.Length){int m=Math.Min(pat[k++%pat.Length],bytes.Length-pos);sp.Process(bytes.Slice(pos,m),Consume,Consume);pos+=m;}}
  long e=Stopwatch.GetTimestamp();guard=g;Out(id,(long)d.Length*passes,units,e-t,g);
  void Consume(ReadOnlySpan<byte>s){var x=MemoryMarshal.Cast<byte,PlayerState>(s);g+=ReadAll(x);units+=x.Length;}
 }

 abstract class ProcessorBase : IProcessor {
  protected readonly int size,mask; protected readonly bool pow2; protected int slot,len;
  protected ProcessorBase(int s){size=s;pow2=(s&(s-1))==0;mask=pow2?s-1:0;}
  public abstract void Process(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> cross,Action<ReadOnlySpan<byte>> borrowed);
 }
 sealed class ProcessorV0(int s):ProcessorBase(s) {
  readonly byte[][] carry=[new byte[s],new byte[s]];readonly bool[] used=new bool[2];
  public override void Process(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> cross,Action<ReadOnlySpan<byte>> borrowed){
   if(len!=0){int take=Math.Min(size-len,input.Length);input[..take].CopyTo(carry[slot].AsSpan(len));len+=take;input=input[take..];if(len!=size)return;Borrow(cross);len=0;slot^=1;}
   int rem=pow2?input.Length&mask:input.Length%size,complete=input.Length-rem;if(complete!=0){borrowed(input[..complete]);input=input[complete..];}
   if(input.IsEmpty)return;if(used[slot])throw new InvalidOperationException();input.CopyTo(carry[slot]);len=input.Length;
  }
  void Borrow(Action<ReadOnlySpan<byte>> cb){if(used[slot])throw new InvalidOperationException();used[slot]=true;try{cb(carry[slot]);}finally{used[slot]=false;}}
 }
 sealed class ProcessorV1(int s):ProcessorBase(s) {
  readonly byte[][] carry=[new byte[s],new byte[s]];readonly bool[] used=new bool[2];
  public override void Process(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> cross,Action<ReadOnlySpan<byte>> borrowed){
   if(len!=0){int needed=size-len,take=input.Length<needed?input.Length:needed;input[..take].CopyTo(carry[slot].AsSpan(len));len+=take;input=input.Slice(take);if(len!=size)return;Borrow(cross);len=0;slot^=1;}
   int rem=pow2?input.Length&mask:input.Length%size,complete=input.Length-rem;if(complete!=0){borrowed(input.Slice(0,complete));input=input.Slice(complete);}
   if(input.IsEmpty)return;if(used[slot])throw new InvalidOperationException();input.CopyTo(carry[slot]);len=input.Length;
  }
  void Borrow(Action<ReadOnlySpan<byte>> cb){if(used[slot])throw new InvalidOperationException();used[slot]=true;try{cb(carry[slot]);}finally{used[slot]=false;}}
 }
 sealed class ProcessorV2(int s):ProcessorBase(s) {
  readonly byte[] c0=new byte[s],c1=new byte[s];bool u0,u1;
  public override void Process(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> cross,Action<ReadOnlySpan<byte>> borrowed){
   if(len!=0){int needed=size-len,take=input.Length<needed?input.Length:needed;byte[] c=slot==0?c0:c1;input[..take].CopyTo(c.AsSpan(len));len+=take;input=input.Slice(take);if(len!=size)return;Borrow(cross);len=0;slot^=1;}
   int rem=pow2?input.Length&mask:input.Length%size,complete=input.Length-rem;if(complete!=0){borrowed(input.Slice(0,complete));input=input.Slice(complete);}
   if(input.IsEmpty)return;if(slot==0?u0:u1)throw new InvalidOperationException();input.CopyTo(slot==0?c0:c1);len=input.Length;
  }
  void Borrow(Action<ReadOnlySpan<byte>> cb){if(slot==0){if(u0)throw new InvalidOperationException();u0=true;try{cb(c0);}finally{u0=false;}return;}if(u1)throw new InvalidOperationException();u1=true;try{cb(c1);}finally{u1=false;}}
 }
 sealed class ProcessorV3(int s):ProcessorBase(s) {
  readonly byte[] c0=new byte[s],c1=new byte[s];bool u0,u1;
  public override void Process(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> cross,Action<ReadOnlySpan<byte>> borrowed){
   if(len!=0){int needed=size-len,take=input.Length<needed?input.Length:needed;byte[] c=slot==0?c0:c1;input[..take].CopyTo(c.AsSpan(len));len+=take;input=input.Slice(take);if(len!=size)return;Borrow(cross);len=0;slot^=1;}
   int rem=pow2?input.Length&mask:input.Length%size,complete=input.Length-rem;if(complete!=0){borrowed(input.Slice(0,complete));input=input.Slice(complete);}if(rem==0)return;Store(input);
  }
  void Store(ReadOnlySpan<byte> input){if(slot==0?u0:u1)throw new InvalidOperationException();input.CopyTo(slot==0?c0:c1);len=input.Length;}
  void Borrow(Action<ReadOnlySpan<byte>> cb){if(slot==0){if(u0)throw new InvalidOperationException();u0=true;try{cb(c0);}finally{u0=false;}return;}if(u1)throw new InvalidOperationException();u1=true;try{cb(c1);}finally{u1=false;}}
 }
 sealed class ProcessorV4(int s):ProcessorBase(s) {
  readonly byte[] c0=new byte[s],c1=new byte[s];bool u0,u1;
  public override void Process(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> cross,Action<ReadOnlySpan<byte>> borrowed){
   if(len!=0&&!Complete(ref input,cross))return;int n=input.Length,rem=pow2?n&mask:n%size,complete=n-rem;if(complete!=0){borrowed(input.Slice(0,complete));input=input.Slice(complete);}if(rem==0)return;Store(input);
  }
  bool Complete(ref ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> cb){int needed=size-len,take=input.Length<needed?input.Length:needed;byte[] c=slot==0?c0:c1;input[..take].CopyTo(c.AsSpan(len));len+=take;input=input.Slice(take);if(len!=size)return false;Borrow(cb);len=0;slot^=1;return true;}
  void Store(ReadOnlySpan<byte> input){if(slot==0?u0:u1)throw new InvalidOperationException();input.CopyTo(slot==0?c0:c1);len=input.Length;}
  void Borrow(Action<ReadOnlySpan<byte>> cb){if(slot==0){if(u0)throw new InvalidOperationException();u0=true;try{cb(c0);}finally{u0=false;}return;}if(u1)throw new InvalidOperationException();u1=true;try{cb(c1);}finally{u1=false;}}
 }
 }
}