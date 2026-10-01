using DHMP.RawIpv6;
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
    public void NativeBackendRemainsExplicitlyLinuxOnly()
    {
        if (OperatingSystem.IsLinux()) return; // Linux socket success/ownership is exercised by the privileged lab.
        Assert.Throws<PlatformNotSupportedException>(() => DhmpLinuxRawIpv6Socket.Open(253));
    }
}
