using System.Reflection;
using System.Text.Json;
using DHMP.Protocol;
using DHMP.Server;

var assembly = Assembly.Load("DHMP.FramebufferDemo");
var lab = assembly.GetType("DhmpFullReportLab", throwOnError: true)!;
var run = lab.GetMethod("RunAggregateTimingBenchmark", BindingFlags.NonPublic | BindingFlags.Static)!;
var confirmationType = run.GetParameters()[4].ParameterType;
var senderType = lab.GetNestedType("AggregateReportSender", BindingFlags.NonPublic)!;
int rows = 0;
foreach (int bytes in new[] { 16, 1408, 65520 })
foreach (var (mode, smoothing) in new[] {
    (DhmpProcessingMode.Sequential, false), (DhmpProcessingMode.UnsafeSequential, false),
    (DhmpProcessingMode.UnsafeLatest, false), (DhmpProcessingMode.Latest, false),
    (DhmpProcessingMode.Latest, true) })
{
    object? sharedDirect = null;
    foreach (var rate in Enum.GetValues<DhmpRatePolicy>())
    foreach (object confirmation in Enum.GetValues(confirmationType))
    {
        // Verify every byte reaches the publisher, including the slot-copy path.
        var server = new DhmpServer(new DhmpWireContract(bytes),
            new DhmpReceivePolicy(mode, 65520, smoothing, sequentialBacklogCapacityRecords: 64));
        byte[] input = new byte[bytes];
        for (int i = 0; i < input.Length; i++) input[i] = (byte)i;
        bool published = false;
        Action<ReadOnlySpan<byte>> observe = span => {
            Require(span.SequenceEqual(input), "published bytes changed");
            published = true;
        };
        var sender = (IDhmpPacketSender)Activator.CreateInstance(senderType, server, observe, confirmation)!;
        sender.SendPacketAsync(input).GetAwaiter().GetResult();
        Require(published, "record was not published");
        input.AsSpan().Fill(0xa7);
        sender.SendPacketAsync(input).GetAwaiter().GetResult();

        var result = run.Invoke(null, new object?[] { bytes, mode, smoothing, rate, confirmation, sharedDirect })!;
        sharedDirect = result.GetType().GetProperty("DirectReceive")!.GetValue(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var root = json.RootElement;
        Require(root.GetProperty("RatePolicy").GetString() == rate.ToString(), "wrong policy");
        Require(root.GetProperty("ConfirmationMode").GetString() == confirmation.ToString(), "wrong confirmation");
        int returnBytes = confirmation.ToString() switch { "None" => 0, "ApplicationId" => 16, _ => bytes };
        Require(root.GetProperty("ReturnBytesPerForwardPacket").GetInt32() == returnBytes, "wrong return size");
        long perPass = root.GetProperty("PacketsPerPass").GetInt64();
        Require(perPass * bytes >= 10_000_000, "pass is shorter than 10 MB");
        foreach (string path in new[] { "DirectReceive", "FullClient" })
        {
            var total = root.GetProperty(path);
            var samples = total.GetProperty("Samples");
            Require(samples.GetArrayLength() == 3, "expected three samples");
            long packets = 0;
            double ms = 0;
            foreach (var sample in samples.EnumerateArray())
            {
                long count = sample.GetProperty("TotalPackets").GetInt64();
                double elapsed = sample.GetProperty("ElapsedMilliseconds").GetDouble();
                Require(elapsed >= 100, "sample shorter than 100 ms");
                Require(count % perPass == 0, "partial pass counted");
                Require(sample.GetProperty("TotalLogicalBytes").GetInt64() == count * bytes, "wrong byte count");
                double ns = sample.GetProperty("NanosecondsPerPacket").GetDouble();
                Require(Math.Abs(ns - elapsed * 1e6 / count) < 1e-6, "wrong ns calculation");
                packets += count;
                ms += elapsed;
            }
            Require(total.GetProperty("TotalPackets").GetInt64() == packets, "total differs from samples");
            Require(Math.Abs(total.GetProperty("NanosecondsPerPacket").GetDouble() - ms * 1e6 / packets) < 1e-6,
                "aggregate must use actual elapsed time");
        }
        rows++;
    }
}
Require(rows == 135, "missing mode/option combinations");
Console.WriteLine($"Passed {rows} aggregate combinations, byte publication, sample durations and timing arithmetic.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
