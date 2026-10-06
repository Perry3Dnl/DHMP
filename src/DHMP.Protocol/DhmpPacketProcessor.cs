namespace DHMP.Protocol;

/// <summary>Validates one complete headerless DHMP data payload and publishes one borrowed record batch.</summary>
public sealed class DhmpPacketProcessor
{
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpReceivePolicy _receivePolicy;
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
        _receivePolicy = receivePolicy;
        _latest =
            receivePolicy.Mode ==
            DhmpProcessingMode.Latest;
    }

    public void Process(
        ReadOnlySpan<byte> packet,
        Action<ReadOnlySpan<byte>> publishBatch)
    {
        Process(
            packet,
            publishBatch,
            validatedPacketObserver: null);
    }

    /// <summary>
    /// Validate once, optionally expose the complete validated packet to an
    /// internal receive-side observer, then apply normal publication semantics.
    /// </summary>
    public void Process(
        ReadOnlySpan<byte> packet,
        Action<ReadOnlySpan<byte>> publishBatch,
        Action<ReadOnlySpan<byte>>? validatedPacketObserver)
    {
        ArgumentNullException.ThrowIfNull(
            publishBatch);

        _wireContract.ValidatePacket(
            packet.Length,
            _receivePolicy.MaximumPayloadBytes);

        validatedPacketObserver?.Invoke(
            packet);

        publishBatch(
            _latest
                ? packet[^_wireContract.RecordSize..]
                : packet);
    }
}
