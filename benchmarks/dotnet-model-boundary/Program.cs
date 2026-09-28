using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

[StructLayout(LayoutKind.Sequential, Pack=1)]
readonly struct P32 { public readonly ulong A,B,C,D; public P32(ulong a){A=a;B=1;C=2;D=a^0x9e3779b97f4a7c15UL;} }
[StructLayout(LayoutKind.Sequential, Pack=1)]
readonly struct M32 { public readonly ulong A,B,C,D; }

static class Program {
 static ulong sink;
 static P32[] Data(int n){var a=new P32[n];for(int i=0;i<n;i++)a[i]=new P32((ulong)i);return a;}
 static void Out(string stage,long messages,long ticks,ulong guard,long spans){double s=(double)ticks/Stopwatch.Frequency;Console.WriteLine($"stage={stage} messages={messages} bytes={messages*32L} spans={spans} wall_s={s:F6} logical_GBps={messages*32.0/s/1e9:F3} logical_mps={messages/s/1e6:F3} ns_msg={s*1e9/messages:F3} ns_span={s*1e9/spans:F3} guard={guard}");}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void ObserveSpan(ReadOnlySpan<M32> m,ref ulong g){g^=(ulong)m.Length;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] static void ObserveLatest(ReadOnlySpan<M32> m,ref ulong g){g^=(ulong)m.Length;g^=m[m.Length-1].A;}

 static void Run(P32[] a,int passes,bool lease,bool latest,string name){
   const int slab=512,slots=32;var state=new int[slots];ulong g=0;long spans=0;int slot=0;long messages=(long)a.Length*passes;
   long t=Stopwatch.GetTimestamp();
   for(int pass=0;pass<passes;pass++)for(int p=0;p<a.Length;p+=slab){int n=Math.Min(slab,a.Length-p);
     if(lease && Interlocked.CompareExchange(ref state[slot],1,0)!=0)throw new InvalidOperationException("region busy");
     ReadOnlySpan<M32> m=MemoryMarshal.Cast<P32,M32>(a.AsSpan(p,n));
     if(latest)ObserveLatest(m,ref g);else ObserveSpan(m,ref g);
     if(lease)Volatile.Write(ref state[slot],0);
     spans++;slot++;if(slot==slots)slot=0;
   }
   long e=Stopwatch.GetTimestamp();sink=g;Out(name,messages,e-t,g,spans);
 }
 static void AppRef(P32[] a,int passes){ulong s=0;long messages=(long)a.Length*passes;long t=Stopwatch.GetTimestamp();for(int pass=0;pass<passes;pass++){ReadOnlySpan<M32> m=MemoryMarshal.Cast<P32,M32>(a);for(int i=0;i<m.Length;i++)s+=m[i].A+m[i].D;}long e=Stopwatch.GetTimestamp();sink=s;Out("REF-app-consume-out-of-scope",messages,e-t,s,messages);}
 static void Warm(P32[] a){for(int i=0;i<4;i++)Run(a,1,false,false,"warm");}
 static void Main(string[] args){string stage=args[0];int n=int.Parse(args[1]);int passes=int.Parse(args[2]);var a=Data(n);Warm(a);switch(stage){
  case "seq-base":Run(a,passes,false,false,"B0-sequential-base");break;
  case "seq-lease":Run(a,passes,true,false,"B1-sequential-lease");break;
  case "latest-base":Run(a,passes,false,true,"B2-latest-base");break;
  case "latest-lease":Run(a,passes,true,true,"B3-latest-lease");break;
  case "app-reference":AppRef(a,passes);break;
  default:throw new ArgumentException(stage);
 }}
}
