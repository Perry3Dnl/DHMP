using System.Buffers;
using DHMP.Protocol;

namespace DHMP.Security;

/// <summary>
/// Wraps any direct-IP packet sender with the explicit DHMP PSK security envelope.
/// The wrapped sender lifetime is owned by the caller.
/// </summary>
public sealed class DhmpProtectedPacketSender : IDhmpPacketSender
{
    private readonly IDhmpPacketSender _inner;
    private readonly DhmpPskChaCha20Poly1305Session _session;

    public DhmpProtectedPacketSender(
        IDhmpPacketSender inner,
        DhmpPskChaCha20Poly1305Session session)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(session);

        if (inner.MaximumPayloadBytes <=
            DhmpPskChaCha20Poly1305Session.Overhead)
            throw new ArgumentException(
                "Underlying sender cannot fit the DHMP security envelope.",
                nameof(inner));

        _inner = inner;
        _session = session;

        MaximumPayloadBytes =
            inner.MaximumPayloadBytes -
            DhmpPskChaCha20Poly1305Session.Overhead;
    }

    public int MaximumPayloadBytes { get; }

    public async ValueTask SendPacketAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (payload.IsEmpty ||
            payload.Length > MaximumPayloadBytes)
            throw new DhmpProtocolException(
                "Plaintext DHMP payload is empty or exceeds the protected sender limit.");

        int protectedLength = checked(
            payload.Length +
            DhmpPskChaCha20Poly1305Session.Overhead);

        byte[] rented =
            ArrayPool<byte>.Shared.Rent(
                protectedLength);

        try
        {
            int written =
                _session.Protect(
                    payload.Span,
                    rented.AsSpan(
                        0,
                        protectedLength));

            await _inner.SendPacketAsync(
                rented.AsMemory(0, written),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(
                rented,
                clearArray: true);
        }
    }
}
