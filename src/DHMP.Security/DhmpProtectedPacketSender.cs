using System.Buffers;
using DHMP.Protocol;

namespace DHMP.Security;

/// <summary>
/// Wraps any direct-IP packet sender with the explicit DHMP PSK security envelope.
/// The wrapped sender lifetime is owned by the caller.
/// </summary>
public sealed class DhmpProtectedPacketSender : IDhmpDynamicPacketSender, IAsyncDisposable
{
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeSends;
    private bool _retired;
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

    public int CurrentMaximumPayloadBytes
    {
        get
        {
            int innerCurrent =
                _inner.MaximumPayloadBytes;

            if (_inner is IDhmpDynamicPacketSender dynamicSender)
            {
                innerCurrent =
                    Math.Min(
                        innerCurrent,
                        dynamicSender.CurrentMaximumPayloadBytes);
            }

            return Math.Max(
                0,
                innerCurrent -
                DhmpPskChaCha20Poly1305Session.Overhead);
        }
    }

    public async ValueTask SendPacketAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            _activeSends++;
        }

        try
        {
            await SendCoreAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lifetimeGate)
            {
                _activeSends--;
                if (_retired && _activeSends == 0)
                    _drained.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Stop admitting sends and wait for local backend completion and pooled-buffer cleanup.
    /// The caller retains ownership of the backend and security session.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            _retired = true;
            if (_activeSends == 0)
                _drained.TrySetResult();
            return new ValueTask(_drained.Task);
        }
    }

    private async ValueTask SendCoreAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int currentMaximum =
            CurrentMaximumPayloadBytes;

        if (payload.IsEmpty ||
            payload.Length > currentMaximum)
            throw new DhmpProtocolException(
                $"Plaintext DHMP payload is empty or exceeds the protected sender live limit of {currentMaximum} bytes.");

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

