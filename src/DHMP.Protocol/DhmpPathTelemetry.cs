namespace DHMP.Protocol;

/// <summary>
/// Authenticated path observation returned by a peer.
/// Loss is a rolling estimate over the peer's newest protected-data counter window.
/// RTT is locally measured from an authenticated probe/echo exchange.
/// </summary>
public readonly record struct DhmpPathTelemetry
{
    public DhmpPathTelemetry(
        TimeSpan roundTripTime,
        ulong highestPacketCounter,
        int windowSpan,
        int missingWithinWindow,
        long acceptedPackets)
    {
        if (roundTripTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(roundTripTime));

        if (windowSpan < 0 || windowSpan > 64)
            throw new ArgumentOutOfRangeException(
                nameof(windowSpan));

        if (missingWithinWindow < 0 ||
            missingWithinWindow > windowSpan)
            throw new ArgumentOutOfRangeException(
                nameof(missingWithinWindow));

        if (acceptedPackets < 0)
            throw new ArgumentOutOfRangeException(
                nameof(acceptedPackets));

        RoundTripTime = roundTripTime;
        HighestPacketCounter = highestPacketCounter;
        WindowSpan = windowSpan;
        MissingWithinWindow = missingWithinWindow;
        AcceptedPackets = acceptedPackets;
    }

    public TimeSpan RoundTripTime { get; }
    public ulong HighestPacketCounter { get; }
    public int WindowSpan { get; }
    public int MissingWithinWindow { get; }
    public long AcceptedPackets { get; }

    public int LossPermille =>
        WindowSpan == 0
            ? 0
            : checked(
                MissingWithinWindow * 1000 /
                WindowSpan);
}
