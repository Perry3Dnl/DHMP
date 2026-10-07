using DHMP.Protocol;

namespace DHMP.Server;

/// <summary>
/// FIFO storage used by the Sequential grabber after a record leaves the
/// three-slot receive arrival ring.
/// </summary>
/// <remarks>
/// The fixed Backpressure mode is an SPSC ring: one receive producer and one
/// consumer. Its normal enqueue/consume path does not take a monitor lock and
/// advances cached segment/record cursors without division or modulo.
/// A monitor is entered only when the producer actually reaches a full FIFO.
/// DropOldest retains a synchronized path because producer-side eviction can
/// race the consumer. Unbounded intentionally grows and may allocate.
/// </remarks>
internal sealed class DhmpSequentialBacklog
{
    private const int TargetSegmentBytes = 16 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly int _recordSize;
    private readonly DhmpSequentialBacklogOverflowPolicy _overflowPolicy;
    private readonly long _capacityRecords;
    private readonly int _recordsPerSegment;
    private readonly byte[][]? _segments;
    private readonly Queue<byte[]>? _unbounded;

    private int _headSegment;
    private int _headRecord;
    private int _tailSegment;
    private int _tailRecord;
    private int _backpressureWaiters;

    private long _count;
    private long _recordsEnqueued;
    private long _recordsDequeued;
    private long _recordsDropped;
    private long _backpressureWaits;

    public DhmpSequentialBacklog(
        int recordSize,
        long capacityRecords,
        DhmpSequentialBacklogOverflowPolicy overflowPolicy)
    {
        if (recordSize <= 0 || recordSize > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(recordSize));

        if (!Enum.IsDefined(overflowPolicy))
            throw new ArgumentOutOfRangeException(nameof(overflowPolicy));

        _recordSize = recordSize;
        _overflowPolicy = overflowPolicy;

        if (overflowPolicy == DhmpSequentialBacklogOverflowPolicy.Unbounded)
        {
            _capacityRecords = long.MaxValue;
            _unbounded = new Queue<byte[]>();
            return;
        }

        if (capacityRecords <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityRecords));

        _capacityRecords = capacityRecords;
        _recordsPerSegment = Math.Max(1, TargetSegmentBytes / recordSize);

        long segmentCountLong =
            (capacityRecords + _recordsPerSegment - 1) /
            _recordsPerSegment;

