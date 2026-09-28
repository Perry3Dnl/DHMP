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
 static void Out(string stage,int n,long ticks,ulong guard,long spans){double s=(double)ticks/Stopwatch.Frequency;Console.WriteLine($"stage={stage} messages={n} bytes={n*32L} spans={spans} wall_s={s:F6} logical_GBps={n*32.0/s/1e9:F3} logical_mps={n/s/1e6:F3} ns_msg={s*1e9/n:F3} ns_span={s*1e9/spans:F3} guard={guard}");}
 [MethodImpl(MethodImplOptions.NoInlining)] static void Boundary(ReadOnlySpan<M32> models,ref ulong guard){guard^=(ulong)models.Length;}
 static void TypedBoundary(P32[] a){const int slab=512;ulong g=0;long spans=0;long t=Stopwatch.GetTimestamp();for(int p=0;p<a.Length;p+=slab){int n=Math.Min(slab,a.Length-p);ReadOnlySpan<M32> m=MemoryMarshal.Cast<P32,M32>(a.AsSpan(p,n));Boundary(m,ref g);spans++;}long e=Stopwatch.GetTimestamp();sink=g;Out("B0-typed-span-boundary",a.Length,e-t,g,spans);}
 static void Ownership(P32[] a,bool latest){const int slab=512,slots=32;var state=new int[slots];ulong g=0;long spans=0;int slot=0;long t=Stopwatch.GetTimestamp();
   for(int p=0;p<a.Length;p+=slab){int n=Math.Min(slab,a.Length-p);
     if(Interlocked.CompareExchange(ref state[slot],1,0)!=0)throw new InvalidOperationException("region busy");
     ReadOnlySpan<M32> m=MemoryMarshal.Cast<P32,M32>(a.AsSpan(p,n));
     if(latest){ref readonly var newest=ref m[m.Length-1];g^=(ulong)m.Length;g^=newest.A;} else Boundary(m,ref g);
     Volatile.Write(ref state[slot],0);spans++;slot++;if(slot==slots)slot=0;
   }
   long e=Stopwatch.GetTimestamp();sink=g;Out(latest?"B2-latest-lease":"B1-sequential-lease",a.Length,e-t,g,spans);
 }
 static void ModelConsumeReference(P32[] a){ulong s=0;long t=Stopwatch.GetTimestamp();ReadOnlySpan<M32> m=MemoryMarshal.Cast<P32,M32>(a);for(int i=0;i<m.Length;i++)s+=m[i].A+m[i].D;long e=Stopwatch.GetTimestamp();sink=s;Out("REF-app-consume-out-of-scope",a.Length,e-t,s,a.Length);}
 static void Warm(P32[] a){ulong g=0;ReadOnlySpan<M32> m=MemoryMarshal.Cast<P32,M32>(a.AsSpan(0,Math.Min(512,a.Length)));Boundary(m,ref g);sink=g;}
 static void Main(string[] args){string stage=args[0];int n=int.Parse(args[1]);var a=Data(n);Warm(a);switch(stage){case "typed-boundary":TypedBoundary(a);break;case "sequential-lease":Ownership(a,false);break;case "latest-lease":Ownership(a,true);break;case "app-reference":ModelConsumeReference(a);break;default:throw new ArgumentException(stage);}}
}
