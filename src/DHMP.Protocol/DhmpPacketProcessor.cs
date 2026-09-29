namespace DHMP.Protocol;

/// <summary>Validates one complete IP payload and publishes one borrowed record batch.</summary>
/// <remarks>
/// No byte-stream reconstruction, carry buffer, per-message allocation or transport calls.
/// The caller supplies a non-null synchronous internal handoff callback. It must not retain
/// the span after returning. Async queues need explicit storage ownership in the next layer.
/// Latest selects the final record in this packet; it does not detect reordered IP packets.
/// </remarks>
public sealed class DhmpPacketProcessor
{
    private readonly DhmpFixedContract _contract;
    private readonly bool _latest;

    public DhmpPacketProcessor(DhmpFixedContract contract, DhmpProcessingMode mode = DhmpProcessingMode.Sequential)
    {
        contract.Validate();
        if (mode is not DhmpProcessingMode.Sequential and not DhmpProcessingMode.Latest)
            throw new ArgumentOutOfRangeException(nameof(mode));
        _contract = contract;
        _latest = mode == DhmpProcessingMode.Latest;
    }

    public void Process(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<byte>> publishBatch)
    {
        _contract.ValidatePacket(packet.Length);
        publishBatch(_latest ? packet[^_contract.PayloadSize..] : packet);
    }
}
