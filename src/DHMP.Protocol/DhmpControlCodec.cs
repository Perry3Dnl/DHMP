using System.Buffers.Binary;

namespace DHMP.Protocol;

/// <summary>
/// Encodes and decodes the fixed 32-byte DHMP V1 control packet.
/// </summary>
public static class DhmpControlCodec
{
    private static ReadOnlySpan<byte> Magic => "DHMC"u8;

    public static void Encode(
        DhmpControlMessage message,
        Span<byte> destination)
    {
        message.Validate();

        if (destination.Length < DhmpProtocol.ControlPacketSize)
            throw new ArgumentException(
                $"DHMP control packets require {DhmpProtocol.ControlPacketSize} bytes.",
                nameof(destination));

        destination = destination[..DhmpProtocol.ControlPacketSize];
        destination.Clear();

        Magic.CopyTo(destination);
        destination[4] = DhmpProtocol.ControlVersion;
        destination[5] = (byte)message.Type;
        destination[6] = message.DataWireVersion;
        destination[7] = (byte)message.RejectReason;

        BinaryPrimitives.WriteUInt16BigEndian(
            destination.Slice(8, 2),
            checked((ushort)message.RecordSize));

        BinaryPrimitives.WriteUInt16BigEndian(
            destination.Slice(10, 2),
            checked((ushort)message.MaximumReceivePayloadBytes));

        if (!message.SchemaId.TryWriteBytes(
                destination.Slice(12, 16),
                bigEndian: true,
                out int schemaBytes) ||
            schemaBytes != 16)
            throw new InvalidOperationException("Could not encode DHMP schema identifier.");

        BinaryPrimitives.WriteUInt32BigEndian(
            destination.Slice(28, 4),
            message.CorrelationId);
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> packet,
        out DhmpControlMessage message)
    {
        message = default;

        if (packet.Length != DhmpProtocol.ControlPacketSize ||
            !packet[..4].SequenceEqual(Magic) ||
            packet[4] != DhmpProtocol.ControlVersion)
            return false;

        var type = (DhmpControlMessageType)packet[5];
        var reason = (DhmpControlRejectReason)packet[7];

        if (type is not DhmpControlMessageType.Hello and
            not DhmpControlMessageType.Accept and
            not DhmpControlMessageType.Reject)
            return false;

        if (!Enum.IsDefined(reason))
            return false;

        byte dataVersion = packet[6];
        int recordSize =
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(8, 2));
        int maximumReceivePayload =
            BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(10, 2));
        var schemaId = new Guid(packet.Slice(12, 16), bigEndian: true);
        uint correlationId =
            BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(28, 4));

        try
        {
            message = new DhmpControlMessage(
                type,
                dataVersion,
                recordSize,
                maximumReceivePayload,
                schemaId,
                correlationId,
                reason);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
