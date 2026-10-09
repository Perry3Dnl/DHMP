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
    private readonly byte[]?[]? _segments;
    private readonly Queue<byte[]>? _unbounded;

    private int _headSegment;
    private int _headRecord;
    private int _tailSegment;
    private int _tailRecord;
    private int _backpressureWaiters;
    private int _dataWaiters;
    private bool _directWriteReserved;

    // Fixed Backpressure is true SPSC. Producer and consumer publish their
    // own monotonic cursors instead of contending on one Interlocked count.
    // This keeps the hot path to acquire/release loads/stores and avoids a
    // locked read-modify-write for every accepted record.
    private long _fixedProducerSequence;
    private long _fixedConsumerSequence;

    // DropOldest / Unbounded still use the synchronized count path.
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

        // Fixed capacity is a logical bound, not an instruction to reserve the
        // entire worst-case byte capacity immediately. Segment storage is retained
        // once touched, but allocated only when the producer first reaches it.
        // This keeps a 1,000,000-record policy usable with large record sizes
        // without reserving tens of gigabytes for an empty FIFO.
        _segments =
            new byte[]?[
                checked((int)segmentCountLong)];
    }

    public int RecordSize => _recordSize;

    public bool IsUnbounded =>
        _overflowPolicy == DhmpSequentialBacklogOverflowPolicy.Unbounded;

    public long CapacityRecords => _capacityRecords;

    public long Count =>
        _overflowPolicy ==
            DhmpSequentialBacklogOverflowPolicy.Backpressure
            ? FixedBackpressureCount
            : Interlocked.Read(ref _count);

    public long RecordsEnqueued =>
        Volatile.Read(ref _recordsEnqueued);

    public long RecordsDequeued =>
        Volatile.Read(ref _recordsDequeued);

    public long RecordsDropped =>
        Volatile.Read(ref _recordsDropped);

    public long BackpressureWaits =>
        Volatile.Read(ref _backpressureWaits);

    private long FixedBackpressureCount
    {
        get
        {
            // Read consumer first. If either side advances concurrently this
            // may be slightly stale, but it cannot report a negative count.
            long consumer =
                Volatile.Read(
                    ref _fixedConsumerSequence);

            long producer =
                Volatile.Read(
                    ref _fixedProducerSequence);

            return producer - consumer;
        }
    }

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

            long newCount;

            lock (_gate)
            {
                _unbounded!.Enqueue(owned);
                newCount = Interlocked.Increment(ref _count);
            }

            if (newCount == 1)
                WakeDataConsumerIfNeeded();

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

            long newCount =
                Interlocked.Increment(ref _count);

            if (newCount == 1)
                Monitor.Pulse(_gate);
        }

        Interlocked.Increment(ref _recordsEnqueued);
    }

    private void EnqueueBackpressure(ReadOnlySpan<byte> record)
    {
        WaitForFixedCapacity();

        // SPSC invariant: only the producer mutates the tail cursor.
        record.CopyTo(GetTailFixedSpan());

        PublishFixedTail();
        _recordsEnqueued++;
    }

    /// <summary>
    /// Reserve the next fixed Backpressure FIFO slot so a transport can receive
    /// directly into backlog-owned memory. The single producer must commit or
    /// cancel exactly once before reserving another slot.
    /// </summary>
    public Memory<byte> BeginDirectWrite()
    {
        if (_overflowPolicy !=
            DhmpSequentialBacklogOverflowPolicy.Backpressure)
        {
            throw new InvalidOperationException(
                "Direct FIFO receive requires the fixed Backpressure overflow policy.");
        }

        if (_directWriteReserved)
        {
            throw new InvalidOperationException(
                "A Sequential FIFO direct-write slot is already reserved.");
        }

        WaitForFixedCapacity();

        byte[] segment =
            EnsureSegmentAllocated(
                _tailSegment);

        _directWriteReserved = true;

        return segment.AsMemory(
            _tailRecord * _recordSize,
            _recordSize);
    }

    /// <summary>
    /// Publish a transport-filled FIFO tail slot. No payload bytes are copied.
    /// </summary>
    public void CommitDirectWrite()
    {
        if (!_directWriteReserved)
        {
            throw new InvalidOperationException(
                "No Sequential FIFO direct-write slot is reserved.");
        }

        PublishFixedTail();
        _directWriteReserved = false;

        _recordsEnqueued++;
    }

    public void CancelDirectWrite()
    {
        if (!_directWriteReserved)
        {
            throw new InvalidOperationException(
                "No Sequential FIFO direct-write slot is reserved.");
        }

        _directWriteReserved = false;
    }

    private void WaitForFixedCapacity()
    {
        while (IsFixedBackpressureFull())
        {
            _backpressureWaits++;
            Interlocked.Increment(ref _backpressureWaiters);

            try
            {
                lock (_gate)
                {
                    while (IsFixedBackpressureFull())
                        Monitor.Wait(_gate);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _backpressureWaiters);
            }
        }
    }

    private bool IsFixedBackpressureFull() =>
        _fixedProducerSequence -
        Volatile.Read(
            ref _fixedConsumerSequence) >=
        _capacityRecords;

    private void PublishFixedTail()
    {
        long producer =
            _fixedProducerSequence;

        bool wasEmpty =
            producer ==
            Volatile.Read(
                ref _fixedConsumerSequence);

        AdvanceTailFixed();

        // Release-publish the record only after its bytes and tail cursor are
        // stable. The consumer's Volatile.Read is the acquire side.
        Volatile.Write(
            ref _fixedProducerSequence,
            producer + 1);

        if (wasEmpty)
            WakeDataConsumerIfNeeded();
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
            long consumerSequence =
                _fixedConsumerSequence;

            if (Volatile.Read(
                    ref _fixedProducerSequence) ==
                consumerSequence)
            {
                return false;
            }

            consumer(GetHeadFixedSpan());
            AdvanceHeadFixed();

            // Release the consumed slot after the callback has finished using
            // it. The producer acquires this cursor before reusing capacity.
            Volatile.Write(
                ref _fixedConsumerSequence,
                consumerSequence + 1);

            WakeBackpressuredProducerIfNeeded();

            _recordsDequeued++;
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

    /// <summary>
    /// Blocking SPSC consumer used by a decoupled receive pump. The normal
    /// producer only enters the monitor when the FIFO transitions from empty
    /// while a consumer is actually waiting.
    /// </summary>
    public void ConsumeUntilCancelled(
        Action<ReadOnlySpan<byte>> consumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        while (true)
        {
            while (TryConsume(consumer))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            cancellationToken.ThrowIfCancellationRequested();

            Interlocked.Increment(ref _dataWaiters);

            try
            {
                lock (_gate)
                {
                    while (Count == 0 &&
                           !cancellationToken.IsCancellationRequested)
                    {
                        Monitor.Wait(
                            _gate,
                            millisecondsTimeout: 50);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _dataWaiters);
            }
        }
    }

    public bool TryDequeue(Span<byte> destination)
    {
        if (destination.Length < _recordSize)
            throw new ArgumentException(
                $"Destination must fit {_recordSize} bytes.",
                nameof(destination));

        if (_overflowPolicy == DhmpSequentialBacklogOverflowPolicy.Backpressure)
        {
            long consumerSequence =
                _fixedConsumerSequence;

            if (Volatile.Read(
                    ref _fixedProducerSequence) ==
                consumerSequence)
            {
                return false;
            }

            GetHeadFixedSpan()
                .CopyTo(destination[.._recordSize]);
            AdvanceHeadFixed();

            Volatile.Write(
                ref _fixedConsumerSequence,
                consumerSequence + 1);

            WakeBackpressuredProducerIfNeeded();

            _recordsDequeued++;
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

    private void WakeDataConsumerIfNeeded()
    {
        if (Volatile.Read(ref _dataWaiters) == 0)
            return;

        lock (_gate)
            Monitor.Pulse(_gate);
    }

    private Span<byte> GetHeadFixedSpan() =>
        GetAllocatedSegment(
                _headSegment)
            .AsSpan(
                _headRecord * _recordSize,
                _recordSize);

    private Span<byte> GetTailFixedSpan() =>
        EnsureSegmentAllocated(
                _tailSegment)
            .AsSpan(
                _tailRecord * _recordSize,
                _recordSize);

    private byte[] EnsureSegmentAllocated(
        int segmentIndex)
    {
        byte[]? segment =
            _segments![segmentIndex];

        if (segment is not null)
            return segment;

        long recordsBefore =
            (long)segmentIndex *
            _recordsPerSegment;

        int records =
            checked(
                (int)Math.Min(
                    _capacityRecords -
                    recordsBefore,
                    _recordsPerSegment));

        segment =
            GC.AllocateUninitializedArray<byte>(
                checked(
                    records *
                    _recordSize));

        _segments[segmentIndex] =
            segment;

        return segment;
    }

    private byte[] GetAllocatedSegment(
        int segmentIndex) =>
        _segments![segmentIndex] ??
        throw new InvalidOperationException(
            "Sequential backlog attempted to consume an unallocated FIFO segment.");

    private void AdvanceHeadFixed()
    {
        int nextRecord = _headRecord + 1;
        byte[] segment =
            GetAllocatedSegment(
                _headSegment);

        if (nextRecord * _recordSize == segment.Length)
        {
            _headRecord = 0;

            int nextSegment = _headSegment + 1;
            if (nextSegment == _segments!.Length)
                nextSegment = 0;

            _headSegment = nextSegment;
            return;
        }

        _headRecord = nextRecord;
    }

    private void AdvanceTailFixed()
    {
        int nextRecord = _tailRecord + 1;
        byte[] segment =
            GetAllocatedSegment(
                _tailSegment);

        if (nextRecord * _recordSize == segment.Length)
        {
            _tailRecord = 0;

            int nextSegment = _tailSegment + 1;
            if (nextSegment == _segments!.Length)
                nextSegment = 0;

            _tailSegment = nextSegment;
            return;
        }

        _tailRecord = nextRecord;
    }
}
