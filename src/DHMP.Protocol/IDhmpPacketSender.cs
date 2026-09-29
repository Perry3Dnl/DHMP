namespace DHMP.Protocol;

/// <summary>Send one complete DHMP packet payload through a direct-IP backend.</summary>
/// <remarks>
/// The backend is configured with peer/session addressing before use. Each call is one packet:
/// do not segment, coalesce, retry or silently queue beyond a bounded send budget.
/// Keep payload storage valid until the returned ValueTask completes. Completion means the
/// backend has finished using that storage, not remote delivery. Reject sizes exceeding the
/// actual path MTU after IP/security overhead. Production backend and session negotiation
/// remain pending; no compatibility transport is selected implicitly.
/// </remarks>
public interface IDhmpPacketSender
{
    int MaximumPayloadBytes { get; }
    ValueTask SendPacketAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
