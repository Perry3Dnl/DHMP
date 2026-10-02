using System.Diagnostics;

namespace DHMP.Client;

/// <summary>
/// Bounds for opt-in application-owned confirmation tracking.
/// This helper defines no DHMP wire fields and adds no bytes to a record.
/// </summary>
public sealed class DhmpApplicationConfirmationOptions
{
    /// <summary>Maximum number of application confirmation IDs allowed to wait concurrently.</summary>
    public int MaximumInFlight { get; set; } = 32;

    /// <summary>Maximum time to wait for a matching application-owned confirmation.</summary>
    public TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Evidence that the application observed its own confirmation identifier from the peer.
/// It does not prove application execution or create reliable delivery.
/// </summary>
public readonly record struct DhmpApplicationConfirmationReceipt(
    ulong ApplicationId,
    TimeSpan RoundTripTime);

/// <summary>
/// Tracks application-owned confirmation identifiers while leaving the complete DHMP record opaque.
/// The application decides where identifiers and confirmations live in its own fixed record schema.
/// No DHMP header, sequence field, ACK field or retransmission is introduced.
/// </summary>
/// <remarks>
/// The wrapped <see cref="DhmpClient"/> must be exclusively owned by this tracker while it is active,
/// because DHMP client sends are serialized. Use <see cref="SendUntrackedAsync"/> for ordinary
/// application records that share the same client.
/// </remarks>
public sealed class DhmpApplicationConfirmationTracker : IAsyncDisposable
{
    private readonly DhmpClient _client;
    private readonly int _maximumInFlight;
    private readonly TimeSpan _timeout;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<ulong, TaskCompletionSource> _pending = [];
    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _retired =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private bool _stopped;

    /// <summary>
    /// Create a tracker over an exclusively owned client send path. Disposing the tracker retires
    /// its own pending work but does not dispose the underlying <see cref="DhmpClient"/> or sender.
    /// </summary>
    public DhmpApplicationConfirmationTracker(
        DhmpClient client,
        DhmpApplicationConfirmationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        options ??= new();

        if (options.MaximumInFlight is < 1 or > 4096 ||
            options.ConfirmationTimeout <= TimeSpan.Zero ||
            options.ConfirmationTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Confirmation admission and deadline must be bounded.");
        }

        _client = client;
        _maximumInFlight = options.MaximumInFlight;
        _timeout = options.ConfirmationTimeout;
    }

    /// <summary>Configured bound on concurrently pending confirmation IDs.</summary>
    public int MaximumInFlight => _maximumInFlight;

    /// <summary>
    /// Sends one complete application record once and waits until the application later calls
    /// <see cref="TryConfirm"/> with the same nonzero application-owned identifier.
    /// Timeout or cancellation means delivery remains unknown. No retransmission occurs.
    /// </summary>
    public async Task<DhmpApplicationConfirmationReceipt> SendTrackedAsync(
        ulong applicationId,
        ReadOnlyMemory<byte> completeApplicationRecord,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (applicationId == 0)
            throw new ArgumentOutOfRangeException(
                nameof(applicationId),
                "Application confirmation IDs must be nonzero.");

        _client.WireContract.ValidateRecord(completeApplicationRecord.Length);

        TaskCompletionSource pending;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);

            if (_pending.Count >= _maximumInFlight)
                throw new InvalidOperationException(
                    "Application confirmation admission is full.");

            if (_pending.ContainsKey(applicationId))
                throw new InvalidOperationException(
                    "The application confirmation ID is already pending.");

            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(applicationId, pending);
            _active++;
        }

        long started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stop.Token,
            deadline.Token);

        try
        {
            await SendRecordAsync(
                completeApplicationRecord,
                linked.Token).ConfigureAwait(false);

            await pending.Task.WaitAsync(linked.Token).ConfigureAwait(false);

            return new(
                applicationId,
                Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested &&
            !_stop.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Matching application confirmation was not observed before the deadline; delivery is unknown.");
        }
        finally
        {
            lock (_sync)
            {
                _pending.Remove(applicationId);
                Leave();
            }
        }
    }

    /// <summary>
    /// Sends an ordinary complete application record through the same serialized client path
    /// without creating confirmation state. The bytes are not rewritten.
    /// </summary>
    public async ValueTask SendUntrackedAsync(
        ReadOnlyMemory<byte> completeApplicationRecord,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _client.WireContract.ValidateRecord(completeApplicationRecord.Length);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            _active++;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _stop.Token);

            await SendRecordAsync(
                completeApplicationRecord,
                linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                Leave();
            }
        }
    }

    /// <summary>
    /// Completes one matching pending send after the application has decoded that ID from its
    /// own authenticated/validated receive schema. Unknown, late and duplicate IDs return false.
    /// </summary>
    public bool TryConfirm(ulong applicationId)
    {
        if (applicationId == 0)
            return false;

        lock (_sync)
        {
            if (_stopped ||
                !_pending.TryGetValue(applicationId, out TaskCompletionSource? pending))
            {
                return false;
            }

            return pending.TrySetResult();
        }
    }

    private async ValueTask SendRecordAsync(
        ReadOnlyMemory<byte> record,
        CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _client.SendAsync(
                record,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // Called only while _sync is held.
    private void Leave()
    {
        if (--_active == 0 && _stopped)
            _drained.TrySetResult();
    }

    /// <summary>
    /// Stop admitting sends, cancel pending waits and join active tracker operations.
    /// The underlying client and packet sender remain caller-owned.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_stopped)
                return new(_retired.Task);

            _stopped = true;
            if (_active == 0)
                _drained.TrySetResult();
        }

        return new(RetireAsync());
    }

    private async Task RetireAsync()
    {
        try
        {
            _stop.Cancel();
            await _drained.Task.ConfigureAwait(false);
            _sendGate.Dispose();
            _stop.Dispose();
            _retired.TrySetResult();
        }
        catch (Exception error)
        {
            _retired.TrySetException(error);
            throw;
        }
    }
}
