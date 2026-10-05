namespace DHMP.AfXdp;

public sealed record DhmpAfXdpBenchmarkResult(
    bool Supported,
    DhmpAfXdpMode Mode,
    long PacketsCompleted,
    long PayloadBytesCompleted,
    double Seconds,
    double PacketsPerSecond,
    double PayloadGigabytesPerSecond,
    string Detail);
