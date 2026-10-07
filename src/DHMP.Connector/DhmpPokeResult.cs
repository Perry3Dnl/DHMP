namespace DHMP.Connector;

/// <summary>
/// Reachability and exact-echo timing captured before compatibility/security negotiation.
/// </summary>
public readonly record struct DhmpPokeResult(
    DhmpTransportKind Transport,
    TimeSpan MiniRoundTripTime,
    TimeSpan FullEchoRoundTripTime,
    int FullEchoBytes)
{
    public double MiniRoundTripMilliseconds =>
        MiniRoundTripTime.TotalMilliseconds;

    public double FullEchoRoundTripMilliseconds =>
        FullEchoRoundTripTime.TotalMilliseconds;

    /// <summary>
    /// Bytes observed across the request+echo round trip divided by measured RTT.
    /// This is an echo-path observation, not a sustained throughput claim.
    /// </summary>
    public double FullEchoRoundTripBytesPerSecond =>
        FullEchoRoundTripTime <= TimeSpan.Zero
            ? 0
            : checked(FullEchoBytes * 2d) /
              FullEchoRoundTripTime.TotalSeconds;
}
