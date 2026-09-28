using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack=1)]
readonly struct P32 { public readonly ulong A,B,C,D; public P32(ulong a){A=a;B=1;C=2;D=a^0x9e3779b97f4a7c15UL;} }
[StructLayout(LayoutKind.Sequential, Pack=1)]
readonly struct M32 { public readonly ulong A,B,C,D; }

static class Program {
 static ulong sink;
 static P32[] Data(int n){var a=new P32[n];for(int i=0;i<n;i++)a[i]=new P32((ulong)i);return a;}
 static void Out(string stage,int n,long ticks,ulong sum,long spans=0){double s=(double)ticks/Stopwatch.Frequency;Console.WriteLine($"stage={stage} messages={n} bytes={n*32L} spans={spans} wall_s={s:F6} GBps={n*32.0/s/1e9:F3} mps={n/s/1e6:F3} ns_msg={s*1e9/n:F3} guard={sum}");}
 [MethodImpl(MethodImplOptions.NoInlining)]
 static void AcceptSpan(ReadOnlySpan<P32> span,ref ulong guard){guard^=(ulong)span.Length; if(!span.IsEmpty)guard^=span[0].A;}
 static void SpanAccept(P32[] a){const int slab=512;ulong g=0;long spans=0;long t=Stopwatch.GetTimestamp();for(int p=0;p<a.Length;p+=slab){int n=Math.Min(slab,a.Length-p);AcceptSpan(a.AsSpan(p,n),ref g);spans++;}long e=Stopwatch.GetTimestamp();sink=g;Out("L0-span-accept",a.Length,e-t,g,spans);}
 static void ModelView(P32[] a){ulong s=0;long t=Stopwatch.GetTimestamp();ReadOnlySpan<M32> m=MemoryMarshal.Cast<P32,M32>(a);for(int i=0;i<m.Length;i++)s+=m[i].A+m[i].D;long e=Stopwatch.GetTimestamp();sink=s;Out("L1-zero-copy-model-view",a.Length,e-t,s);}
 static void ModelViewBatch4(P32[] a){ulong a0=0,a1=0,a2=0,a3=0;long t=Stopwatch.GetTimestamp();ReadOnlySpan<M32> m=MemoryMarshal.Cast<P32,M32>(a);int i=0;for(;i+4<=m.Length;i+=4){a0+=m[i].A+m[i].D;a1+=m[i+1].A+m[i+1].D;a2+=m[i+2].A+m[i+2].D;a3+=m[i+3].A+m[i+3].D;}ulong s=a0+a1+a2+a3;for(;i<m.Length;i++)s+=m[i].A+m[i].D;long e=Stopwatch.GetTimestamp();sink=s;Out("L2-zero-copy-batch4",a.Length,e-t,s);}
 static void CopyModels(P32[] a){var dst=new M32[a.Length];ulong s=0;long t=Stopwatch.GetTimestamp();ReadOnlySpan<M32> src=MemoryMarshal.Cast<P32,M32>(a);src.CopyTo(dst);for(int i=0;i<dst.Length;i++)s+=dst[i].A+dst[i].D;long e=Stopwatch.GetTimestamp();sink=s;Out("L3-copy-to-model-buffer",a.Length,e-t,s);}
 static void Latest(P32[] a){const int slab=512;ulong s=0;long spans=0;long t=Stopwatch.GetTimestamp();ReadOnlySpan<M32> m=MemoryMarshal.Cast<P32,M32>(a);for(int p=0;p<m.Length;p+=slab){int n=Math.Min(slab,m.Length-p);ref readonly var latest=ref m[p+n-1];s+=latest.A+latest.D;spans++;}long e=Stopwatch.GetTimestamp();sink=s;Out("L4-latest-per-span",a.Length,e-t,s,spans);}
 static void Warm(P32[] a){ulong g=0;AcceptSpan(a.AsSpan(0,Math.Min(512,a.Length)),ref g);sink=g;}
 static void Main(string[] args){string stage=args[0];int n=int.Parse(args[1]);var a=Data(n);Warm(a);switch(stage){case "span-accept":SpanAccept(a);break;case "model-view":ModelView(a);break;case "model-view-batch4":ModelViewBatch4(a);break;case "copy-models":CopyModels(a);break;case "latest":Latest(a);break;default:throw new ArgumentException(stage);}}
}
