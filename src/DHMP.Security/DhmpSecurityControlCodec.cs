using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DHMP.Security;

/// <summary>
/// Fixed 48-byte PSK-authenticated security control packet:
/// 32-byte body plus a 16-byte truncated HMAC-SHA256 tag.
/// Historical V1 codec; it does not establish OFFER freshness. Use DhmpSecuritySetupCodec V2 for new setup.
/// </summary>
public static class DhmpSecurityControlCodec
{
    public const byte CurrentVersion = 1;
    public const int BodySize = 32;
    public const int TagSize = 16;
    public const int PacketSize = BodySize + TagSize;

    private static ReadOnlySpan<byte> Magic => "DHMS"u8;

    public static void Encode(
        DhmpSecurityControlMessage message,
        DhmpPreSharedKey key,
        Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (destination.Length < PacketSize)
            throw new ArgumentException(
                $"DHMP security control packets require {PacketSize} bytes.",
                nameof(destination));

        if (message.KeyId != key.KeyId)
            throw new ArgumentException(
                "Security message key ID does not match the supplied PSK.",
                nameof(message));

        destination = destination[..PacketSize];
        destination.Clear();

        Magic.CopyTo(destination);
        destination[4] = CurrentVersion;
        destination[5] = (byte)message.Type;
        destination[6] = (byte)message.Suite;
        destination[7] = (byte)message.RejectReason;

        if (!message.SessionId.TryWriteBytes(
                destination.Slice(8, 16),
                bigEndian: true,
                out int sessionBytes) ||
            sessionBytes != 16)
            throw new InvalidOperationException(
                "Could not encode DHMP security session ID.");

        BinaryPrimitives.WriteUInt32BigEndian(
            destination.Slice(24, 4),
            message.KeyId);

        BinaryPrimitives.WriteUInt32BigEndian(
            destination.Slice(28, 4),
            message.CorrelationId);

        Span<byte> fullTag = stackalloc byte[32];

        HMACSHA256.HashData(
            key.KeySpan,
            destination[..BodySize],
            fullTag);

        fullTag[..TagSize].CopyTo(
            destination.Slice(BodySize, TagSize));

        CryptographicOperations.ZeroMemory(fullTag);
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> packet,
        DhmpPreSharedKey key,
        out DhmpSecurityControlMessage message)
    {
        ArgumentNullException.ThrowIfNull(key);
        message = default;

        if (packet.Length != PacketSize ||
            !packet[..4].SequenceEqual(Magic) ||
            packet[4] != CurrentVersion)
            return false;

        uint keyId =
            BinaryPrimitives.ReadUInt32BigEndian(
                packet.Slice(24, 4));

        if (keyId != key.KeyId)
            return false;

        Span<byte> fullTag = stackalloc byte[32];

        HMACSHA256.HashData(
            key.KeySpan,
            packet[..BodySize],
            fullTag);

        bool tagMatches =
            CryptographicOperations.FixedTimeEquals(
                fullTag[..TagSize],
                packet.Slice(BodySize, TagSize));

        CryptographicOperations.ZeroMemory(fullTag);

        if (!tagMatches)
            return false;

        var type =
            (DhmpSecurityControlType)packet[5];
        var suite =
            (DhmpSecuritySuite)packet[6];
        var reason =
            (DhmpSecurityRejectReason)packet[7];

        try
        {
            message = new DhmpSecurityControlMessage(
                type,
                suite,
                new Guid(
                    packet.Slice(8, 16),
                    bigEndian: true),
                keyId,
                BinaryPrimitives.ReadUInt32BigEndian(
                    packet.Slice(28, 4)),
                reason);

            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
