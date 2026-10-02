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

    /// <summary>Create a receiver-side facade for one fixed-record wire contract and local receive policy.</summary>
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

    /// <summary>The fixed-record wire contract enforced for incoming packets.</summary>
    public DhmpWireContract WireContract => _wireContract;

    /// <summary>The local publication mode and inbound payload ceiling.</summary>
    public DhmpReceivePolicy ReceivePolicy => _receivePolicy;

    /// <summary>
    /// Validate one complete borrowed packet payload and synchronously publish the selected records.
    /// The callback must not retain the supplied span beyond the call.
    /// </summary>
    public void ProcessPacket(
        ReadOnlySpan<byte> packet,
        Action<ReadOnlySpan<byte>> publishBatch)
        => _processor.Process(packet, publishBatch);
}
