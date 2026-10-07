namespace DHMP.Connector;

/// <summary>One locally evaluated DHMP transport candidate.</summary>
public sealed record DhmpTransportCandidate(
    DhmpTransportKind Kind,
    bool LocallyAvailable,
    string Detail);
