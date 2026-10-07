using System.Net;
using DHMP.Connector;
using Xunit;

namespace DHMP.Connector.Tests;

public sealed class DhmpTransportResolverTests
{
    [Fact]
    public void Ipv4_auto_exposes_udp_compatibility_without_claiming_raw_ipv6()
    {
        var options =
            new DhmpConnectorOptions(
                IPAddress.Loopback,
                recordSize: 32,
                Guid.NewGuid())
            {
                AllowUnprotectedPayloads = true
            };

        IReadOnlyList<DhmpTransportCandidate> candidates =
            DhmpTransportResolver.Probe(
                options,
                IPAddress.Loopback);

        DhmpTransportCandidate raw =
            Assert.Single(
                candidates.Where(
                    candidate =>
                        candidate.Kind ==
                        DhmpTransportKind.RawIpv6));

        DhmpTransportCandidate udp =
            Assert.Single(
                candidates.Where(
                    candidate =>
                        candidate.Kind ==
                        DhmpTransportKind.UdpCompatibility));

        Assert.False(raw.LocallyAvailable);
        Assert.True(udp.LocallyAvailable);
    }

    [Fact]
    public void Raw_only_rejects_ipv4_configuration()
    {
        var options =
            new DhmpConnectorOptions(
                IPAddress.Loopback,
                recordSize: 32,
                Guid.NewGuid())
            {
                AllowUnprotectedPayloads = true,
                TransportPreference =
                    DhmpTransportPreference.RawIpv6Only
            };

        Assert.Throws<InvalidOperationException>(
            options.Validate);
    }

    [Fact]
    public void Udp_payload_ceiling_must_fit_one_record_when_fallback_is_enabled()
    {
        var options =
            new DhmpConnectorOptions(
                IPAddress.IPv6Loopback,
                recordSize: 1200,
                Guid.NewGuid())
            {
                AllowUnprotectedPayloads = true,
                UdpMaximumPayloadBytes = 1000
            };

        Assert.Throws<ArgumentOutOfRangeException>(
            options.Validate);
    }

    [Fact]
    public void Defaults_enable_resolver_with_separate_udp_data_and_control_ports()
    {
        var options =
            new DhmpConnectorOptions(
                IPAddress.IPv6Loopback,
                recordSize: 32,
                Guid.NewGuid())
            {
                AllowUnprotectedPayloads = true
            };

        Assert.Equal(
            DhmpTransportPreference.Auto,
            options.TransportPreference);

        Assert.NotEqual(
            options.UdpDataPort,
            options.UdpControlPort);

        Assert.Equal(
            1232,
            options.UdpMaximumPayloadBytes);
    }
}
