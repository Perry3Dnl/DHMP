namespace DHMP.Security;

/// <summary>
/// Point-in-time receiver telemetry derived from the authenticated data counter stream.
/// MissingWithinWindow is a rolling estimate over at most the newest 64 counters and may
/// decrease when reordered packets arrive later.
/// </summary>
public readonly record struct DhmpSecureReceiveSnapshot(
    ulong HighestPacketCounter,
    int WindowSpan,
    int MissingWithinWindow,
    long AcceptedPackets,
    long ReorderedPackets,
    long ReplayRejectedPackets,
    long AuthenticationFailures)
{
    public int LossPermille =>
        WindowSpan == 0
            ? 0
            : checked(
                MissingWithinWindow * 1000 /
                WindowSpan);
}
