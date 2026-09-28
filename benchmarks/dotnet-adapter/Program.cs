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
  for(int v=10;v<20;v++)Run($"o{v}-o3-derived",d,n,passes,v);
  for(int v=20;v<30;v++)Run($"o{v}-o15-derived",d,n,passes,v);
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
   case 10: for(int i=0;i<count;i++)GenericCast<PlayerState>(bytes.Slice(i*Batch*Size,Batch*Size)); break;
   case 11: for(int i=0,off=0;i<count;i++,off+=Batch*Size)GenericCast<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 12: for(int off=0,end=count*Batch*Size;off<end;off+=Batch*Size)GenericCast<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 13: for(int i=0,off=0;i<count;i++,off+=Batch*Size)GenericCastUnchecked<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 14: for(int i=0,off=0;i<count;i++,off+=Batch*Size)GenericCount<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 15: for(int i=0,off=0;i<count;i++,off+=Batch*Size)GenericCountKnown<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 16: for(int i=0,off=0;i<count;i++,off+=Batch*Size)GenericCastNoInline<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 17: for(int i=0,off=0;i<count;i++,off+=Batch*Size)GenericCastRef<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 18: for(int i=0,off=0;i<count;i++,off+=Batch*Size)GenericCastLocal<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 19: for(int i=0,off=0;i<count;i++,off+=Batch*Size)GenericCastCount<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 20: for(int i=0,off=0;i<count;i++,off+=Batch*Size)GenericCountKnown<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 21: for(int i=0,off=0;i<count;i++,off+=Batch*Size)KnownCount32(bytes.Slice(off,Batch*Size)); break;
   case 22: for(int i=0,off=0;i<count;i++,off+=Batch*Size)sink+=bytes.Slice(off,Batch*Size).Length/Size; break;
   case 23: for(int i=0,off=0;i<count;i++,off+=Batch*Size)sink+=bytes.Slice(off,Batch*Size).Length>>5; break;
   case 24: for(int off=0,end=count*Batch*Size;off<end;off+=Batch*Size)sink+=Batch; break;
   case 25: for(int i=0;i<count;i++)sink+=Batch; break;
   case 26: {int off=0;for(int i=0;i<count;i++){sink+=bytes.Slice(off,Batch*Size).Length>>5;off+=Batch*Size;}break;}
   case 27: for(int i=0,off=0;i<count;i++,off+=Batch*Size)KnownCountGeneric<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 28: for(int i=0,off=0;i<count;i++,off+=Batch*Size)KnownCountGenericShift<PlayerState>(bytes.Slice(off,Batch*Size)); break;
   case 29: for(int i=0,off=0;i<count;i++,off+=Batch*Size)KnownCountNoInline<PlayerState>(bytes.Slice(off,Batch*Size)); break;}
  }

 [MethodImpl(MethodImplOptions.NoInlining)] static void Observe(ReadOnlySpan<PlayerState> x){sink+=x.Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void StaticCast(ReadOnlySpan<byte>b)=>Observe(MemoryMarshal.Cast<byte,PlayerState>(b));
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void GenericCast<T>(ReadOnlySpan<byte>b) where T:unmanaged {var x=MemoryMarshal.Cast<byte,T>(b);sink+=x.Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void GenericCastUnchecked<T>(ReadOnlySpan<byte>b) where T:unmanaged {var x=MemoryMarshal.Cast<byte,T>(b);sink+=x.Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void GenericCount<T>(ReadOnlySpan<byte>b) where T:unmanaged {sink+=MemoryMarshal.Cast<byte,T>(b).Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void GenericCountKnown<T>(ReadOnlySpan<byte>b) where T:unmanaged {sink+=b.Length/Unsafe.SizeOf<T>();}
 [MethodImpl(MethodImplOptions.NoInlining)] static void GenericCastNoInline<T>(ReadOnlySpan<byte>b) where T:unmanaged {sink+=MemoryMarshal.Cast<byte,T>(b).Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void GenericCastRef<T>(ReadOnlySpan<byte>b) where T:unmanaged {var x=MemoryMarshal.Cast<byte,T>(b);ref readonly var first=ref MemoryMarshal.GetReference(x);sink+=x.Length+(Unsafe.IsNullRef(in first)?1:0);}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void GenericCastLocal<T>(ReadOnlySpan<byte>b) where T:unmanaged {ReadOnlySpan<T> x=MemoryMarshal.Cast<byte,T>(b);sink+=x.Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void GenericCastCount<T>(ReadOnlySpan<byte>b) where T:unmanaged {int count=b.Length/Unsafe.SizeOf<T>();sink+=count;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void KnownCount32(ReadOnlySpan<byte>b)=>sink+=b.Length>>5;
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void KnownCountGeneric<T>(ReadOnlySpan<byte>b) where T:unmanaged => sink+=b.Length/Unsafe.SizeOf<T>();
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void KnownCountGenericShift<T>(ReadOnlySpan<byte>b) where T:unmanaged {int size=Unsafe.SizeOf<T>();sink+=size==32?b.Length>>5:b.Length/size;}
 [MethodImpl(MethodImplOptions.NoInlining)] static void KnownCountNoInline<T>(ReadOnlySpan<byte>b) where T:unmanaged => sink+=b.Length/Unsafe.SizeOf<T>();
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void Size32Cast(ReadOnlySpan<byte>b)=>Observe(MemoryMarshal.Cast<byte,PlayerState>(b));
 [MethodImpl(MethodImplOptions.NoInlining)] static void ByteConsume(ReadOnlySpan<byte>b)=>Observe(MemoryMarshal.Cast<byte,PlayerState>(b));

 interface IConsumer{void Consume(ReadOnlySpan<PlayerState>x);}
 sealed class InterfaceConsumer:IConsumer{[MethodImpl(MethodImplOptions.NoInlining)]public void Consume(ReadOnlySpan<PlayerState>x)=>sink+=x.Length;}
 struct StructConsumer{[MethodImpl(MethodImplOptions.AggressiveInlining)]public void Consume(ReadOnlySpan<PlayerState>x)=>sink+=x.Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void ConsumeStruct<TConsumer>(ReadOnlySpan<byte>b,ref TConsumer c) where TConsumer:struct {var x=MemoryMarshal.Cast<byte,PlayerState>(b);if(typeof(TConsumer)==typeof(StructConsumer))Unsafe.As<TConsumer,StructConsumer>(ref c).Consume(x);}
}