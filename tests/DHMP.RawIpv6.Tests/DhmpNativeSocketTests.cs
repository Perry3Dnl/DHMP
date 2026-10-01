using System.Net;
using DHMP.Protocol;
using DHMP.RawIpv6;
using DHMP.Server;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpNativeSocketTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(17)]
    [InlineData(255)]
    public void UnrelatedNativeProtocolsAreRejectedBeforeOpeningDescriptors(int protocol)
        => Assert.Throws<ArgumentOutOfRangeException>(() => DhmpLinuxRawIpv6Socket.Open((byte)protocol));

    [Fact]
    public void RawSenderRejectsImplicitExperimentalBindingBeforeOpeningSocket()
    {
        if (!OperatingSystem.IsLinux()) return;

        var options = new DhmpRawIpv6Options(
            IPAddress.IPv6Loopback,
            IPAddress.IPv6Loopback,
            128);

        var error = Assert.Throws<InvalidOperationException>(
            () => new DhmpRawIpv6PacketSender(options));

        Assert.Contains(
            "experimental Next Header values 253/254",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnprotectedReceiverRequiresExplicitAcceptanceBeforeSocketOpen()
    {
        if (!OperatingSystem.IsLinux()) return;

        var options = new DhmpRawIpv6Options(
            IPAddress.IPv6Loopback,
            IPAddress.IPv6Loopback,
            128,
            enableExperimentalProtocolNumbers: true);

        var server = new DhmpServer(
            new DhmpWireContract(4),
            new DhmpReceivePolicy(
                DhmpProcessingMode.Sequential,
                64));

        var error = Assert.Throws<InvalidOperationException>(
            () => new DhmpRawIpv6Receiver(options, server));

        Assert.Contains(
            "Unprotected DHMP receive is disabled by default",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeBackendRemainsExplicitlyLinuxOnly()
    {
        if (OperatingSystem.IsLinux()) return; // Linux socket success/ownership is exercised by the privileged lab.
        Assert.Throws<PlatformNotSupportedException>(() => DhmpLinuxRawIpv6Socket.Open(253));
    }
}
