using System.Net.Sockets;
using DHMP.Protocol;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpRawIpv6HostProbeTests
{
    [Fact]
    public void HappyFlow_OpensBothExperimentalBindingsAndReportsReady()
    {
        var openedProtocols =
            new List<byte>();

        DhmpRawIpv6HostProbeResult result =
            DhmpRawIpv6HostProbe.ProbeCore(
                isLinux: true,
                ipv6Available: true,
                protocol =>
                {
                    openedProtocols.Add(protocol);

                    return new Socket(
                        AddressFamily.InterNetwork,
                        SocketType.Dgram,
                        ProtocolType.Udp);
                });

        Assert.True(result.IsReady);
        Assert.Equal(
            DhmpRawIpv6HostProbeStatus.Ready,
            result.Status);
        Assert.Equal(
            new[]
            {
                DhmpProtocol.ExperimentalIpv6DataNextHeader,
                DhmpProtocol.ExperimentalIpv6ControlNextHeader
            },
            openedProtocols);
        Assert.Contains(
            "reachability are not tested",
            result.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedFlow_NonLinuxStopsBeforeOpeningSocket()
    {
        DhmpRawIpv6HostProbeResult result =
            DhmpRawIpv6HostProbe.ProbeCore(
                isLinux: false,
                ipv6Available: true,
                _ => throw new InvalidOperationException(
                    "socket must not be opened"));

        Assert.False(result.IsReady);
        Assert.Equal(
            DhmpRawIpv6HostProbeStatus.UnsupportedOperatingSystem,
            result.Status);
        Assert.Contains(
            "Linux-only",
            result.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedFlow_NoIpv6StopsBeforeOpeningSocket()
    {
        DhmpRawIpv6HostProbeResult result =
            DhmpRawIpv6HostProbe.ProbeCore(
                isLinux: true,
                ipv6Available: false,
                _ => throw new InvalidOperationException(
                    "socket must not be opened"));

        Assert.False(result.IsReady);
        Assert.Equal(
            DhmpRawIpv6HostProbeStatus.Ipv6Unavailable,
            result.Status);
        Assert.Contains(
            "IPv6",
            result.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CriticalFlow_PermissionFailureBecomesActionableDiagnostic()
    {
        DhmpRawIpv6HostProbeResult result =
            DhmpRawIpv6HostProbe.ProbeCore(
                isLinux: true,
                ipv6Available: true,
                _ => throw new UnauthorizedAccessException(
                    "CAP_NET_RAW is required."));

        Assert.False(result.IsReady);
        Assert.Equal(
            DhmpRawIpv6HostProbeStatus.PermissionDenied,
            result.Status);
        Assert.Contains(
            "CAP_NET_RAW",
            result.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CriticalFlow_NativeSocketFailureIsReportedWithoutThrowing()
    {
        DhmpRawIpv6HostProbeResult result =
            DhmpRawIpv6HostProbe.ProbeCore(
                isLinux: true,
                ipv6Available: true,
                _ => throw new IOException(
                    "native raw socket failed"));

        Assert.False(result.IsReady);
        Assert.Equal(
            DhmpRawIpv6HostProbeStatus.SocketOpenFailed,
            result.Status);
        Assert.Contains(
            "native raw socket failed",
            result.Message,
            StringComparison.Ordinal);
    }
}
