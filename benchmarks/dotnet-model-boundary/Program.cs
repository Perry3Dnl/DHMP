using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Package32 { public uint V0,V1,V2,V3,V4,V5,V6,V7; }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Model32 { public uint V0,V1,V2,V3,V4,V5,V6,V7; }

static class Program
{
    static long _sink;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Model32 Materialize(in Package32 p)
        => Unsafe.As<Package32, Model32>(ref Unsafe.AsRef(in p));

    [MethodImpl(MethodImplOptions.NoInlining)]
    static ulong MockApplication(in Model32 m) => (ulong)m.V0 + m.V3 + m.V7;

    static Package32 Make(ulong i) => new() {
        V0=(uint)i,V1=1,V2=2,V3=3,V4=4,V5=5,V6=6,V7=(uint)(i ^ 0x9e3779b9u)
    };

    static void Main(string[] args)
    {
        int n = args.Length > 0 ? int.Parse(args[0]) : 10_000_000;
        var packages = new Package32[n];
        for (int i=0;i<n;i++) packages[i]=Make((ulong)i);

        // Warm JIT before timing.
        ulong warm=0;
        for(int i=0;i<Math.Min(n,100_000);i++) {
            var m=Materialize(in packages[i]);
            warm += MockApplication(in m);
        }
        Volatile.Write(ref _sink, unchecked((long)warm));

        ulong checksum=0;
        long alloc0=GC.GetAllocatedBytesForCurrentThread();
        var sw=Stopwatch.StartNew();
        for(int i=0;i<n;i++) {
            var m=Materialize(in packages[i]);
            checksum += MockApplication(in m);
        }
        sw.Stop();
        long allocated=GC.GetAllocatedBytesForCurrentThread()-alloc0;
        Volatile.Write(ref _sink, unchecked((long)checksum));

        double seconds=sw.Elapsed.TotalSeconds;
        Console.WriteLine($"mode=dotnet-model messages={n} wall_s={seconds:F6} mps={n/seconds/1e6:F3} ns_msg={seconds*1e9/n:F3} allocated_bytes={allocated} checksum={checksum}");
    }
}
