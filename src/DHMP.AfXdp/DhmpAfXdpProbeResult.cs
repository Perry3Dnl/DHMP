namespace DHMP.AfXdp;

public sealed record DhmpAfXdpProbeResult(
    bool Supported,
    DhmpAfXdpMode Mode,
    string Detail);
