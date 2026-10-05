using System.Buffers.Binary;
using System.Diagnostics;

namespace DHMP.Client;

/// <summary>Bounds for the opt-in DECO/1 full-record echo application profile.</summary>
public sealed class DhmpEchoConfirmationOptions
{
    /// <summary>Maximum number of locally initiated echo confirmations waiting concurrently.</summary>
    public int MaximumInFlight { get; set; } = 32;

    /// <summary>Maximum number of inbound echo requests processed concurrently.</summary>
    public int MaximumConcurrentReceives { get; set; } = 8;

    /// <summary>Maximum time allowed for one send or echo operation before delivery becomes unknown.</summary>
    public TimeSpan ConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Evidence that the peer received matching application bytes, not that it processed them.</summary>
public readonly record struct DhmpEchoReceipt(TimeSpan RoundTripTime);

/// <summary>
/// Optional DECO/1 full-record echo confirmation over complete DHMP application records.
/// Both peers must select this schema and use the same authenticated session. No retransmission occurs.
/// </summary>
public sealed class DhmpEchoConfirmation : IAsyncDisposable
{
    /// <summary>Bytes reserved by the DECO/1 application schema inside each fixed DHMP record.</summary>
    public const int ApplicationHeaderBytes = 40;
    private readonly DhmpClient _client;
    private readonly Guid _sessionId;
    private readonly int _maximumInFlight, _maximumReceives;
    private readonly TimeSpan _timeout;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<ulong, Pending> _pending = [];
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong _sequence;
    private int _active, _receiving;
    private bool _stopped;
    private sealed record Pending(byte[] Record, TaskCompletionSource Completion);

    /// <summary>
    /// Create a DECO/1 helper bound to one authenticated session. The helper serializes access to
    /// the supplied client but does not own or dispose that client or its packet sender.
    /// </summary>
    public DhmpEchoConfirmation(DhmpClient client, Guid authenticatedSessionId, DhmpEchoConfirmationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        options ??= new();
        if (authenticatedSessionId == Guid.Empty) throw new ArgumentException("An active authenticated session ID is required.", nameof(authenticatedSessionId));
        if (client.WireContract.RecordSize <= ApplicationHeaderBytes || client.WireContract.RecordSize > 65535 ||
            options.MaximumInFlight is < 1 or > 128 || options.MaximumConcurrentReceives is < 1 or > 128 ||
            options.ConfirmationTimeout <= TimeSpan.Zero || options.ConfirmationTimeout > TimeSpan.FromMinutes(5) ||
            (long)client.WireContract.RecordSize * (options.MaximumInFlight + options.MaximumConcurrentReceives) > 32 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "Echo record size, admission and deadline must be bounded.");
        _client = client; _sessionId = authenticatedSessionId;
        _maximumInFlight = options.MaximumInFlight; _maximumReceives = options.MaximumConcurrentReceives;
        _timeout = options.ConfirmationTimeout;
    }

    /// <summary>Maximum application content bytes available in one DECO/1 record.</summary>
    public int MaximumContentBytes => _client.WireContract.RecordSize - ApplicationHeaderBytes;

    /// <summary>Send once and wait for an exact echo. Timeout/cancellation means delivery is unknown.</summary>
    public async Task<DhmpEchoReceipt> SendAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (content.IsEmpty || content.Length > MaximumContentBytes) throw new ArgumentException("Content must fit one complete echo application record.", nameof(content));
        ulong sequence;
        Pending pending;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_pending.Count >= _maximumInFlight) throw new InvalidOperationException("Echo confirmation admission is full.");
            if (_sequence == ulong.MaxValue) throw new InvalidOperationException("Echo sequence exhausted; establish a fresh session.");
            sequence = ++_sequence;
            byte[] record = new byte[_client.WireContract.RecordSize];
            "DECO"u8.CopyTo(record); record[4] = 1; record[5] = 1;
            _sessionId.TryWriteBytes(record.AsSpan(8, 16), bigEndian: true, out _);
            BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(24, 8), sequence);
            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(32, 4), content.Length);
            content.Span.CopyTo(record.AsSpan(ApplicationHeaderBytes));
            pending = new(record, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending.Add(sequence, pending); _active++;
        }
        long started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token, deadline.Token);
        try
        {
            await SendRecordAsync(pending.Record, linked.Token).ConfigureAwait(false);
            await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            return new(Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
        { throw new TimeoutException("Matching echo was not received before the deadline; delivery is unknown."); }
        finally
        {
            lock (_sync) { _pending.Remove(sequence); Leave(); }
        }
    }

    /// <summary>
    /// Process one owned complete record from the authenticated receive path. Requests are echoed once
    /// and return owned application content; echoes return null and are never echoed again.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(ReadOnlyMemory<byte> record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryParse(record.Span, out byte kind, out ulong sequence, out int length)) return null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (kind == 2)
            {
                if (_pending.TryGetValue(sequence, out var pending) && record.Span[8..].SequenceEqual(pending.Record.AsSpan(8)))
                    pending.Completion.TrySetResult();
                return null;
            }
            if (_receiving >= _maximumReceives) return null; // Bounded overload drop; sender sees uncertainty.
            _receiving++; _active++;
        }
        try
        {
            byte[] echo = record.ToArray(); echo[5] = 2;
            using var deadline = new CancellationTokenSource(_timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token, deadline.Token);
            await SendRecordAsync(echo, linked.Token).ConfigureAwait(false);
            return echo.AsMemory(ApplicationHeaderBytes, length);
        }
        finally { lock (_sync) { _receiving--; Leave(); } }
    }

    private bool TryParse(ReadOnlySpan<byte> record, out byte kind, out ulong sequence, out int length)
    {
        kind = 0; sequence = 0; length = 0;
        if (record.Length != _client.WireContract.RecordSize || !record[..4].SequenceEqual("DECO"u8) || record[4] != 1 ||
            record[5] is not (1 or 2) || record[6] != 0 || record[7] != 0 ||
            new Guid(record.Slice(8, 16), bigEndian: true) != _sessionId || BinaryPrimitives.ReadUInt32BigEndian(record.Slice(36, 4)) != 0) return false;
        sequence = BinaryPrimitives.ReadUInt64BigEndian(record.Slice(24, 8));
        length = BinaryPrimitives.ReadInt32BigEndian(record.Slice(32, 4));
        if (sequence == 0 || length <= 0 || length > MaximumContentBytes) return false;
        foreach (byte padding in record[(ApplicationHeaderBytes + length)..]) if (padding != 0) return false;
        kind = record[5]; return true;
    }

    private async ValueTask SendRecordAsync(ReadOnlyMemory<byte> record, CancellationToken token)
    {
        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _client.SendAsync(record, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        finally { _sendGate.Release(); }
    }
    // Called only under _sync. Active sends/receives own their buffers until this point.
    private void Leave() { if (--_active == 0 && _stopped) _drained.TrySetResult(); }

    /// <summary>
    /// Stop new echo work, cancel pending waits and join active operations without disposing the client.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_stopped) return new(_retired.Task);
            _stopped = true;
            if (_active == 0) _drained.TrySetResult();
        }
        return new(RetireAsync());
    }
    private async Task RetireAsync()
    {
        try
        {
            _stop.Cancel();
            await _drained.Task.ConfigureAwait(false);
            _sendGate.Dispose(); _stop.Dispose();
            _retired.TrySetResult();
        }
        catch (Exception error) { _retired.TrySetException(error); throw; }
    }
}
