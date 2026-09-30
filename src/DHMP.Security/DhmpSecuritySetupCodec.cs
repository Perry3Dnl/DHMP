using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DHMP.Security;

/// <summary>64-byte DHMS V2 control packet with an authenticated responder freshness challenge.</summary>
public static class DhmpSecuritySetupCodec
{
    public const byte CurrentVersion = 2;
    public const int BodySize = 48;
    public const int TagSize = 16;
    public const int PacketSize = BodySize + TagSize;

    public static void Encode(DhmpSecuritySetupMessage message, DhmpPreSharedKey key, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(key);
        message.Validate();
        if (message.KeyId != key.KeyId)
            throw new ArgumentException("Setup key ID does not match the PSK.", nameof(message));
        if (destination.Length < PacketSize)
            throw new ArgumentException("PSK setup V2 requires 64 bytes.", nameof(destination));

        WriteBody(message, destination[..BodySize]);
        Span<byte> tag = stackalloc byte[32];
        HMACSHA256.HashData(key.KeySpan, destination[..BodySize], tag);
        tag[..TagSize].CopyTo(destination.Slice(BodySize, TagSize));
        CryptographicOperations.ZeroMemory(tag);
    }

    public static bool TryDecode(ReadOnlySpan<byte> packet, DhmpPreSharedKey key,
        out DhmpSecuritySetupMessage message)
    {
        ArgumentNullException.ThrowIfNull(key);
        message = default;
        if (packet.Length != PacketSize || !packet[..4].SequenceEqual("DHMS"u8) ||
            packet[4] != CurrentVersion ||
            packet[6] != (byte)DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256 || packet[7] != 0 ||
            BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(40, 4)) != key.KeyId)
            return false;

        Span<byte> tag = stackalloc byte[32];
        HMACSHA256.HashData(key.KeySpan, packet[..BodySize], tag);
        bool valid = CryptographicOperations.FixedTimeEquals(tag[..TagSize], packet[BodySize..]);
        CryptographicOperations.ZeroMemory(tag);
        if (!valid) return false;

        try
        {
            message = new DhmpSecuritySetupMessage((DhmpSecuritySetupType)packet[5],
                new Guid(packet.Slice(8, 16), bigEndian: true),
                new Guid(packet.Slice(24, 16), bigEndian: true), key.KeyId,
                BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(44, 4)));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Domain-separated ID used as the existing data profile's HKDF salt and AEAD context.</summary>
    public static Guid DeriveSessionId(DhmpSecuritySetupMessage transcript)
    {
        transcript.Validate();
        if (transcript.Type == DhmpSecuritySetupType.Offer)
            throw new ArgumentException("A responder challenge is required before deriving session identity.", nameof(transcript));

        ReadOnlySpan<byte> domain = "DHMP-PSK-SETUP-V2-ID"u8;
        Span<byte> input = stackalloc byte[domain.Length + BodySize];
        domain.CopyTo(input);
        // Canonical CHALLENGE body makes CONFIRM and ACCEPT derive the same transcript ID.
        WriteBody(transcript.WithType(DhmpSecuritySetupType.Challenge), input[domain.Length..]);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        Guid sessionId = new(digest[..16], bigEndian: true);
        CryptographicOperations.ZeroMemory(digest);
        if (sessionId == Guid.Empty)
            throw new DhmpSecurityException("Derived an unusable security session ID; start a fresh attempt.");
        return sessionId;
    }

    private static void WriteBody(DhmpSecuritySetupMessage message, Span<byte> body)
    {
        body.Clear();
        "DHMS"u8.CopyTo(body);
        body[4] = CurrentVersion;
        body[5] = (byte)message.Type;
        body[6] = (byte)DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256;
        message.InitiatorNonce.TryWriteBytes(body.Slice(8, 16), bigEndian: true, out _);
        message.ResponderNonce.TryWriteBytes(body.Slice(24, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(body.Slice(40, 4), message.KeyId);
        BinaryPrimitives.WriteUInt32BigEndian(body.Slice(44, 4), message.CorrelationId);
    }
}
