using DHMP.Protocol;

namespace DHMP.Server;

/// <summary>
/// FIFO storage used by the Sequential grabber after a record leaves the
/// three-slot receive sweep window.
/// </summary>
/// <remarks>
/// Fixed Backpressure/DropOldest modes allocate segmented ring storage once
/// and perform no per-record managed allocation. Unbounded mode intentionally
/// grows and therefore may allocate/produce GC pressure.
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

    private long _head;
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

        _recordsPerSegment =
            Math.Max(
                1,
                TargetSegmentBytes / recordSize);

        long segmentCountLong =
            (capacityRecords + _recordsPerSegment - 1) /
            _recordsPerSegment;

        if (segmentCountLong > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacityRecords),
                "Sequential backlog requires too many fixed storage segments.");
        }

        _segments =
            new byte[checked((int)segmentCountLong)][];

        long remaining = capacityRecords;

        for (int index = 0;
             index < _segments.Length;
             index++)
        {
            int records =
                checked(
                    (int)Math.Min(
                        remaining,
                        _recordsPerSegment));

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

    public long Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

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

        if (IsUnbounded)
        {
            byte[] owned = record.ToArray();

            lock (_gate)
            {
                _unbounded!.Enqueue(owned);
                _count++;
            }

            Interlocked.Increment(ref _recordsEnqueued);
            return;
        }

        lock (_gate)
        {
            while (_count == _capacityRecords &&
                   _overflowPolicy == DhmpSequentialBacklogOverflowPolicy.Backpressure)
            {
                Interlocked.Increment(ref _backpressureWaits);
                Monitor.Wait(_gate);
            }

            if (_count == _capacityRecords)
            {
                _head = (_head + 1) % _capacityRecords;
                _count--;
                Interlocked.Increment(ref _recordsDropped);
            }

            long tail =
                (_head + _count) %
                _capacityRecords;

            record.CopyTo(
                GetFixedRecordSpan(tail));

            _count++;
        }

        Interlocked.Increment(ref _recordsEnqueued);
    }

    /// <summary>
    /// Consume the oldest FIFO record in-place. The callback executes while
    /// the record owns its ring slot and must not retain the span. Fixed modes
    /// perform no copy or allocation on this synchronous handoff.
    /// </summary>
    public bool TryConsume(
        Action<ReadOnlySpan<byte>> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        lock (_gate)
        {
            if (_count == 0)
                return false;

            if (IsUnbounded)
            {
                byte[] owned =
                    _unbounded!.Dequeue();

                consumer(owned);
                _count--;
            }
            else
            {
                consumer(
                    GetFixedRecordSpan(_head));

                _head =
                    (_head + 1) %
                    _capacityRecords;

                _count--;

                Monitor.Pulse(_gate);
            }
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

        lock (_gate)
        {
            if (_count == 0)
                return false;

            if (IsUnbounded)
            {
                byte[] owned =
                    _unbounded!.Dequeue();

                owned.AsSpan().CopyTo(
                    destination[.._recordSize]);

                _count--;
            }
            else
            {
                GetFixedRecordSpan(_head)
                    .CopyTo(
                        destination[.._recordSize]);

                _head =
                    (_head + 1) %
                    _capacityRecords;

                _count--;

                Monitor.Pulse(_gate);
            }
        }

        Interlocked.Increment(ref _recordsDequeued);
        return true;
    }

    private Span<byte> GetFixedRecordSpan(
        long logicalIndex)
    {
        long segmentIndexLong =
            logicalIndex /
            _recordsPerSegment;

        int segmentIndex =
            checked((int)segmentIndexLong);

        int recordInSegment =
            checked(
                (int)(
                    logicalIndex %
                    _recordsPerSegment));

        return _segments![segmentIndex]
            .AsSpan(
                checked(recordInSegment * _recordSize),
                _recordSize);
    }
}
