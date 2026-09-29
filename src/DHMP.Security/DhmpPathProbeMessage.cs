namespace DHMP.Security;

public enum DhmpPathProbeType : byte
{
    Request = 1,
    Response = 2
}

/// <summary>
/// Authenticated path probe/echo payload. SenderTimestamp is opaque to the remote peer
/// and is echoed unchanged so the originator can measure RTT on its own monotonic clock.
/// </summary>
public readonly record struct DhmpPathProbeMessage
{
    public DhmpPathProbeMessage(
        DhmpPathProbeType type,
        ulong probeId,
        ulong senderTimestamp,
        ulong highestPacketCounter,
        int windowSpan,
        int missingWithinWindow,
        long acceptedPackets)
    {
        if (type is not DhmpPathProbeType.Request and
            not DhmpPathProbeType.Response)
            throw new ArgumentOutOfRangeException(nameof(type));

        if (probeId == 0)
            throw new ArgumentOutOfRangeException(nameof(probeId));

        if (senderTimestamp == 0)
            throw new ArgumentOutOfRangeException(nameof(senderTimestamp));

        if (windowSpan < 0 || windowSpan > 64)
            throw new ArgumentOutOfRangeException(nameof(windowSpan));

        if (missingWithinWindow < 0 ||
            missingWithinWindow > windowSpan)
            throw new ArgumentOutOfRangeException(
                nameof(missingWithinWindow));

        if (acceptedPackets < 0)
            throw new ArgumentOutOfRangeException(nameof(acceptedPackets));

        Type = type;
        ProbeId = probeId;
        SenderTimestamp = senderTimestamp;
        HighestPacketCounter = highestPacketCounter;
        WindowSpan = windowSpan;
        MissingWithinWindow = missingWithinWindow;
        AcceptedPackets = acceptedPackets;
    }

    public DhmpPathProbeType Type { get; }
    public ulong ProbeId { get; }
    public ulong SenderTimestamp { get; }
    public ulong HighestPacketCounter { get; }
    public int WindowSpan { get; }
    public int MissingWithinWindow { get; }
    public long AcceptedPackets { get; }

    public void Validate()
    {
        if (Type is not DhmpPathProbeType.Request and
            not DhmpPathProbeType.Response)
            throw new ArgumentException(
                "A supported DHMP path probe type is required.");

        if (ProbeId == 0 ||
            SenderTimestamp == 0 ||
            WindowSpan < 0 ||
            WindowSpan > 64 ||
            MissingWithinWindow < 0 ||
            MissingWithinWindow > WindowSpan ||
            AcceptedPackets < 0)
            throw new ArgumentException(
                "A valid DHMP path probe message is required.");
    }
}
