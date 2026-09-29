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
}
