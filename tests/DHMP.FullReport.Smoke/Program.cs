using System.Reflection;
using System.Text.Json;
using DHMP.Protocol;
using DHMP.Server;

var assembly = Assembly.Load("DHMP.FramebufferDemo");
if (args.Contains("--raw-sender"))
{
    var type = assembly.GetType("DhmpAfXdpLiveLab", throwOnError: true)!;
    object instance = Activator.CreateInstance(type)!;
    var transmit = type.GetMethod("RunRawIpv6Transmit", BindingFlags.NonPublic | BindingFlags.Instance)!;
    foreach (int bytes in new[] { 16, 1408 })
    {
        var task = (Task)transmit.Invoke(instance, new object[] { 0, bytes, 128L, CancellationToken.None })!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        if ((long)result.GetType().GetProperty("PacketsCompleted")!.GetValue(result)! != 128L ||
            (long)result.GetType().GetProperty("PayloadBytesCompleted")!.GetValue(result)! != 128L * bytes)
            throw new InvalidOperationException("Production sender benchmark counts changed.");
    }
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var blocked = (Task)transmit.Invoke(instance, new object[] { 0, 16, 128L, cancelled.Token })!;
    try
    {
        await blocked;
        throw new InvalidOperationException("Cancelled production sender benchmark completed.");
    }
    catch (OperationCanceledException) { }
    Console.WriteLine("Production raw sender benchmark smoke passed: scoped multicast, two sizes, counts and cancellation. No receiver/delivery measurement.");
    return;
}
if (args.Contains("--investigate"))
{
    var investigation = assembly.GetType("DhmpUnsafeLatestInvestigation", throwOnError: true)!;
    investigation.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null,
        new object[] { args.Contains("--disasm-only") });
    return;
}
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
