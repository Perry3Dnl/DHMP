namespace DHMP.Protocol;

/// <summary>Validates one complete headerless DHMP data payload and publishes one borrowed record batch.</summary>
public sealed class DhmpPacketProcessor
{
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpReceivePolicy _receivePolicy;
    private readonly bool _latest;

    public DhmpPacketProcessor(DhmpWireContract wireContract, DhmpReceivePolicy receivePolicy = default)
    {
        wireContract.Validate();
        if (receivePolicy == default) receivePolicy = new DhmpReceivePolicy();
        receivePolicy.Validate(wireContract);
        _wireContract = wireContract;
        _receivePolicy = receivePolicy;
        _latest = receivePolicy.Mode == DhmpProcessingMode.Latest;
    }

    public void Process(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<byte>> publishBatch)
    {
        ArgumentNullException.ThrowIfNull(publishBatch);
        _wireContract.ValidatePacket(packet.Length, _receivePolicy.MaximumPayloadBytes);
        publishBatch(_latest ? packet[^_wireContract.RecordSize..] : packet);
    }
}
