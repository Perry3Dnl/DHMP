using Dhmp.Server;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
readonly struct M32 { public readonly ulong A, B, C, D; }

static class Program
{
    static long sink;
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static byte[] Data(int size)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)((i * 37 + i / 251) & 255);
        return data;
    }

    static void Check(int packageSize, int[] chunks)
    {
        var source = Data(packageSize * 41 + packageSize - 1);
        var got = new List<byte>();
        var processor = new DHMPFixedStreamProcessor(packageSize);
        Assert(processor.PackageSize == packageSize, "PackageSize changed");
        Action<ReadOnlySpan<byte>> cross = span =>
        {
            Assert(span.Length == packageSize, "Cross-boundary publication must be one package");
            got.AddRange(span.ToArray());
        };
        Action<ReadOnlySpan<byte>> borrowed = span =>
        {
            Assert(!span.IsEmpty && span.Length % packageSize == 0, "Invalid borrowed run");
            got.AddRange(span.ToArray());
        };
        int position = 0;
        void Feed(int count)
        {
            processor.Process(source.AsSpan(position, count), cross, borrowed);
            position += count;
            int expected = position / packageSize * packageSize;
            Assert(got.Count == expected, $"Premature/missing publication for size {packageSize}");
            Assert(CollectionsMarshal.AsSpan(got).SequenceEqual(source.AsSpan(0, expected)),
                $"Corrupted/reordered bytes for size {packageSize}");
        }
        Feed(0);
        foreach (int requested in chunks)
        {
            Feed(Math.Min(requested, source.Length - position));
            Feed(0); // Empty input must preserve any pending fragment.
        }
        Feed(source.Length - position);
        Feed(0);
        if (packageSize > 1)
        {
            byte[] last = [173];
            processor.Process(last, cross, borrowed);
            Assert(got.Count == source.Length + 1, "Final carry was not retained");
            Assert(CollectionsMarshal.AsSpan(got)[..source.Length].SequenceEqual(source), "Tail corrupted");
            Assert(got[^1] == last[0], "Tail completion corrupted");
        }
    }

    static void InvalidSizes()
    {
        foreach (int size in new[] { 0, -1, int.MinValue })
        {
            try { _ = new DHMPFixedStreamProcessor(size); }
            catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "packageSize") { continue; }
            throw new Exception("Invalid package size was accepted");
        }
    }

    static void BorrowingAndBatching()
    {
        byte[] source = Data(12);
        var processor = new DHMPFixedStreamProcessor(4);
        int calls = 0;
        processor.Process(source,
            _ => throw new Exception("Aligned input used carry"),
            span =>
            {
                calls++;
                Assert(span.Length == source.Length, "Complete run was split");
                Assert(Unsafe.AreSame(ref MemoryMarshal.GetReference(span), ref source[0]),
                    "Complete input was copied instead of borrowed");
            });
        Assert(calls == 1, "Unexpected number of batch callbacks");
    }

    static void IndependentStreams()
    {
        var first = new DHMPFixedStreamProcessor(3);
        var second = new DHMPFixedStreamProcessor(3);
        var a = new List<byte>();
        var b = new List<byte>();
        Action<ReadOnlySpan<byte>> publishA = s => a.AddRange(s.ToArray());
        Action<ReadOnlySpan<byte>> publishB = s => b.AddRange(s.ToArray());
        first.Process(new byte[] { 1 }, publishA, publishA);
        second.Process(new byte[] { 7, 8 }, publishB, publishB);
        first.Process(new byte[] { 2, 3 }, publishA, publishA);
        second.Process(new byte[] { 9 }, publishB, publishB);
        Assert(a.SequenceEqual(new byte[] { 1, 2, 3 }) && b.SequenceEqual(new byte[] { 7, 8, 9 }),
            "Independent streams interfered");
    }

    static void CallbackFailures()
    {
        foreach (bool crossing in new[] { false, true })
        {
            var processor = new DHMPFixedStreamProcessor(4);
            var failure = new InvalidOperationException("Consumer failed");
            int crossCalls = 0, borrowedCalls = 0;
            Action<ReadOnlySpan<byte>> cross = _ => { crossCalls++; throw failure; };
            Action<ReadOnlySpan<byte>> borrowed = _ => { borrowedCalls++; throw failure; };
            if (crossing) processor.Process(new byte[] { 1 }, cross, borrowed);
            bool propagated = false;
            try { processor.Process(Data(crossing ? 11 : 12), cross, borrowed); }
            catch (InvalidOperationException ex) when (ReferenceEquals(ex, failure)) { propagated = true; }
            Assert(propagated, "Callback exception was swallowed/replaced");
            Assert(crossCalls == (crossing ? 1 : 0) && borrowedCalls == (crossing ? 0 : 1),
                "Publication continued/retried after callback failure");
            // Discard this instance: recovery/reuse is explicitly outside the contract.
        }
    }

    static void TypedBoundary()
    {
        int models = 0;
        var processor = new DHMPFixedStreamProcessor(32);
        var data = Data(32 * 4);
        Action<ReadOnlySpan<byte>> consume = s =>
            DHMPModelBoundary.Consume<M32>(s, 32, m => models += m.Length);
        processor.Process(data.AsSpan(0, 31), consume, consume);
        processor.Process(data.AsSpan(31), consume, consume);
        Assert(models == 4, "Typed boundary model count changed");
    }

    static void NoProcessingAllocations()
    {
        foreach (int size in new[] { 31, 32 })
        {
            var processor = new DHMPFixedStreamProcessor(size);
            var data = Data(size * 8);
            Action<ReadOnlySpan<byte>> consume = s => sink += s.Length;
            void Batch()
            {
                processor.Process(data.AsSpan(0, size - 1), consume, consume);
                processor.Process(data.AsSpan(size - 1), consume, consume);
            }
            for (int i = 0; i < 2000; i++) Batch();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++) Batch();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert(allocated == 0, $"Process allocated {allocated} bytes for size {size}");
        }
    }

    static void Main()
    {
        InvalidSizes();
        foreach (int size in new[] { 1, 2, 3, 7, 31, 32, 33, 64, 255, 256, 257 })
        {
            Check(size, [size * 3]);
            Check(size, Enumerable.Repeat(1, size * 43).ToArray());
            for (int split = 0; split <= size; split++) Check(size, [split, size - split, size + 1]);
            var random = new Random(827 + size);
            Check(size, Enumerable.Range(0, 100).Select(_ => random.Next(0, size * 4 + 1)).ToArray());
            Check(size, [4093, 8191, 12287, 16381]);
        }
        BorrowingAndBatching();
        IndependentStreams();
        CallbackFailures();
        TypedBoundary();
        NoProcessingAllocations();
        Console.WriteLine("PASS: package sizes, all split positions, random fragments, empty input, tail retention,");
        Console.WriteLine("      byte identity/order, borrowing/batching, independent streams, callback failures,");
        Console.WriteLine("      typed boundary and zero processing allocations after warm-up.");
    }
}
