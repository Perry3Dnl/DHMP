namespace DHMP.Protocol;

/// <summary>Validates one complete headerless DHMP data payload and publishes one borrowed record batch.</summary>
/// <remarks>
/// The packet bytes contain records only: no DHMP packet header, per-record header, separator or trailer.
/// The wire contract defines record interpretation; receive mode and packet ceiling are local policy.
/// Latest mode can optionally expose a tail window of already-received records for application smoothing.
/// </remarks>
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
        _latest = receivePolicy.Mode == DhmpProcessingMode.Latest;
    }

    public void Process(
        ReadOnlySpan<byte> packet,
        Action<ReadOnlySpan<byte>> publishBatch)
    {
        ArgumentNullException.ThrowIfNull(publishBatch);

        _wireContract.ValidatePacket(
            packet.Length,
            _receivePolicy.MaximumPayloadBytes);

        if (!_latest)
        {
            publishBatch(packet);
            return;
        }

        int packetRecords =
            packet.Length /
            _wireContract.RecordSize;

        int historyRecords =
            Math.Min(
                packetRecords,
                _receivePolicy.LatestHistoryRecords);

        int historyBytes =
            historyRecords *
            _wireContract.RecordSize;

        publishBatch(packet[^historyBytes..]);
    }
}
