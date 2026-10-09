namespace DHMP.Protocol;

/// <summary>
/// Applies the configured receive policy to bytes already accepted by the transport/session boundary.
/// Steady-state processing does not revalidate the negotiated wire contract.
/// </summary>
public sealed class DhmpPacketProcessor
{
    private readonly DhmpWireContract _wireContract;
    private readonly bool _latest;

    public DhmpPacketProcessor(
        DhmpWireContract wireContract,
        DhmpReceivePolicy receivePolicy = default)
    {
        wireContract.Validate();

        if (receivePolicy == default)
            receivePolicy = new DhmpReceivePolicy();

        receivePolicy.Validate(wireContract);

        _wireContract = wireContract;
        _latest =
            receivePolicy.Mode is
                DhmpProcessingMode.Latest or
                DhmpProcessingMode.UnsafeLatest;
    }

    public void Process(
        ReadOnlySpan<byte> packet,
        Action<ReadOnlySpan<byte>> publishBatch)
    {
        Process(
            packet,
            publishBatch,
            completeRecordsObserver: null);
    }

    /// <summary>
    /// Ignore any incomplete tail, expose only whole records to the optional internal observer,
    /// then apply Sequential/Latest-family publication. No per-packet protocol exception is raised.
    /// </summary>
    public void Process(
        ReadOnlySpan<byte> packet,
        Action<ReadOnlySpan<byte>> publishBatch,
        Action<ReadOnlySpan<byte>>? completeRecordsObserver)
    {
        ArgumentNullException.ThrowIfNull(
            publishBatch);

        int recordSize =
            _wireContract.RecordSize;

        int completeBytes =
            packet.Length /
            recordSize *
            recordSize;

        if (completeBytes == 0)
            return;

        ReadOnlySpan<byte> complete =
            packet[..completeBytes];

        completeRecordsObserver?.Invoke(
            complete);

        publishBatch(
            _latest
                ? complete[^recordSize..]
                : complete);
    }
}
