using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

[StructLayout(LayoutKind.Sequential, Pack=1)]
struct P32 { public ulong A,B,C,D; }
[StructLayout(LayoutKind.Sequential, Pack=1)]
struct M32 { public ulong A,B,C,D; }

static class Program {
    static ulong sink;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static M32 Cast(in P32 p) => Unsafe.As<P32,M32>(ref Unsafe.AsRef(in p));

    static P32[] CreateInput(int n) {
        var a=new P32[n];
        for(int i=0;i<n;i++) a[i]=new P32{A=(ulong)i,B=1,C=2,D=(ulong)i^0x9e3779b97f4a7c15UL};
        return a;
    }
    static void Out(string stage,int n,long ticks,ulong sum) {
        double sec=(double)ticks/Stopwatch.Frequency;
        Console.WriteLine($"stage={stage} messages={n} bytes={n*32L} wall_s={sec:F6} GBps={n*32.0/sec/1e9:F3} mps={n/sec/1e6:F3} ns_msg={sec*1e9/n:F3} checksum={sum}");
    }
    static void Baseline(P32[] input) {
        ulong sum=0; long t=Stopwatch.GetTimestamp();
        for(int i=0;i<input.Length;i++){ref readonly var p=ref input[i];sum+=p.A+p.D;}
        long e=Stopwatch.GetTimestamp();sink=sum;Out("baseline-read",input.Length,e-t,sum);
    }
    static void DirectCast(P32[] input) {
        ulong sum=0; long t=Stopwatch.GetTimestamp();
        for(int i=0;i<input.Length;i++){var m=Cast(in input[i]);sum+=m.A+m.D;}
        long e=Stopwatch.GetTimestamp();sink=sum;Out("direct-cast",input.Length,e-t,sum);
    }
    static void WorkerWrite(P32[] input,int workers) {
        var output=new M32[input.Length];
        var sums=new ulong[workers];
        var threads=new Thread[workers];
        using var start=new ManualResetEventSlim(false);

        // Contiguous, non-overlapping slices: no per-message atomics, locks, or shared write cache lines.
        int chunk=(input.Length+workers-1)/workers;
        for(int w=0;w<workers;w++) {
            int id=w, begin=Math.Min(input.Length,w*chunk), end=Math.Min(input.Length,(w+1)*chunk);
            threads[w]=new Thread(()=>{
                start.Wait();
                ulong local=0;
                for(int i=begin;i<end;i++) {
                    output[i]=Cast(in input[i]);
                    local+=output[i].A+output[i].D;
                }
                sums[id]=local;
            });
            threads[w].IsBackground=true;
            threads[w].Start();
        }

        long t=Stopwatch.GetTimestamp();
        start.Set();
        for(int w=0;w<workers;w++) threads[w].Join();
        long e=Stopwatch.GetTimestamp();
        ulong sum=0;for(int w=0;w<workers;w++)sum+=sums[w];
        sink=sum;Out($"model-write-{workers}w",input.Length,e-t,sum);
    }
    static void Warm(P32[] input) {
        int n=Math.Min(input.Length,10000);ulong s=0;
        for(int i=0;i<n;i++){var m=Cast(in input[i]);s+=m.A+m.D;}sink=s;
    }
    static void Main(string[] args) {
        string stage=args[0];int n=int.Parse(args[1]);
        var input=CreateInput(n);Warm(input);
        switch(stage) {
            case "baseline-read":Baseline(input);break;
            case "direct-cast":DirectCast(input);break;
            case "model-write-1w":WorkerWrite(input,1);break;
            case "model-write-2w":WorkerWrite(input,2);break;
            case "model-write-4w":WorkerWrite(input,4);break;
            default:throw new ArgumentException(stage);
        }
    }
}