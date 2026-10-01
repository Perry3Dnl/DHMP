namespace DHMP.RawIpv6;

public enum DhmpPathMtuDiscoveryState
{
    Error = 0,
    SearchComplete = 1
}

/// <summary>
/// Result of one authenticated DHMP DPLPMTUD search.
/// When BaseConfirmed is false, PathBudget is only the conservative IPv6-minimum fallback
/// and has not been confirmed by the peer.
/// </summary>
public readonly record struct DhmpPathMtuDiscoveryResult
{
    public DhmpPathMtuDiscoveryResult(
        DhmpPathMtuDiscoveryState state,
        DhmpIpv6PathBudget pathBudget,
        bool baseConfirmed,
        bool reachedConfiguredMaximum,
        int probeTransmissions)
    {
        if (state is not DhmpPathMtuDiscoveryState.Error and
            not DhmpPathMtuDiscoveryState.SearchComplete)
            throw new ArgumentOutOfRangeException(nameof(state));

        if (probeTransmissions < 0)
            throw new ArgumentOutOfRangeException(nameof(probeTransmissions));

        if (state == DhmpPathMtuDiscoveryState.Error && baseConfirmed)
            throw new ArgumentException("DPLPMTUD error state cannot report a confirmed base path.");

        if (reachedConfiguredMaximum && !baseConfirmed)
            throw new ArgumentException("An unconfirmed path cannot reach the configured maximum.");

        State = state;
        PathBudget = pathBudget;
        BaseConfirmed = baseConfirmed;
        ReachedConfiguredMaximum = reachedConfiguredMaximum;
        ProbeTransmissions = probeTransmissions;
    }

    public DhmpPathMtuDiscoveryState State { get; }
    public DhmpIpv6PathBudget PathBudget { get; }
    public bool BaseConfirmed { get; }
    public bool ReachedConfiguredMaximum { get; }
    public int ProbeTransmissions { get; }
    public int ConfirmedPathMtu => PathBudget.PathMtu;
}
