using DHMP.Protocol;
using Xunit;

namespace DHMP.Protocol.Tests;

public sealed class DhmpPokeCodecTests
{
    [Theory]
    [InlineData(DhmpPokeCodec.MinimumPacketSize)]
    [InlineData(DhmpPokeCodec.FullEchoPacketSize)]
    public void Poke_round_trips_token_without_mutating_payload_shape(
        int packetBytes)
    {
        const ulong token =
            0x0102030405060708UL;

        byte[] packet =
            new byte[packetBytes];

        DhmpPokeCodec.Encode(
            token,
            packet);

        Assert.True(
            DhmpPokeCodec.TryReadToken(
                packet,
                out ulong decoded));

        Assert.Equal(
            token,
            decoded);

        byte[] echo =
            packet.ToArray();

        Assert.Equal(
            packet,
            echo);
    }

    [Fact]
    public void Poke_rejects_zero_token_and_modified_prefix()
    {
        byte[] packet =
            new byte[
                DhmpPokeCodec.MinimumPacketSize];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DhmpPokeCodec.Encode(
                0,
                packet));

        DhmpPokeCodec.Encode(
            1,
            packet);

        packet[5] = 1;

        Assert.False(
            DhmpPokeCodec.TryReadToken(
                packet,
                out _));
    }

    [Fact]
    public void Poke_requires_fixed_minimum_prefix()
    {
        byte[] tooSmall =
            new byte[
                DhmpPokeCodec.MinimumPacketSize - 1];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => DhmpPokeCodec.Encode(
                1,
                tooSmall));

        Assert.False(
            DhmpPokeCodec.TryReadToken(
                tooSmall,
                out _));
    }
}
