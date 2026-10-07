namespace DHMP.Protocol;

/// <summary>Send one complete headerless DHMP data payload through the selected packet backend.</summary>
/// <remarks>
/// The backend is configured with peer/path/session addressing before use. Each call represents
/// one DHMP data payload for one IP packet. Do not prepend hidden DHMP metadata, segment, coalesce,
/// retry or silently queue beyond a bounded send budget.
///
/// Keep payload storage valid until the returned ValueTask completes. Completion means the backend
/// has finished using that local storage, not remote delivery. MaximumPayloadBytes is the maximum
/// DHMP data payload the backend can place on its configured path after lower-layer/security overhead.
///
/// Packet I/O and control-plane negotiation remain separate from V1 framing. A higher-level
/// connection resolver may explicitly select a compatible carrier without changing these payload bytes.
/// </remarks>
public interface IDhmpPacketSender
{
    int MaximumPayloadBytes { get; }
    ValueTask SendPacketAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
