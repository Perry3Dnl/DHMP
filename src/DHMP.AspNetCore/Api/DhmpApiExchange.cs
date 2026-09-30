using System.Collections;
using System.Threading.Channels;

namespace DHMP.AspNetCore;

internal interface IDhmpApiExchange
{
    Task<byte[]> RequestAsync(byte[] message, CancellationToken cancellationToken);
}

/// <summary>Bounded application messages over complete fixed DHMP records. No retries.</summary>
internal sealed class DhmpApiExchange : IDhmpApiExchange, IAsyncDisposable
{
    private sealed class Assembly
    {
        internal required byte[] Bytes;
        internal required BitArray Seen;
        internal required DateTimeOffset Deadline;
        internal int Received;
    }
    private readonly object _gate = new();
    private readonly DhmpApiOptions _options;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _send;
    private readonly Func<byte[], CancellationToken, Task<byte[]>> _dispatch;
    private readonly Dictionary<Guid, TaskCompletionSource<byte[]>> _pending = new();
    private readonly Dictionary<(byte, Guid), Assembly> _assemblies = new();
    private readonly DhmpApiRequestWindow _requestIds = new();
    private readonly Channel<(Guid Id, byte[] Bytes, DateTimeOffset Deadline)> _requests;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task[] _workers;
    private readonly TimeProvider _clock;
    private ulong _sentRequests;
    private bool _retired;
    private Task? _retirement;
    private readonly TaskCompletionSource _outboundDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _rejected;

    internal DhmpApiExchange(DhmpApiOptions options,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        Func<byte[], CancellationToken, Task<byte[]>> dispatch, TimeProvider? clock = null)
    {
        _options = options; _send = send; _dispatch = dispatch; _clock = clock ?? TimeProvider.System;
        _requests = Channel.CreateBounded<(Guid, byte[], DateTimeOffset)>(new BoundedChannelOptions(options.MaximumInFlight)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = false });
        _workers = Enumerable.Range(0, options.MaximumConcurrentRequests).Select(_ => WorkAsync()).ToArray();
    }
    internal long RejectedRecords => Interlocked.Read(ref _rejected);
    internal int PendingCount { get { lock (_gate) return _pending.Count; } }
    internal int AssemblyCount { get { lock (_gate) return _assemblies.Count; } }

    public async Task<byte[]> RequestAsync(byte[] message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (message.Length == 0 || message.Length > _options.MaximumMessageBytes)
            throw new InvalidOperationException("API application message exceeds the configured limit.");
        Guid id;
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            if (_pending.Count >= _options.MaximumInFlight || _sentRequests == ulong.MaxValue)
                throw new InvalidOperationException("DHMP API request capacity or session request limit reached.");
            id = DhmpApiRecord.RequestId(++_sentRequests);
            _pending.Add(id, completion);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        deadline.CancelAfter(_options.RequestTimeout);
        try
        {
            await SendAsync(DhmpApiRecord.Request, id, message, deadline.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stop.IsCancellationRequested)
        { throw new TimeoutException("DHMP API request timed out; remote execution may already have occurred. No retry was sent."); }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(id); _assemblies.Remove((DhmpApiRecord.Response, id));
                if (_retired && _pending.Count == 0) _outboundDrained.TrySetResult();
            }
        }
    }

    internal void Receive(ReadOnlySpan<byte> record)
    {
        if (!DhmpApiRecord.TryParse(record, _options.MaximumMessageBytes, out var fragment))
        { Interlocked.Increment(ref _rejected); return; }
        lock (_gate)
        {
            if (_retired) return;
            ExpireCore();
            var key = (fragment.Kind, fragment.Id);
            if (fragment.Kind == DhmpApiRecord.Response && !_pending.ContainsKey(fragment.Id)) return;
            if (!_assemblies.TryGetValue(key, out var assembly))
            {
                if (_assemblies.Count >= _options.MaximumInFlight * 2) { _rejected++; return; }
                if (fragment.Kind == DhmpApiRecord.Request)
                {
                    if (!_options.AcceptRequests || !_requestIds.TryAccept(DhmpApiRecord.Sequence(fragment.Id))) return;
                }
                assembly = new Assembly { Bytes = new byte[fragment.Total], Seen = new BitArray(fragment.Count),
                    Deadline = _clock.GetUtcNow() + _options.RequestTimeout };
                _assemblies.Add(key, assembly);
            }
            if (assembly.Bytes.Length != fragment.Total || assembly.Seen.Length != fragment.Count)
            { _assemblies.Remove(key); _rejected++; return; }
            int offset = fragment.Index * DhmpApiRecord.ContentSize;
            int length = Math.Min(DhmpApiRecord.ContentSize, fragment.Total - offset);
            if (assembly.Seen[fragment.Index])
            {
                if (!record.Slice(DhmpApiRecord.Header, length).SequenceEqual(assembly.Bytes.AsSpan(offset, length)))
                { _assemblies.Remove(key); _rejected++; }
                return;
            }
            record.Slice(DhmpApiRecord.Header, length).CopyTo(assembly.Bytes.AsSpan(offset));
            assembly.Seen[fragment.Index] = true;
            if (++assembly.Received != fragment.Count) return;
            _assemblies.Remove(key);
            if (fragment.Kind == DhmpApiRecord.Response) _pending[fragment.Id].TrySetResult(assembly.Bytes);
            else if (!_requests.Writer.TryWrite((fragment.Id, assembly.Bytes, assembly.Deadline))) _rejected++;
        }
    }
    internal void Expire() { lock (_gate) ExpireCore(); }
    private void ExpireCore()
    {
        var now = _clock.GetUtcNow();
        foreach (var entry in _assemblies.Where(e => e.Value.Deadline <= now).Select(e => e.Key).ToArray())
            _assemblies.Remove(entry);
    }
    private async Task SendAsync(byte kind, Guid id, byte[] message, CancellationToken token)
    {
        if (message.Length == 0 || message.Length > _options.MaximumMessageBytes)
            throw new InvalidOperationException("API application message exceeds the configured limit.");
        await _sendGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int count = (message.Length + DhmpApiRecord.ContentSize - 1) / DhmpApiRecord.ContentSize;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                await _send(DhmpApiRecord.Encode(kind, id, message, i), token).ConfigureAwait(false);
            }
        }
        finally { _sendGate.Release(); }
    }
    private async Task WorkAsync()
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                TimeSpan remaining = request.Deadline - _clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(remaining);
                try
                {
                    byte[] response = await _dispatch(request.Bytes, deadline.Token).ConfigureAwait(false);
                    await SendAsync(DhmpApiRecord.Response, request.Id, response, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                catch (Exception) { Interlocked.Increment(ref _rejected); } // Caller times out; never retry an action.
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_retirement is not null) return new(_retirement);
            _retired = true;
            _stop.Cancel();
            _requests.Writer.TryComplete();
            foreach (var completion in _pending.Values) completion.TrySetCanceled(_stop.Token);
            _assemblies.Clear();
            if (_pending.Count == 0) _outboundDrained.TrySetResult();
            _retirement = Task.WhenAll(_workers.Append(_outboundDrained.Task));
            return new(_retirement);
        }
    }
}
