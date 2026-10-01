namespace DHMP.RawIpv6;

/// <summary>
/// Search policy for authenticated DHMP Datagram PLPMTUD probes.
/// The base IPv6 PLPMTU is 1280 bytes; the configured maximum is an upper search bound,
/// not a claim about the path.
/// </summary>
public sealed class DhmpPathMtuDiscoveryOptions
{
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(20);

    public DhmpPathMtuDiscoveryOptions(
        int maximumPathMtu = 1500,
        int additionalIpv6HeaderBytes = 0,
        TimeSpan? probeTimeout = null,
        int maximumProbeAttempts = 3,
        int minimumSearchGainBytes = 8)
    {
        if (maximumPathMtu < DhmpIpv6PathBudget.MinimumIpv6Mtu ||
            maximumPathMtu > DhmpIpv6PathBudget.MaximumNonJumboIpv6PacketBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPathMtu));

        if (additionalIpv6HeaderBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(additionalIpv6HeaderBytes));

        var baseBudget = new DhmpIpv6PathBudget(
            DhmpIpv6PathBudget.MinimumIpv6Mtu,
            additionalIpv6HeaderBytes);

        if (baseBudget.MaximumProtocolPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(additionalIpv6HeaderBytes));

        TimeSpan timeout = probeTimeout ?? DefaultProbeTimeout;
        if (timeout < TimeSpan.FromSeconds(1) ||
            timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(
                nameof(probeTimeout),
                "DPLPMTUD probe timeout must be between 1 second and 5 minutes.");

        if (maximumProbeAttempts is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maximumProbeAttempts));

        if (minimumSearchGainBytes < 1 ||
            minimumSearchGainBytes >
                maximumPathMtu - DhmpIpv6PathBudget.MinimumIpv6Mtu + 1)
            throw new ArgumentOutOfRangeException(nameof(minimumSearchGainBytes));

        MaximumPathMtu = maximumPathMtu;
        AdditionalIpv6HeaderBytes = additionalIpv6HeaderBytes;
        ProbeTimeout = timeout;
        MaximumProbeAttempts = maximumProbeAttempts;
        MinimumSearchGainBytes = minimumSearchGainBytes;
    }

    public int MaximumPathMtu { get; }
    public int AdditionalIpv6HeaderBytes { get; }
    public TimeSpan ProbeTimeout { get; }
    public int MaximumProbeAttempts { get; }
    public int MinimumSearchGainBytes { get; }
}
