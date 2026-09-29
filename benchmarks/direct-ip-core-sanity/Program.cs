using System.Buffers.Binary;
using System.Diagnostics;
using DHMP.Protocol;

const int RecordSize = 32;
int messages = args.Length > 0
    ? int.Parse(args[0])
    : 100_000_000;

int[] batches = [1, 4, 8, 16, 32, 44];

Console.WriteLine(
    $"DHMP_DIRECT_IP_CORE_V1 messages={messages} record_size={RecordSize}");
Console.WriteLine(
    "Current DhmpPacketProcessor ceiling only; no socket/kernel/NIC throughput claim.");

foreach (int batch in batches)
{
    Run(Math.Min(messages, 2_000_000), batch, print: false);
    Run(messages, batch, print: true);
}

static void Run(int messages, int batch, bool print)
{
    int packetBytes = checked(batch * RecordSize);
    var wire = new DhmpWireContract(RecordSize);
    var receive = new DhmpReceivePolicy(
        DhmpProcessingMode.Latest,
        packetBytes);
    var processor = new DhmpPacketProcessor(
        wire,
        receive);

    byte[] packet = new byte[packetBytes];
    BinaryPrimitives.WriteInt32LittleEndian(
        packet.AsSpan(packetBytes - RecordSize, 4),
        123456789);

    long guard = 0;
    long packets = 0;
    int remaining = messages;

    Action<ReadOnlySpan<byte>> publish =
        span =>
        {
            guard += BinaryPrimitives.ReadInt32LittleEndian(
                span[..4]);
        };

    long start = Stopwatch.GetTimestamp();

    while (remaining > 0)
    {
        int records = Math.Min(batch, remaining);
        int length = records * RecordSize;

        processor.Process(
            packet.AsSpan(0, length),
            publish);

        remaining -= records;
        packets++;
    }

    long ticks = Stopwatch.GetTimestamp() - start;
    GC.KeepAlive(guard);

    if (!print)
        return;

    double seconds =
        (double)ticks / Stopwatch.Frequency;

    long logicalBytes = (long)messages * RecordSize;

    Console.WriteLine(
        $"DIRECT_IP_CORE_CURRENT_V1 batch={batch} payload_bytes={packetBytes} " +
        $"messages={messages} packets={packets} wall_s={seconds:F6} " +
        $"logical_mps={messages / seconds / 1e6:F3} " +
        $"logical_GBps={logicalBytes / seconds / 1e9:F3} " +
        $"ns_msg={seconds * 1e9 / messages:F3} " +
        $"ns_packet={seconds * 1e9 / packets:F3} guard={guard}");
}
