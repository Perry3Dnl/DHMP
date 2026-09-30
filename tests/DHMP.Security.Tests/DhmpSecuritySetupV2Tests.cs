using System.Buffers.Binary;
using System.Security.Cryptography;
using DHMP.Security;
using Xunit;

namespace DHMP.Security.Tests;

public sealed class DhmpSecuritySetupV2Tests
{
    private static readonly Guid Initiator = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Responder = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");

    [Theory]
    [InlineData(DhmpSecuritySetupType.Offer)]
    [InlineData(DhmpSecuritySetupType.Challenge)]
    [InlineData(DhmpSecuritySetupType.Confirm)]
    [InlineData(DhmpSecuritySetupType.Accept)]
    public void SetupV2_RoundTripsAndHasExplicitWireVersion(DhmpSecuritySetupType type)
    {
        using var key = Key();
        var message = new DhmpSecuritySetupMessage(type, Initiator,
            type == DhmpSecuritySetupType.Offer ? Guid.Empty : Responder, 7, 42);
        byte[] packet = Encode(message, key);
        Assert.Equal(64, packet.Length);
        Assert.Equal((byte)2, packet[4]);
        Assert.Equal((byte)type, packet[5]);
        Assert.Equal(Convert.FromHexString("00112233445566778899AABBCCDDEEFF"), packet[8..24]);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(40, 4)));
        Assert.True(DhmpSecuritySetupCodec.TryDecode(packet, key, out var decoded));
        Assert.Equal(message, decoded);
    }

    [Fact]
    public void Challenge_ConformsToIndependentHmacVector()
    {
        using var key = Key();
        Assert.Equal(Convert.FromHexString(
            "44484d530202010000112233445566778899aabbccddeeffffeeddccbbaa99887766554433221100000000070000002ad3706e9b78b650c4319b635047caeb7b"),
            Encode(Challenge(), key));
    }

    [Fact]
    public void EveryModifiedByte_IsRejectedWithoutProducingTranscript()
    {
        using var key = Key();
        byte[] original = Encode(Challenge(), key);
        for (int offset = 0; offset < original.Length; offset++)
        {
            byte[] modified = original.ToArray();
            modified[offset] ^= 1;
            Assert.False(DhmpSecuritySetupCodec.TryDecode(modified, key, out var message));
            Assert.Equal(default, message);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(47)]
    [InlineData(63)]
    [InlineData(65)]
    public void WrongPacketLength_IsRejected(int length)
    {
        using var key = Key();
        Assert.False(DhmpSecuritySetupCodec.TryDecode(new byte[length], key, out _));
    }

    [Fact]
    public void WrongPskOrKeyId_IsRejected()
    {
        using var key = Key();
        using var wrongKey = new DhmpPreSharedKey(7, Enumerable.Repeat((byte)1, 32).ToArray());
        using var wrongId = new DhmpPreSharedKey(8, new byte[32]);
        byte[] packet = Encode(Challenge(), key);
        Assert.False(DhmpSecuritySetupCodec.TryDecode(packet, wrongKey, out _));
        Assert.False(DhmpSecuritySetupCodec.TryDecode(packet, wrongId, out _));
        Assert.Throws<ArgumentException>(() => DhmpSecuritySetupCodec.Encode(Challenge(), wrongId, packet));
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(5, 0)]
    [InlineData(5, 255)]
    [InlineData(6, 2)]
    [InlineData(7, 1)]
    [InlineData(5, 1)]
    public void AuthenticatedInvalidFields_FailClosed(int offset, int value)
    {
        using var key = Key();
        byte[] packet = Encode(Challenge(), key);
        packet[offset] = (byte)value;
        byte[] tag = HMACSHA256.HashData(new byte[32], packet[..DhmpSecuritySetupCodec.BodySize]);
        tag.AsSpan(0, DhmpSecuritySetupCodec.TagSize).CopyTo(packet.AsSpan(DhmpSecuritySetupCodec.BodySize));
        Assert.False(DhmpSecuritySetupCodec.TryDecode(packet, key, out _));
    }

    [Fact]
    public void V1Packets_AreNotAcceptedAsV2OrAutomaticallyDowngraded()
    {
        using var key = Key();
        byte[] legacy = new byte[DhmpSecurityControlCodec.PacketSize];
        DhmpSecurityControlCodec.Encode(new DhmpSecurityControlMessage(DhmpSecurityControlType.Offer,
            DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256, Initiator, 7, 42), key, legacy);
        Assert.False(DhmpSecuritySetupCodec.TryDecode(legacy, key, out _));
        Assert.False(DhmpSecurityControlCodec.TryDecode(Encode(Challenge(), key), key, out _));
    }

    [Fact]
    public void SessionIdentity_BindsBothNoncesAndPublicContract()
    {
        var challenge = Challenge();
        Guid identity = DhmpSecuritySetupCodec.DeriveSessionId(challenge);
        Assert.Equal(Guid.Parse("b20c9b03-7c97-2851-6f58-7bad66f082a0"), identity);
        Assert.Equal(identity, DhmpSecuritySetupCodec.DeriveSessionId(challenge.WithType(DhmpSecuritySetupType.Confirm)));
        Assert.Equal(identity, DhmpSecuritySetupCodec.DeriveSessionId(challenge.WithType(DhmpSecuritySetupType.Accept)));
        foreach (var changed in new[]
        {
            new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Challenge, Guid.NewGuid(), Responder, 7, 42),
            new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Challenge, Initiator, Guid.NewGuid(), 7, 42),
            new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Challenge, Initiator, Responder, 8, 42),
            new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Challenge, Initiator, Responder, 7, 43)
        }) Assert.NotEqual(identity, DhmpSecuritySetupCodec.DeriveSessionId(changed));
        Assert.Throws<ArgumentException>(() => DhmpSecuritySetupCodec.DeriveSessionId(
            new DhmpSecuritySetupMessage(DhmpSecuritySetupType.Offer, Initiator, Guid.Empty, 7, 42)));
    }

    [Fact]
    public void InvalidDefaultAndNonceLayouts_CannotBeEncoded()
    {
        using var key = Key();
        Assert.Throws<ArgumentOutOfRangeException>(() => DhmpSecuritySetupCodec.Encode(default, key, new byte[64]));
        Assert.Throws<ArgumentException>(() => new DhmpSecuritySetupMessage(
            DhmpSecuritySetupType.Challenge, Initiator, Guid.Empty, 7, 42));
        Assert.Throws<ArgumentException>(() => new DhmpSecuritySetupMessage(
            DhmpSecuritySetupType.Offer, Initiator, Responder, 7, 42));
        Assert.Throws<ArgumentException>(() => new DhmpSecuritySetupMessage(
            DhmpSecuritySetupType.Offer, Guid.Empty, Guid.Empty, 7, 42));
        Assert.Throws<ArgumentException>(() => new DhmpSecuritySetupMessage(
            DhmpSecuritySetupType.Offer, Initiator, Guid.Empty, 0, 42));
    }

    private static DhmpPreSharedKey Key() => new(7, new byte[32]);
    private static DhmpSecuritySetupMessage Challenge()
        => new(DhmpSecuritySetupType.Challenge, Initiator, Responder, 7, 42);
    private static byte[] Encode(DhmpSecuritySetupMessage message, DhmpPreSharedKey key)
    {
        byte[] packet = new byte[DhmpSecuritySetupCodec.PacketSize];
        DhmpSecuritySetupCodec.Encode(message, key, packet);
        return packet;
    }
}
