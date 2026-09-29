using DHMP.Protocol;

namespace DHMP.Server;

/// <summary>
/// Receiver-side protocol facade for one configured DHMP wire contract and local receive policy.
/// A direct-IP backend supplies complete headerless packet payloads.
/// </summary>
public sealed class DhmpServer
{
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpReceivePolicy _receivePolicy;
    private readonly DhmpPacketProcessor _processor;

    public DhmpServer(
        DhmpWireContract wireContract,
        DhmpReceivePolicy receivePolicy = default)
    {
        wireContract.Validate();

        if (receivePolicy == default)
            receivePolicy = new DhmpReceivePolicy();

        receivePolicy.Validate(wireContract);

        _wireContract = wireContract;
        _receivePolicy = receivePolicy;
        _processor = new DhmpPacketProcessor(
            wireContract,
            receivePolicy);
    }

    public DhmpWireContract WireContract => _wireContract;
    public DhmpReceivePolicy ReceivePolicy => _receivePolicy;

    public void ProcessPacket(
        ReadOnlySpan<byte> packet,
        Action<ReadOnlySpan<byte>> publishBatch)
        => _processor.Process(packet, publishBatch);
}
