using DHMP.Protocol;

namespace DHMP.Server;

/// <summary>
/// Receiver-side protocol facade for one configured DHMP wire contract and
/// local receive policy.
/// </summary>
public sealed class DhmpServer
{
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpReceivePolicy _receivePolicy;
    private readonly DhmpPacketProcessor _processor;
    private readonly DhmpLatestStateWindow? _latestSweepSlots;

    public DhmpServer(
        DhmpWireContract wireContract,
        DhmpReceivePolicy receivePolicy = default)
    {
        wireContract.Validate();

        if (receivePolicy == default)
            receivePolicy =
                new DhmpReceivePolicy();

        receivePolicy.Validate(
            wireContract);

        _wireContract =
            wireContract;

        _receivePolicy =
            receivePolicy;

        _processor =
            new DhmpPacketProcessor(
                wireContract,
                receivePolicy);

        // Latest and Latest + Native Smoothing share the exact same three
        // physical sweep slots. Native Smoothing only changes how the grabber
        // consumes those already-completed slots.
        if (receivePolicy.Mode ==
            DhmpProcessingMode.Latest)
        {
            _latestSweepSlots =
                new DhmpLatestStateWindow(
                    wireContract.RecordSize);
        }
    }

    public DhmpWireContract WireContract =>
        _wireContract;

    public DhmpReceivePolicy ReceivePolicy =>
        _receivePolicy;

    public bool LatestGrabberAvailable =>
        _latestSweepSlots is not null;

    public bool NativeSmoothingEnabled =>
        _receivePolicy.NativeSmoothing;

    public int NativeSmoothingRecordCount =>
        NativeSmoothingEnabled
            ? _latestSweepSlots?.Count ?? 0
            : 0;

    /// <summary>
    /// Packet processing is deliberately identical for Latest and Latest +
    /// Native Smoothing. Ring-3 is not maintained from the packet hot path.
    /// </summary>
    public void ProcessPacket(
        ReadOnlySpan<byte> packet,
        Action<ReadOnlySpan<byte>> publishBatch)
    {
        _processor.Process(
            packet,
            publishBatch);
    }

    /// <summary>
    /// Sweeper API: obtain the next physical Latest slot and write the complete
    /// record directly into it. This does not depend on Native Smoothing.
    /// </summary>
    public Span<byte> BeginLatestSweep() =>
        GetLatestSweepSlots()
            .BeginSweep();

    public void CommitLatestSweep() =>
        GetLatestSweepSlots()
            .CommitSweep();

    public void CancelLatestSweep() =>
        GetLatestSweepSlots()
            .CancelSweep();

    /// <summary>
    /// Convenience path when a completed sweep record already exists elsewhere.
    /// Direct integrations should prefer BeginLatestSweep/CommitLatestSweep.
    /// </summary>
    public void SweepLatest(
        ReadOnlySpan<byte> record) =>
        GetLatestSweepSlots()
            .Sweep(record);

    /// <summary>
    /// Latest grabber: immediately copy the slot fully published at the moment
    /// of the grab. It does not wait for a three-slot smoothing window.
    /// </summary>
    public int CopyLatest(
        Span<byte> destination) =>
        GetLatestSweepSlots()
            .CopyLatestTo(destination);

    /// <summary>
    /// Native-smoothing grabber: consume exactly the last three fully swept
    /// slots. Returns zero until a complete three-slot window exists.
    /// </summary>
    public int CopyNativeSmoothingWindow(
        Span<byte> destination)
    {
        if (!NativeSmoothingEnabled)
            return 0;

        return GetLatestSweepSlots()
            .CopyCompletedWindow3To(
                destination);
    }

    private DhmpLatestStateWindow GetLatestSweepSlots() =>
        _latestSweepSlots ??
        throw new InvalidOperationException(
            "Latest sweep/grab APIs require DhmpProcessingMode.Latest.");
}
