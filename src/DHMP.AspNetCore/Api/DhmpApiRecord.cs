using System.Buffers.Binary;

namespace DHMP.AspNetCore;

// DAPI/1 is an opt-in application record schema, not DHMP V1 packet metadata.
internal static class DhmpApiRecord
{
    internal const int Size = 1200;
    internal const int Header = 32;
    internal const int ContentSize = Size - Header;
    internal const byte Request = 1, Response = 2;
    internal readonly record struct Fragment(byte Kind, Guid Id, int Total, int Index, int Count);

    internal static Guid RequestId(ulong sequence)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        BinaryPrimitives.WriteUInt64BigEndian(bytes, sequence);
        return new Guid(bytes, bigEndian: true);
    }
    internal static ulong Sequence(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    internal static byte[] Encode(byte kind, Guid id, ReadOnlySpan<byte> message, int index)
    {
        int count = (message.Length + ContentSize - 1) / ContentSize;
        if (message.IsEmpty || id == Guid.Empty || kind is not (Request or Response) || index < 0 || index >= count || count > ushort.MaxValue)
            throw new ArgumentException("Invalid API application fragment.");
        byte[] record = new byte[Size];
        "DAPI"u8.CopyTo(record);
        record[4] = 1; record[5] = kind;
        id.TryWriteBytes(record.AsSpan(8, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(24, 4), message.Length);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(28, 2), (ushort)index);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(30, 2), (ushort)count);
        message.Slice(index * ContentSize, Math.Min(ContentSize, message.Length - index * ContentSize)).CopyTo(record.AsSpan(Header));
        return record;
    }

    internal static bool TryParse(ReadOnlySpan<byte> record, int maximum, out Fragment fragment)
    {
        fragment = default;
        if (record.Length != Size || !record[..4].SequenceEqual("DAPI"u8) || record[4] != 1 ||
            record[5] is not (Request or Response) || record[6] != 0 || record[7] != 0) return false;
        var id = new Guid(record.Slice(8, 16), bigEndian: true);
        int total = BinaryPrimitives.ReadInt32BigEndian(record.Slice(24, 4));
        int index = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(28, 2));
        int count = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(30, 2));
        if (id == Guid.Empty || BinaryPrimitives.ReadUInt64BigEndian(record.Slice(16, 8)) != 0 || total <= 0 || total > maximum || count != (total + ContentSize - 1) / ContentSize || index >= count) return false;
        int bytes = Math.Min(ContentSize, total - index * ContentSize);
        foreach (byte padding in record[(Header + bytes)..]) if (padding != 0) return false;
        fragment = new(record[5], id, total, index, count);
        return true;
    }
}
