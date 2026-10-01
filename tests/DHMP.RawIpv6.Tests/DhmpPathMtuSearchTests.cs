using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpPathMtuSearchTests
{
    [Fact]
    public async Task MaximumProbeSuccess_StopsAtConfiguredMaximum()
    {
        var options = new DhmpPathMtuDiscoveryOptions(
            maximumPathMtu: 1500,
            probeTimeout: TimeSpan.FromSeconds(1),
            maximumProbeAttempts: 3,
            minimumSearchGainBytes: 1);

        var probed = new List<int>();

        var result = await DhmpPathMtuSearch.RunAsync(
            options,
            (pathMtu, _) =>
            {
                probed.Add(pathMtu);
                return ValueTask.FromResult(true);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(DhmpPathMtuDiscoveryState.SearchComplete, result.State);
        Assert.True(result.BaseConfirmed);
        Assert.True(result.ReachedConfiguredMaximum);
        Assert.Equal(1500, result.ConfirmedPathMtu);
        Assert.Equal(new[] { 1280, 1500 }, probed);
        Assert.Equal(2, result.ProbeTransmissions);
    }

    [Fact]
    public async Task SearchConvergesWithoutEverPromotingFailedSize()
    {
        const int actualPathMtu = 1420;

        var options = new DhmpPathMtuDiscoveryOptions(
            maximumPathMtu: 1500,
            probeTimeout: TimeSpan.FromSeconds(1),
            maximumProbeAttempts: 2,
            minimumSearchGainBytes: 1);

        var result = await DhmpPathMtuSearch.RunAsync(
            options,
            (pathMtu, _) =>
                ValueTask.FromResult(pathMtu <= actualPathMtu),
            TestContext.Current.CancellationToken);

        Assert.Equal(DhmpPathMtuDiscoveryState.SearchComplete, result.State);
        Assert.True(result.BaseConfirmed);
        Assert.False(result.ReachedConfiguredMaximum);
        Assert.Equal(actualPathMtu, result.ConfirmedPathMtu);
        Assert.Equal(
            actualPathMtu - DhmpIpv6PathBudget.Ipv6BaseHeaderBytes,
            result.PathBudget.MaximumProtocolPayloadBytes);
    }

    [Fact]
    public async Task BaseFailure_EntersErrorAndKeepsConservativeFallback()
    {
        var options = new DhmpPathMtuDiscoveryOptions(
            maximumPathMtu: 1500,
            probeTimeout: TimeSpan.FromSeconds(1),
            maximumProbeAttempts: 3);

        var result = await DhmpPathMtuSearch.RunAsync(
            options,
            (_, _) => ValueTask.FromResult(false),
            TestContext.Current.CancellationToken);

        Assert.Equal(DhmpPathMtuDiscoveryState.Error, result.State);
        Assert.False(result.BaseConfirmed);
        Assert.False(result.ReachedConfiguredMaximum);
        Assert.Equal(DhmpIpv6PathBudget.MinimumIpv6Mtu, result.ConfirmedPathMtu);
        Assert.Equal(3, result.ProbeTransmissions);
    }

    [Fact]
    public async Task FailedCandidate_IsRetriedBeforeSearchMovesLower()
    {
        var attempts = new Dictionary<int, int>();

        var options = new DhmpPathMtuDiscoveryOptions(
            maximumPathMtu: 1500,
            probeTimeout: TimeSpan.FromSeconds(1),
            maximumProbeAttempts: 3,
            minimumSearchGainBytes: 16);

        var result = await DhmpPathMtuSearch.RunAsync(
            options,
            (pathMtu, _) =>
            {
                attempts[pathMtu] = attempts.GetValueOrDefault(pathMtu) + 1;
                return ValueTask.FromResult(pathMtu <= 1400);
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.BaseConfirmed);
        Assert.Equal(3, attempts[1500]);
        Assert.InRange(result.ConfirmedPathMtu, 1280, 1400);
    }

    [Fact]
    public void Options_EnforceRfcProbeTimerFloorAndIpv6Bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpPathMtuDiscoveryOptions(
                probeTimeout: TimeSpan.FromMilliseconds(999)));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpPathMtuDiscoveryOptions(
                maximumPathMtu: 1279));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpPathMtuDiscoveryOptions(
                additionalIpv6HeaderBytes:
                    1280 -
                    DhmpIpv6PathBudget.Ipv6BaseHeaderBytes -
                    DHMP.Security.DhmpPskChaCha20Poly1305Session.PathMtuProbeMinimumPacketSize +
                    1));

        var options = new DhmpPathMtuDiscoveryOptions();

        Assert.Equal(1500, options.MaximumPathMtu);
        Assert.Equal(TimeSpan.FromSeconds(20), options.ProbeTimeout);
        Assert.Equal(3, options.MaximumProbeAttempts);
    }
}
