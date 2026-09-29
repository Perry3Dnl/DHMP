using System.Net;
using DHMP.Protocol;
using DHMP.Security;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpIpv6PathBudgetTests
{
    [Fact]
    public void MinimumIpv6Mtu_Leaves1240ProtocolPayloadBytes()
    {
        var budget =
            new DhmpIpv6PathBudget(1280);

        Assert.Equal(
            1240,
            budget.MaximumProtocolPayloadBytes);
        Assert.Equal(
            40,
            DhmpIpv6PathBudget.Ipv6BaseHeaderBytes);
    }

    [Fact]
    public void Standard1500Mtu_Leaves1460RawProtocolBytes()
    {
        var budget =
            new DhmpIpv6PathBudget(1500);

        Assert.Equal(
            1460,
            budget.MaximumProtocolPayloadBytes);
    }

    [Fact]
    public void Secure32ByteRecords_On1500Mtu_AlignTo1408PlaintextBytes()
    {
        var budget =
            new DhmpIpv6PathBudget(1500);

        int plaintextBytes =
            budget.GetAlignedPlaintextPayloadBytes(
                new DhmpWireContract(32),
                DhmpPskChaCha20Poly1305Session.Overhead);

        Assert.Equal(
            24,
            DhmpPskChaCha20Poly1305Session.Overhead);

        Assert.Equal(
            1408,
            plaintextBytes);
    }

    [Fact]
    public void Plain32ByteRecords_On1500Mtu_AlignTo1440Bytes()
    {
        var budget =
            new DhmpIpv6PathBudget(1500);

        Assert.Equal(
            1440,
            budget.GetAlignedPlaintextPayloadBytes(
                new DhmpWireContract(32)));
    }

    [Fact]
    public void AdditionalIpv6Headers_ReduceProtocolBudget()
    {
        var budget =
            new DhmpIpv6PathBudget(
                1500,
                additionalIpv6HeaderBytes: 8);

        Assert.Equal(
            1452,
            budget.MaximumProtocolPayloadBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1279)]
    [InlineData(65576)]
    public void InvalidPathMtu_IsRejected(
        int pathMtu)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpIpv6PathBudget(pathMtu));
    }

    [Fact]
    public void PathTooSmallForOneRecordAfterEnvelope_IsRejected()
    {
        var budget =
            new DhmpIpv6PathBudget(1280);

        Assert.Throws<ArgumentException>(() =>
            budget.GetAlignedPlaintextPayloadBytes(
                new DhmpWireContract(1240),
                packetEnvelopeBytes: 1));
    }

    [Fact]
    public void PeerAndListenerOptions_CanBeCreatedFromPathMtu()
    {
        var peer =
            DhmpRawIpv6Options.FromPathMtu(
                IPAddress.IPv6Loopback,
                IPAddress.Parse("2001:db8::2"),
                1500);

        var listener =
            DhmpRawIpv6ListenerOptions.FromPathMtu(
                IPAddress.IPv6Any,
                1280,
                maximumPeers: 8);

        Assert.Equal(
            1460,
            peer.MaximumPayloadBytes);

        Assert.Equal(
            1240,
            listener.MaximumPayloadBytes);

        Assert.Equal(
            8,
            listener.MaximumPeers);
    }

    [Fact]
    public void PathMtuException_ExposesAttemptAndConfiguredCeiling()
    {
        var inner =
            new InvalidOperationException("native EMSGSIZE");

        var error =
            new DhmpPathMtuException(
                attemptedPayloadBytes: 1460,
                configuredPayloadCeiling: 1408,
                inner);

        Assert.Equal(
            1460,
            error.AttemptedPayloadBytes);

        Assert.Equal(
            1408,
            error.ConfiguredPayloadCeiling);

        Assert.Same(
            inner,
            error.InnerException);
    }
}
