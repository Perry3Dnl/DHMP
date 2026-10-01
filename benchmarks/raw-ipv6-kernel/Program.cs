using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DHMP.Protocol;
using DHMP.RawIpv6;

// DBEN benchmark application records contain a 32-bit record identity and a deterministic byte pattern.
// Every delivered byte and unique identity is checked; this is a kernel loopback measurement, not a NIC test.
const int Size = 32;
int messages = args.Length > 0 ? int.Parse(args[0]) : 200_000;
int batch = args.Length > 1 ? int.Parse(args[1]) : 44;
if (messages < 1 || messages > 5_000_000 || batch < 1 || batch * Size + 40 > 1500) throw new ArgumentOutOfRangeException(nameof(args));
using var rx = DhmpLinuxRawIpv6Socket.Open(253);
using var tx = DhmpLinuxRawIpv6Socket.Open(253);
var endpoint = new IPEndPoint(IPAddress.IPv6Loopback, 0);
rx.Bind(endpoint); rx.ReceiveBufferSize = 16 * 1024 * 1024; rx.ReceiveTimeout = 1000;
tx.SendBufferSize = 16 * 1024 * 1024;
byte[] send = new byte[batch * Size], receive = new byte[batch * Size + 40 + 256];
var seen = new BitArray(messages);
long unique = 0, duplicates = 0, reordered = 0, invalid = 0, offered = 0, lastArrival = 0;
int highest = -1;
var processor = new DhmpPacketProcessor(new(Size), new(DhmpProcessingMode.Sequential, batch * Size));
using var ready = new ManualResetEventSlim();
long started = Stopwatch.GetTimestamp();
var receiver = Task.Run(() =>
{
    ready.Set();
    while (true)
    {
        int count;
        try { count = rx.Receive(receive); }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.TimedOut)
        { if (Volatile.Read(ref offered) >= messages) break; continue; }
        ReadOnlySpan<byte> payload = receive.AsSpan(0, count);
        if (count >= 40 && count % Size == 40 % Size)
        {
            if (payload[0] >> 4 != 6 || payload[6] != 253 || BinaryPrimitives.ReadUInt16BigEndian(payload[4..6]) != count - 40) { invalid++; continue; }
            payload = payload[40..];
        }
        try
        {
            // Validate the complete packet's identity/pattern before accounting any record.
            new DhmpWireContract(Size).ValidatePacket(payload.Length, batch * Size);
            for (int offset = 0; offset < payload.Length; offset += Size)
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
                if (id < 0 || id >= messages) throw new DhmpProtocolException("Invalid benchmark identity.");
                for (int b = 4; b < Size; b++) if (payload[offset + b] != (byte)(id + b)) throw new DhmpProtocolException("Benchmark byte mismatch.");
            }
            processor.Process(payload, bytes =>
            {
                for (int offset = 0; offset < bytes.Length; offset += Size)
                {
                    int id = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));
                    if (seen[id]) { duplicates++; continue; }
                    seen[id] = true; unique++;
                    if (id < highest) reordered++; else highest = id;
                }
            });
            lastArrival = Stopwatch.GetTimestamp();
        }
        catch (DhmpProtocolException) { invalid++; }
        if (unique == messages) break;
    }
});
ready.Wait(); started = Stopwatch.GetTimestamp();
for (int id = 0; id < messages;)
{
    int n = Math.Min(batch, messages - id);
    for (int record = 0; record < n; record++)
    {
        int sequence = id + record;
        BinaryPrimitives.WriteInt32LittleEndian(send.AsSpan(record * Size, 4), sequence);
        for (int b = 4; b < Size; b++) send[record * Size + b] = (byte)(sequence + b);
    }
    tx.SendTo(send.AsSpan(0, n * Size), SocketFlags.None, endpoint);
    id += n; Volatile.Write(ref offered, id);
}
long sendFinished = Stopwatch.GetTimestamp();
await receiver;
double sendSeconds = (sendFinished - started) / (double)Stopwatch.Frequency;
double activeSeconds = (Math.Max(lastArrival, sendFinished) - started) / (double)Stopwatch.Frequency;
if (unique == 0 || invalid != 0) throw new InvalidDataException("Kernel benchmark did not deliver valid records.");
Console.WriteLine("RESULT_JSON " + JsonSerializer.Serialize(new
{
    name = $"kernel-loopback-batch-{batch}", scope = "raw-ipv6-kernel-loopback-unprotected-full-byte-validation",
    offered = messages, unique, duplicates, reordered, invalid, missing = messages - unique,
    loss_pct = 100.0 * (messages - unique) / messages,
    send_seconds = sendSeconds, active_seconds = activeSeconds,
    offered_GBps = messages * 32.0 / sendSeconds / 1e9,
    consumed_GBps = unique * 32.0 / activeSeconds / 1e9,
    operations_per_second = unique / activeSeconds,
    ns_per_operation = activeSeconds * 1e9 / unique,
    drain_wait_excluded = true
}));
