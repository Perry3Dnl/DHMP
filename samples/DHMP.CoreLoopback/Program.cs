using System.Buffers.Binary;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.Server;

const int RecordSize = 8;

var wire = new DhmpWireContract(RecordSize);
var receivePolicy = new DhmpReceivePolicy(
    DhmpProcessingMode.Sequential,
    maximumPayloadBytes: 64);
var server = new DhmpServer(wire, receivePolicy);

var sender = new LoopbackPacketSender(server, maximumPayloadBytes: 64);
var sendPolicy = new DhmpSendPolicy(
    pmax: 1000,
    maximumPayloadBytes: 64,
    ratePolicy: DhmpRatePolicy.SmoothPacing);
var client = new DhmpClient(sender, wire, sendPolicy);

byte[] packet = new byte[RecordSize * 2];
BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(0, RecordSize), 41);
BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(RecordSize, RecordSize), 42);

Console.WriteLine("Sending one DHMP packet containing two complete 8-byte application records.");

await client.SendBatchAsync(packet);

Console.WriteLine("Done. This sample uses an in-process sender; it does not claim network reachability.");

sealed class LoopbackPacketSender(
    DhmpServer server,
    int maximumPayloadBytes) : IDhmpPacketSender
{
    public int MaximumPayloadBytes { get; } = maximumPayloadBytes;

    public ValueTask SendPacketAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int recordSize = server.WireContract.RecordSize;

        server.ProcessPacket(
            payload.Span,
            batch =>
            {
                for (int offset = 0; offset < batch.Length; offset += recordSize)
                {
                    ulong value = BinaryPrimitives.ReadUInt64BigEndian(
                        batch.Slice(offset, recordSize));

                    Console.WriteLine($"Received record: {value}");
                }
            });

        return ValueTask.CompletedTask;
    }
}
