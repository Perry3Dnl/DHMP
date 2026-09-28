using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack=1)]
struct P32 { public ulong A,B,C,D; }
[StructLayout(LayoutKind.Sequential, Pack=1)]
struct M32 { public ulong A,B,C,D; }

static class Program {
    static ulong sink;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static M32 Cast(in P32 p) => Unsafe.As<P32,M32>(ref Unsafe.AsRef(in p));

    static P32[] CreateInput(int n) {
        var a = new P32[n];
        for (int i=0;i<n;i++)
            a[i]=new P32 { A=(ulong)i, B=1, C=2, D=(ulong)i ^ 0x9e3779b97f4a7c15UL };
        return a;
    }

    static void Out(string stage,int n,long ticks,ulong sum) {
        double sec=(double)ticks/Stopwatch.Frequency;
        Console.WriteLine($"stage={stage} messages={n} bytes={n*32L} wall_s={sec:F6} GBps={n*32.0/sec/1e9:F3} mps={n/sec/1e6:F3} ns_msg={sec*1e9/n:F3} checksum={sum}");
    }

    // All four cases use the same prebuilt P32[] input and identical A+D checksum semantics.
    static void Baseline(P32[] input) {
        ulong sum=0; long t=Stopwatch.GetTimestamp();
        for(int i=0;i<input.Length;i++) {
            ref readonly var p=ref input[i];
            sum += p.A + p.D;
        }
        long e=Stopwatch.GetTimestamp(); sink=sum; Out("baseline-read",input.Length,e-t,sum);
    }

    static void DirectCast(P32[] input) {
        ulong sum=0; long t=Stopwatch.GetTimestamp();
        for(int i=0;i<input.Length;i++) {
            var m=Cast(in input[i]);
            sum += m.A + m.D;
        }
        long e=Stopwatch.GetTimestamp(); sink=sum; Out("direct-cast",input.Length,e-t,sum);
    }

    static void CastCopy(P32[] input) {
        var output=new M32[input.Length]; // allocation excluded
        ulong sum=0; long t=Stopwatch.GetTimestamp();
        for(int i=0;i<input.Length;i++) {
            output[i]=Cast(in input[i]);
            sum += output[i].A + output[i].D;
        }
        long e=Stopwatch.GetTimestamp(); sink=sum; Out("cast-copy-model-buffer",input.Length,e-t,sum);
    }

    static void DirectWrite(P32[] input) {
        var output=new M32[input.Length]; // allocation excluded
        ulong sum=0; long t=Stopwatch.GetTimestamp();
        for(int i=0;i<input.Length;i++) {
            // Simulates the stream processor targeting final model storage directly:
            // no intermediate model value and no second package->model copy.
            ref readonly var p=ref input[i];
            ref var m=ref output[i];
            m.A=p.A; m.B=p.B; m.C=p.C; m.D=p.D;
            sum += m.A + m.D;
        }
        long e=Stopwatch.GetTimestamp(); sink=sum; Out("direct-write-model-buffer",input.Length,e-t,sum);
    }

    static void Warm(P32[] input) {
        int n=Math.Min(input.Length,10000);
        ulong s=0;
        for(int i=0;i<n;i++){var m=Cast(in input[i]);s+=m.A+m.D;}
        sink=s;
    }

    static void Main(string[] args) {
        string stage=args[0]; int n=int.Parse(args[1]);
        var input=CreateInput(n); // identical dataset construction, always outside timer
        Warm(input);
        switch(stage) {
            case "baseline-read": Baseline(input); break;
            case "direct-cast": DirectCast(input); break;
            case "cast-copy-model-buffer": CastCopy(input); break;
            case "direct-write-model-buffer": DirectWrite(input); break;
            default: throw new ArgumentException(stage);
        }
    }
}