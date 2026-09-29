using System.Buffers;

namespace DHMP.Server;

/// <summary>
/// Copies borrowed DHMP publication into bounded owned storage for asynchronous application work.
/// It never blocks the packet-receive callback waiting for application capacity.
/// </summary>
/// <remarks>
/// One RunAsync consumer is supported. Publish may be called concurrently.
/// Dispose only after RunAsync has stopped.
/// </remarks>
public sealed class DhmpBoundedReceiveDispatcher : IDisposable
{
    private sealed class OwnedBatch
    {
        public required byte[] Buffer { get; init; }
        public required int Length { get; init; }
    }

    private readonly object _gate = new();
    private readonly DhmpReceiveDispatchMode _mode;
    private readonly Func<
        ReadOnlyMemory<byte>,
        CancellationToken,
        ValueTask> _consumer;
    private readonly OwnedBatch?[] _queue;
    private readonly SemaphoreSlim _available;

    private int _head;
    private int _tail;
    private int _count;
    private int _running;
    private int _disposed;

    private long _acceptedBatches;
    private long _consumedBatches;
    private long _saturationDrops;
    private long _replacedBatches;

    public DhmpBoundedReceiveDispatcher(
        DhmpReceiveDispatchMode mode,
        int sequentialCapacity,
        Func<
            ReadOnlyMemory<byte>,
            CancellationToken,
            ValueTask> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        if (mode is not DhmpReceiveDispatchMode.SequentialReject and
            not DhmpReceiveDispatchMode.LatestReplace)
            throw new ArgumentOutOfRangeException(nameof(mode));

        if (sequentialCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(sequentialCapacity));

        _mode = mode;
        _consumer = consumer;

        int capacity =
            mode == DhmpReceiveDispatchMode.LatestReplace
                ? 1
                : sequentialCapacity;

        _queue = new OwnedBatch?[capacity];
        _available = new SemaphoreSlim(0, capacity);
    }

    public static DhmpBoundedReceiveDispatcher FromReceivePolicy(
        DHMP.Protocol.DhmpReceivePolicy receivePolicy,
        int sequentialCapacity,
        Func<
            ReadOnlyMemory<byte>,
            CancellationToken,
            ValueTask> consumer)
    {
        var mode =
            receivePolicy.Mode ==
                DHMP.Protocol.DhmpProcessingMode.Latest
                ? DhmpReceiveDispatchMode.LatestReplace
                : DhmpReceiveDispatchMode.SequentialReject;

        return new DhmpBoundedReceiveDispatcher(
            mode,
            sequentialCapacity,
            consumer);
    }

    public DhmpReceiveDispatchMode Mode => _mode;
    public int Capacity => _queue.Length;

    public long AcceptedBatches =>
        Interlocked.Read(ref _acceptedBatches);

    public long ConsumedBatches =>
        Interlocked.Read(ref _consumedBatches);

    public long SaturationDrops =>
        Interlocked.Read(ref _saturationDrops);

    public long ReplacedBatches =>
        Interlocked.Read(ref _replacedBatches);

    public int PendingBatches
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    /// <summary>
    /// Copy one borrowed publication into bounded owned storage.
    /// Returns false only when SequentialReject is saturated or the dispatcher is disposed.
    /// LatestReplace returns true after replacing older pending work.
    /// </summary>
    public bool TryPublish(ReadOnlySpan<byte> batch)
    {
        if (batch.IsEmpty)
            throw new ArgumentException(
                "DHMP async publication cannot be empty.",
                nameof(batch));

        if (Volatile.Read(ref _disposed) != 0)
            return false;

        byte[] buffer =
            ArrayPool<byte>.Shared.Rent(batch.Length);

        batch.CopyTo(
            buffer.AsSpan(0, batch.Length));

        var owned =
            new OwnedBatch
            {
                Buffer = buffer,
                Length = batch.Length
            };

        bool signal = false;
        OwnedBatch? replaced = null;

        lock (_gate)
        {
            if (_disposed != 0)
            {
                Return(owned);
                return false;
            }

            if (_mode ==
                DhmpReceiveDispatchMode.LatestReplace)
            {
                if (_count == 0)
                {
                    _queue[0] = owned;
                    _count = 1;
                    signal = true;
                }
                else
                {
                    replaced = _queue[0];
                    _queue[0] = owned;

                    Interlocked.Increment(
                        ref _replacedBatches);
                }

                Interlocked.Increment(
                    ref _acceptedBatches);
            }
            else
            {
                if (_count == _queue.Length)
                {
                    Return(owned);

                    Interlocked.Increment(
                        ref _saturationDrops);

                    return false;
                }

                _queue[_tail] = owned;
                _tail =
                    (_tail + 1) %
                    _queue.Length;
                _count++;

                signal = true;

                Interlocked.Increment(
                    ref _acceptedBatches);
            }
        }

        if (replaced is not null)
            Return(replaced);

        if (signal)
            _available.Release();

        return true;
    }

    /// <summary>Convenience callback for DhmpServer/peer bindings.</summary>
    public void Publish(ReadOnlySpan<byte> batch)
        => TryPublish(batch);

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (Interlocked.Exchange(
                ref _running,
                1) != 0)
            throw new InvalidOperationException(
                "This DHMP receive dispatcher is already running.");

        try
        {
            while (true)
            {
                await _available.WaitAsync(
                    cancellationToken).ConfigureAwait(false);

                OwnedBatch? owned =
                    Dequeue();

                if (owned is null)
                    continue;

                try
                {
                    await _consumer(
                        owned.Buffer.AsMemory(
                            0,
                            owned.Length),
                        cancellationToken)
                    .ConfigureAwait(false);

                    Interlocked.Increment(
                        ref _consumedBatches);
                }
                finally
                {
                    Return(owned);
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
            return;

        lock (_gate)
        {
            while (_count > 0)
            {
                OwnedBatch? owned =
                    DequeueLocked();

                if (owned is not null)
                    Return(owned);
            }
        }

        _available.Dispose();
    }

    private OwnedBatch? Dequeue()
    {
        lock (_gate)
            return DequeueLocked();
    }

    private OwnedBatch? DequeueLocked()
    {
        if (_count == 0)
            return null;

        if (_mode ==
            DhmpReceiveDispatchMode.LatestReplace)
        {
            OwnedBatch? owned =
                _queue[0];

            _queue[0] = null;
            _count = 0;

            return owned;
        }

        OwnedBatch? result =
            _queue[_head];

        _queue[_head] = null;
        _head =
            (_head + 1) %
            _queue.Length;
        _count--;

        return result;
    }

    private static void Return(
        OwnedBatch owned)
    {
        ArrayPool<byte>.Shared.Return(
            owned.Buffer,
            clearArray: true);
    }
}
