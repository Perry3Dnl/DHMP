namespace DHMP.Protocol;

/// <summary>Validates one complete headerless DHMP data payload and publishes one borrowed record batch.</summary>
/// <remarks>
/// The packet bytes contain records only: no DHMP packet header, per-record header, separator or trailer.
/// No byte-stream reconstruction, carry buffer, per-message allocation or transport calls occur here.
///
/// The caller supplies a non-null synchronous internal handoff callback. It must not retain the span
/// after returning. Async queues need explicit storage ownership in the next layer.
///
/// Latest selects the final record in this received packet. DHMP V1 has no wire sequence metadata,
/// so this processor cannot determine cross-packet generation freshness after network reordering.
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
        ArgumentNullException.ThrowIfNull(publishBatch);
        _contract.ValidatePacket(packet.Length);
        publishBatch(_latest ? packet[^_contract.PayloadSize..] : packet);
    }
}
