using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack=1)]
readonly struct PlayerState {
 public readonly int Id; public readonly float X,Y,Z,VX,VY,VZ; public readonly uint Flags;
 public PlayerState(int i){Id=i;X=i*.01f;Y=i*.02f;Z=i*.03f;VX=1;VY=2;VZ=3;Flags=(uint)i&7;}
}

static class Program {
 const int Size=32, Batch=512;
 static long sink;

 static PlayerState[] Data(int n){var a=new PlayerState[n];for(int i=0;i<n;i++)a[i]=new PlayerState(i);return a;}

 static void Out(string id,long messages,long spans,long ticks){
  double s=(double)ticks/Stopwatch.Frequency;
  Console.WriteLine($"ADAPTER_OPT id={id} messages={messages} spans={spans} wall_s={s:F6} logical_GBps={messages*Size/s/1e9:F3} logical_mps={messages/s/1e6:F3} Mspans={spans/s/1e6:F3} ns_span={s*1e9/spans:F3} ns_msg_amortized={s*1e9/messages:F6} guard={sink}");
 }

 static void Main(string[] args){
  int n=args.Length>0?int.Parse(args[0]):5_000_000, passes=args.Length>1?int.Parse(args[1]):20;
  n-=n%Batch; var d=Data(n);
  Console.WriteLine($"DHMP_DOTNET_ADAPTER_OPT_V1 PlayerState={Size} batch={Batch} processor=excluded application=excluded");
  Run("o0-typed-baseline",d,n,passes,0);
  Run("o1-direct-cast",d,n,passes,1);
  Run("o2-static-helper",d,n,passes,2);
  Run("o3-generic-inline",d,n,passes,3);
  Run("o4-size-specialized",d,n,passes,4);
  Run("o5-typed-delegate",d,n,passes,5);
  Run("o6-byte-delegate",d,n,passes,6);
  Run("o7-interface-consumer",d,n,passes,7);
  Run("o8-struct-consumer",d,n,passes,8);
  Run("o9-borrow-try-finally",d,n,passes,9);
 }

 static void Run(string id,PlayerState[] d,int n,int passes,int variant){
  for(int w=0;w<4;w++)Pass(d,n,variant);
  long t=Stopwatch.GetTimestamp(); for(int p=0;p<passes;p++)Pass(d,n,variant); long e=Stopwatch.GetTimestamp();
  long spans=(long)(n/Batch)*passes; Out(id,(long)n*passes,spans,e-t);
 }

 [MethodImpl(MethodImplOptions.NoInlining)]
 static void Pass(PlayerState[] d,int n,int variant){
  var bytes=MemoryMarshal.AsBytes(d.AsSpan()); var typed=d.AsSpan(); int count=n/Batch;
  switch(variant){
   case 0: for(int i=0;i<count;i++)Observe(typed.Slice(i*Batch,Batch)); break;
   case 1: for(int i=0;i<count;i++)Observe(MemoryMarshal.Cast<byte,PlayerState>(bytes.Slice(i*Batch*Size,Batch*Size))); break;
   case 2: for(int i=0;i<count;i++)StaticCast(bytes.Slice(i*Batch*Size,Batch*Size)); break;
   case 3: for(int i=0;i<count;i++)GenericCast<PlayerState>(bytes.Slice(i*Batch*Size,Batch*Size)); break;
   case 4: for(int i=0;i<count;i++)Size32Cast(bytes.Slice(i*Batch*Size,Batch*Size)); break;
   case 5: {Action<ReadOnlySpan<PlayerState>> c=Observe;for(int i=0;i<count;i++)c(MemoryMarshal.Cast<byte,PlayerState>(bytes.Slice(i*Batch*Size,Batch*Size)));break;}
   case 6: {Action<ReadOnlySpan<byte>> c=ByteConsume;for(int i=0;i<count;i++)c(bytes.Slice(i*Batch*Size,Batch*Size));break;}
   case 7: {IConsumer c=new InterfaceConsumer();for(int i=0;i<count;i++)c.Consume(MemoryMarshal.Cast<byte,PlayerState>(bytes.Slice(i*Batch*Size,Batch*Size)));break;}
   case 8: {var c=new StructConsumer();for(int i=0;i<count;i++)ConsumeStruct(bytes.Slice(i*Batch*Size,Batch*Size),ref c);break;}
   case 9: {bool borrowed=false;for(int i=0;i<count;i++){if(borrowed)throw new InvalidOperationException();borrowed=true;try{Observe(MemoryMarshal.Cast<byte,PlayerState>(bytes.Slice(i*Batch*Size,Batch*Size)));}finally{borrowed=false;}}break;}
  }
 }

 [MethodImpl(MethodImplOptions.NoInlining)] static void Observe(ReadOnlySpan<PlayerState> x){sink+=x.Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void StaticCast(ReadOnlySpan<byte>b)=>Observe(MemoryMarshal.Cast<byte,PlayerState>(b));
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void GenericCast<T>(ReadOnlySpan<byte>b) where T:unmanaged {var x=MemoryMarshal.Cast<byte,T>(b);sink+=x.Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void Size32Cast(ReadOnlySpan<byte>b)=>Observe(MemoryMarshal.Cast<byte,PlayerState>(b));
 [MethodImpl(MethodImplOptions.NoInlining)] static void ByteConsume(ReadOnlySpan<byte>b)=>Observe(MemoryMarshal.Cast<byte,PlayerState>(b));

 interface IConsumer{void Consume(ReadOnlySpan<PlayerState>x);}
 sealed class InterfaceConsumer:IConsumer{[MethodImpl(MethodImplOptions.NoInlining)]public void Consume(ReadOnlySpan<PlayerState>x)=>sink+=x.Length;}
 struct StructConsumer{[MethodImpl(MethodImplOptions.AggressiveInlining)]public void Consume(ReadOnlySpan<PlayerState>x)=>sink+=x.Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void ConsumeStruct<TConsumer>(ReadOnlySpan<byte>b,ref TConsumer c) where TConsumer:struct {var x=MemoryMarshal.Cast<byte,PlayerState>(b);if(typeof(TConsumer)==typeof(StructConsumer))Unsafe.As<TConsumer,StructConsumer>(ref c).Consume(x);}
}