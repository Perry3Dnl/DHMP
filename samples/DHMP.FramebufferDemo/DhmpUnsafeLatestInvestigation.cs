using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.Server;

// Research only. These local hot-buffer measurements exclude sockets and wire I/O.
internal static class DhmpUnsafeLatestInvestigation
{
    public static void Run(bool disassemblyOnly = false)
    {
        var rows = new List<object>();
        foreach (int size in new[] { 16, 1408, 65520 })
        {
            var wire = new DhmpWireContract(size);
            var policy = new DhmpSendPolicy(long.MaxValue, 65520, DhmpRatePolicy.Unlimited);
            var server = new DhmpServer(wire, new DhmpReceivePolicy(DhmpProcessingMode.UnsafeLatest, 65520));
            byte[] source = new byte[size];
            source.AsSpan().Fill(0x5a);
            Memory<byte> slot = server.GetUnsafeLatestReceiveMemoryUnchecked();
            long observed = 0;
            Action<ReadOnlySpan<byte>> publish = span => observed += span[0];
            var senderType = typeof(DhmpFullReportLab).GetNestedType("AggregateReportSender", BindingFlags.NonPublic)!;
            var sender = (IDhmpPacketSender)Activator.CreateInstance(senderType, server, publish, DhmpStressConfirmationMode.None)!;
            var client = new DhmpClient(sender, wire, policy);
            var candidate = new UnlimitedProbe(sender, wire, policy);
            var actions = new (string Name, Action Action)[] {
                ("loop/delegate control", static () => { }),
                ("publication only", () => server.PublishUnsafeLatestReceiveUnchecked(publish)),
                ("copy only", () => source.AsSpan().CopyTo(slot.Span)),
                ("copy + publication", () => { source.AsSpan().CopyTo(slot.Span); server.PublishUnsafeLatestReceiveUnchecked(publish); }),
                ("report sender", () => sender.SendPacketAsync(source).GetAwaiter().GetResult()),
                ("current client + same sender", () => client.SendAsync(source).GetAwaiter().GetResult()),
                ("candidate client + same sender", () => candidate.UnlimitedCandidate(source).GetAwaiter().GetResult())
            };
            // Confirm data reaches the actual server slot before measuring it.
            sender.SendPacketAsync(source).GetAwaiter().GetResult();
            if (!slot.Span.SequenceEqual(source)) throw new InvalidOperationException("Copy mismatch.");
            if (disassemblyOnly)
            {
                foreach (var action in actions)
                    for (int i = 0; i < 10000; i++) action.Action();
                continue;
            }
            foreach (var action in actions)
            {
                long start = Stopwatch.GetTimestamp();
                do { for (int i = 0; i < 10000; i++) action.Action(); }
                while (Stopwatch.GetElapsedTime(start).TotalSeconds < 0.1);
            }
            // Interleave and reverse the order; do not collect all samples of one case consecutively.
            var samples = actions.Select(_ => new List<double>()).ToArray();
            for (int round = 0; round < 5; round++)
            {
                for (int step = 0; step < actions.Length; step++)
                {
                    int index = round % 2 == 0 ? step : actions.Length - 1 - step;
                    samples[index].Add(Measure(actions[index].Action));
                }
            }
            for (int i = 0; i < actions.Length; i++)
            {
                double[] values = samples[i].Order().ToArray();
                rows.Add(new { size, stage = actions[i].Name, medianNs = values[2], minNs = values[0], maxNs = values[^1], samplesNs = samples[i] });
            }
            GC.KeepAlive(observed);
        }
        if (!disassemblyOnly)
        {
            var report = new { runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                logicalProcessors = Environment.ProcessorCount, tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
                scope = "Local hot buffers; five interleaved >=100 ms samples. Component timings are not additive. Candidate is an Unlimited-only research prototype, not a production replacement. No socket or physical-network measurement.", rows };
            Console.WriteLine("INVESTIGATION_JSON=" + JsonSerializer.Serialize(report));
        }
    }

    private static double Measure(Action action)
    {
        const int group = 10000;
        long count = 0;
        long start = Stopwatch.GetTimestamp();
        double seconds;
        do
        {
            for (int i = 0; i < group; i++) action();
            count += group;
            seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        } while (seconds < 0.1);
        return seconds * 1e9 / count;
    }

    private sealed class UnlimitedProbe
    {
        private readonly IDhmpPacketSender _sender;
        private readonly DhmpWireContract _wire;
        private readonly DhmpSendPolicy _policy;
        public UnlimitedProbe(IDhmpPacketSender sender, DhmpWireContract wire, DhmpSendPolicy policy)
        {
            if (policy.RatePolicy != DhmpRatePolicy.Unlimited) throw new ArgumentException("Unlimited only.");
            _sender = sender; _wire = wire; _policy = policy;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ValueTask UnlimitedCandidate(ReadOnlyMemory<byte> record, CancellationToken token = default)
        {
            // Same success-path validation as DhmpClient.SendAsync; faults remain unmeasured.
            // Before shipping, exception/cancellation completion behavior also needs compatibility tests.
            token.ThrowIfCancellationRequested();
            int size = _wire.RecordSize;
            if (record.Length != size) throw new DhmpProtocolException("Expected one negotiated record.");
            int maximum = _sender.MaximumPayloadBytes;
            if (_sender is IDhmpDynamicPacketSender dynamicSender)
                maximum = Math.Min(maximum, dynamicSender.CurrentMaximumPayloadBytes);
            maximum = Math.Min(maximum, _policy.MaximumPayloadBytes);
            if (maximum < size) throw new DhmpProtocolException("Current path cannot fit one record.");
            return _sender.SendPacketAsync(record, token);
        }
    }
}
