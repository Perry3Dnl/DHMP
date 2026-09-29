using System.Net;
using DHMP.Protocol;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpRawIpv6OptionsTests
{
    [Fact]
    public void HappyPath_NativeIpv6PeerBinding_IsAccepted()
    {
        var options = new DhmpRawIpv6Options(
            IPAddress.IPv6Loopback,
            IPAddress.Parse("2001:db8::2"),
            1408);

        Assert.Equal(IPAddress.IPv6Loopback, options.LocalAddress);
        Assert.Equal(IPAddress.Parse("2001:db8::2"), options.RemoteAddress);
        Assert.Equal(1408, options.MaximumPayloadBytes);
        Assert.Equal(DhmpProtocol.ExperimentalIpv6NextHeader, options.ProtocolNumber);
        Assert.True(options.SocketBufferBytes >= options.MaximumPayloadBytes);
    }

    [Fact]
    public void BlockedPath_Ipv4Addresses_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new DhmpRawIpv6Options(IPAddress.Loopback, IPAddress.IPv6Loopback, 1408));

        Assert.Throws<ArgumentException>(() =>
            new DhmpRawIpv6Options(IPAddress.IPv6Loopback, IPAddress.Loopback, 1408));
    }

    [Fact]
    public void BlockedPath_Ipv4MappedAddresses_AreRejected()
    {
        var mapped = IPAddress.Parse("::ffff:127.0.0.1");

        Assert.Throws<ArgumentException>(() =>
            new DhmpRawIpv6Options(mapped, IPAddress.IPv6Loopback, 1408));

        Assert.Throws<ArgumentException>(() =>
            new DhmpRawIpv6Options(IPAddress.IPv6Loopback, mapped, 1408));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void BlockedPath_InvalidPayloadLimit_IsRejected(int payloadBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpRawIpv6Options(
                IPAddress.IPv6Loopback,
                IPAddress.IPv6Loopback,
                payloadBytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public void BlockedPath_ReservedProtocolBinding_IsRejected(int protocolNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpRawIpv6Options(
                IPAddress.IPv6Loopback,
                IPAddress.IPv6Loopback,
                1408,
                (byte)protocolNumber));
    }

    [Fact]
    public void BlockedPath_UndersizedSocketBuffer_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpRawIpv6Options(
                IPAddress.IPv6Loopback,
                IPAddress.IPv6Loopback,
                maximumPayloadBytes: 1408,
                socketBufferBytes: 1024));
    }
}
