using System.Buffers.Binary;

namespace DHMP.Protocol;

/// <summary>
/// Ultra-small pre-handshake DHMP reachability probe.
/// A valid Poke is echoed byte-for-byte by the configured peer.
/// </summary>
public static class DhmpPokeCodec
{
    public const byte CurrentVersion = 1;
    public const int MinimumPacketSize = 16;
    public const int FullEchoPacketSize = 1200;
    public const int MaximumPacketSize = FullEchoPacketSize;

    private static ReadOnlySpan<byte> Magic => "DHPK"u8;

    public static void Encode(
        ulong token,
        Span<byte> destination)
    {
        if (token == 0)
            throw new ArgumentOutOfRangeException(nameof(token));

        if (destination.Length < MinimumPacketSize ||
            destination.Length > MaximumPacketSize)
            throw new ArgumentOutOfRangeException(
                nameof(destination),
                $"DHMP Poke packets must be between {MinimumPacketSize} and {MaximumPacketSize} bytes.");

        destination.Clear();
        Magic.CopyTo(destination);
        destination[4] = CurrentVersion;

        BinaryPrimitives.WriteUInt64BigEndian(
            destination.Slice(8, sizeof(ulong)),
            token);
    }

    public static bool TryReadToken(
        ReadOnlySpan<byte> packet,
        out ulong token)
    {
        token = 0;

        if (packet.Length < MinimumPacketSize ||
            packet.Length > MaximumPacketSize ||
            !packet[..4].SequenceEqual(Magic) ||
            packet[4] != CurrentVersion ||
            packet[5] != 0 ||
            packet[6] != 0 ||
            packet[7] != 0)
            return false;

        token =
            BinaryPrimitives.ReadUInt64BigEndian(
                packet.Slice(8, sizeof(ulong)));

        return token != 0;
    }
}
