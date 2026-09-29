namespace DHMP.Server;

/// <summary>
/// Immutable point-in-time view of bounded receive-dispatch pressure.
/// </summary>
public readonly record struct DhmpReceiveDispatchSnapshot(
    DhmpReceiveDispatchMode Mode,
    int Capacity,
    int PendingBatches,
    long AcceptedBatches,
    long ConsumedBatches,
    long SaturationDrops,
    long ReplacedBatches)
{
    public bool IsSaturated =>
        Mode == DhmpReceiveDispatchMode.SequentialReject &&
        PendingBatches >= Capacity;

    public long LostPendingWork =>
        checked(SaturationDrops + ReplacedBatches);

    public void Validate()
    {
        if (Mode is not DhmpReceiveDispatchMode.SequentialReject and
            not DhmpReceiveDispatchMode.LatestReplace)
            throw new ArgumentException(
                "A supported receive dispatch mode is required.");

        if (Capacity <= 0 ||
            PendingBatches < 0 ||
            PendingBatches > Capacity ||
            AcceptedBatches < 0 ||
            ConsumedBatches < 0 ||
            SaturationDrops < 0 ||
            ReplacedBatches < 0 ||
            ConsumedBatches > AcceptedBatches)
            throw new ArgumentException(
                "A valid receive dispatch snapshot is required.");
    }
}