        if (segmentCountLong > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacityRecords),
                "Sequential backlog requires too many fixed storage segments.");
        }

        _segments = new byte[checked((int)segmentCountLong)][];

        long remaining = capacityRecords;

        for (int index = 0; index < _segments.Length; index++)
        {
            int records =
                checked((int)Math.Min(remaining, _recordsPerSegment));

            _segments[index] =
                GC.AllocateUninitializedArray<byte>(
                    checked(records * recordSize));

            remaining -= records;
        }
    }

    public int RecordSize => _recordSize;

    public bool IsUnbounded =>
        _overflowPolicy == DhmpSequentialBacklogOverflowPolicy.Unbounded;

    public long CapacityRecords => _capacityRecords;

    public long Count =>
        Interlocked.Read(ref _count);

    public long RecordsEnqueued =>
        Interlocked.Read(ref _recordsEnqueued);

    public long RecordsDequeued =>
        Interlocked.Read(ref _recordsDequeued);

    public long RecordsDropped =>
        Interlocked.Read(ref _recordsDropped);

    public long BackpressureWaits =>
        Interlocked.Read(ref _backpressureWaits);

    public void Enqueue(ReadOnlySpan<byte> record)
    {
        if (record.Length != _recordSize)
            throw new ArgumentException(
                $"Sequential backlog requires exactly {_recordSize} bytes.",
                nameof(record));

        if (_overflowPolicy == DhmpSequentialBacklogOverflowPolicy.Backpressure)
        {
            EnqueueBackpressure(record);
            return;
        }

        if (IsUnbounded)
        {
            byte[] owned = record.ToArray();

            lock (_gate)
            {
                _unbounded!.Enqueue(owned);
                Interlocked.Increment(ref _count);
            }

            Interlocked.Increment(ref _recordsEnqueued);
            return;
        }

        // DropOldest must synchronize producer-side head movement with the
        // consumer because both may advance the oldest-record cursor.
        lock (_gate)
        {
            if (_count == _capacityRecords)
            {
                AdvanceHeadFixed();
                Interlocked.Decrement(ref _count);
                Interlocked.Increment(ref _recordsDropped);
            }

            record.CopyTo(GetTailFixedSpan());
            AdvanceTailFixed();
            Interlocked.Increment(ref _count);
        }

        Interlocked.Increment(ref _recordsEnqueued);
    }

    private void EnqueueBackpressure(ReadOnlySpan<byte> record)
    {
        while (Interlocked.Read(ref _count) == _capacityRecords)
        {
            Interlocked.Increment(ref _backpressureWaits);
            Interlocked.Increment(ref _backpressureWaiters);

            try
            {
                lock (_gate)
                {
                    while (Interlocked.Read(ref _count) == _capacityRecords)
                        Monitor.Wait(_gate);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _backpressureWaiters);
            }
        }

        // SPSC invariant: only the producer mutates the tail cursor.
        record.CopyTo(GetTailFixedSpan());
        AdvanceTailFixed();

        // Publish the completed FIFO slot only after its bytes are stable.
        Interlocked.Increment(ref _count);
        Interlocked.Increment(ref _recordsEnqueued);
    }

    /// <summary>
    /// Consume the oldest FIFO record in-place. The callback must not retain
    /// the span. Fixed Backpressure mode performs no copy, allocation or lock
    /// while capacity is available.
    /// </summary>
    public bool TryConsume(
        Action<ReadOnlySpan<byte>> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        if (_overflowPolicy == DhmpSequentialBacklogOverflowPolicy.Backpressure)
        {
            if (Interlocked.Read(ref _count) == 0)
                return false;

            consumer(GetHeadFixedSpan());
            AdvanceHeadFixed();

            Interlocked.Decrement(ref _count);
            WakeBackpressuredProducerIfNeeded();

            Interlocked.Increment(ref _recordsDequeued);
            return true;
        }

        lock (_gate)
        {
            if (_count == 0)
                return false;

            if (IsUnbounded)
            {
                byte[] owned = _unbounded!.Dequeue();
                consumer(owned);
            }
            else
            {
                consumer(GetHeadFixedSpan());
                AdvanceHeadFixed();
            }

            Interlocked.Decrement(ref _count);
        }

        Interlocked.Increment(ref _recordsDequeued);
        return true;
    }

    public bool TryDequeue(Span<byte> destination)
    {
        if (destination.Length < _recordSize)
            throw new ArgumentException(
                $"Destination must fit {_recordSize} bytes.",
                nameof(destination));

        if (_overflowPolicy == DhmpSequentialBacklogOverflowPolicy.Backpressure)
        {
            if (Interlocked.Read(ref _count) == 0)
                return false;

            GetHeadFixedSpan().CopyTo(destination[.._recordSize]);
            AdvanceHeadFixed();

            Interlocked.Decrement(ref _count);
            WakeBackpressuredProducerIfNeeded();

            Interlocked.Increment(ref _recordsDequeued);
            return true;
        }

        lock (_gate)
        {
            if (_count == 0)
                return false;

            if (IsUnbounded)
            {
                _unbounded!.Dequeue()
                    .AsSpan()
                    .CopyTo(destination[.._recordSize]);
            }
            else
            {
                GetHeadFixedSpan()
                    .CopyTo(destination[.._recordSize]);
                AdvanceHeadFixed();
            }

            Interlocked.Decrement(ref _count);
        }

        Interlocked.Increment(ref _recordsDequeued);
        return true;
    }

    private void WakeBackpressuredProducerIfNeeded()
    {
        if (Volatile.Read(ref _backpressureWaiters) == 0)
            return;

        lock (_gate)
            Monitor.Pulse(_gate);
    }

    private Span<byte> GetHeadFixedSpan() =>
        _segments![_headSegment]
            .AsSpan(
                _headRecord * _recordSize,
                _recordSize);

    private Span<byte> GetTailFixedSpan() =>
        _segments![_tailSegment]
            .AsSpan(
                _tailRecord * _recordSize,
                _recordSize);

    private void AdvanceHeadFixed()
    {
        int nextRecord = _headRecord + 1;
        byte[] segment = _segments![_headSegment];

        if (nextRecord * _recordSize == segment.Length)
        {
            _headRecord = 0;

            int nextSegment = _headSegment + 1;
            if (nextSegment == _segments.Length)
                nextSegment = 0;

            _headSegment = nextSegment;
            return;
        }

        _headRecord = nextRecord;
    }

    private void AdvanceTailFixed()
    {
        int nextRecord = _tailRecord + 1;
        byte[] segment = _segments![_tailSegment];

        if (nextRecord * _recordSize == segment.Length)
        {
            _tailRecord = 0;

            int nextSegment = _tailSegment + 1;
            if (nextSegment == _segments.Length)
                nextSegment = 0;

            _tailSegment = nextSegment;
            return;
        }

        _tailRecord = nextRecord;
    }
}
