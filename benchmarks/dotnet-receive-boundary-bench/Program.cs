using Dhmp.Server;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
readonly struct M32 { public readonly ulong A, B, C, D; }

static class Program
{
    static ulong sink;

    static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        int packages = int.Parse(args[0]);
        int passes = int.Parse(args[1]);
        string profile = args.Length > 2 ? args[2] : "receive";
        if (packages <= 0 || passes <= 0) throw new ArgumentOutOfRangeException(nameof(args));
        int[] pattern = profile switch
        {
            "receive" => [4093, 8191, 12287, 16381, 32749, 65521],
            "fragmented" => [31, 1, 33, 31],
            _ => throw new ArgumentException("Expected receive or fragmented profile")
        };
        var data = new byte[checked(packages * 32)];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)i;
        var processor = new DHMPFixedStreamProcessor(32);
        long spans = 0, cross = 0, observedPackages = 0;
        ulong guard = 0;

        // Create all capturing delegates once, including the nested typed consumers.
        Action<ReadOnlySpan<M32>> consumeCross = models =>
        {
            observedPackages += models.Length;
            guard ^= (ulong)models.Length;
            guard ^= models[0].A;
        };
        Action<ReadOnlySpan<M32>> consumeBorrowed = models =>
        {
            observedPackages += models.Length;
            guard ^= (ulong)models.Length;
        };
        Action<ReadOnlySpan<byte>> publishCross = s =>
        {
            DHMPModelBoundary.Consume<M32>(s, 32, consumeCross);
            cross++;
        };
        Action<ReadOnlySpan<byte>> publishBorrowed = s =>
        {
            DHMPModelBoundary.Consume<M32>(s, 32, consumeBorrowed);
            spans++;
        };
        void Run(int count)
        {
            for (int pass = 0; pass < count; pass++)
            {
                int position = 0, k = 0;
                while (position < data.Length)
                {
                    int n = Math.Min(pattern[k++ % pattern.Length], data.Length - position);
                    processor.Process(data.AsSpan(position, n), publishCross, publishBorrowed);
                    position += n;
                }
            }
        }

        Run(3);
        spans = cross = observedPackages = 0;
        guard = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        Run(passes);
        long elapsed = Stopwatch.GetTimestamp() - start;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        sink = guard;
        long messages = (long)packages * passes;
        if (observedPackages != messages) throw new Exception("Incorrect published package count");
        if (allocated != 0) throw new Exception($"Timed processing allocated {allocated} bytes");
        double seconds = (double)elapsed / Stopwatch.Frequency;
        Console.WriteLine($"benchmark=production-receive-boundary profile={profile} messages={messages} bytes={messages * 32} publications={spans + cross} crossing_packages={cross} wall_s={seconds:F6} logical_GBps={messages * 32.0 / seconds / 1e9:F3} logical_mps={messages / seconds / 1e6:F3} ns_msg={seconds * 1e9 / messages:F3} allocated_bytes={allocated} guard={guard}");
    }
}
