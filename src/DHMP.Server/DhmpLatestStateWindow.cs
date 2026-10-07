namespace DHMP.Server;

/// <summary>
/// Three fixed receive slots forming the arrival Ring-3 before the sweeper/grabber.
/// The receive path writes each complete arriving record into the next physical
/// slot and publishes it only after the slot is complete. The sweeper/grabber
/// then reads the completed arrival state: Latest consumes the newest slot and
/// Native Smoothing consumes N-2/N-1/N. Readers may retry when racing an
/// overwrite, but the receive writer never waits for a reader.
/// </summary>
public delegate void DhmpWindow3Consumer(
    ReadOnlySpan<byte> oldest,
    ReadOnlySpan<byte> middle,
    ReadOnlySpan<byte> newest);

public sealed class DhmpLatestStateWindow
{
    public const int Capacity = 3;

    private readonly int _recordSize;
    private readonly byte[] _slots;
    private readonly long[] _slotVersions = new long[Capacity];

    // One sweeper/producer is supported. Readers may run concurrently.
    private int _sweepActive;
    private long _pendingSequence;
    private int _pendingSlot;
    private long _publishedSequence;
    private int _nextWriterSlot;

    public DhmpLatestStateWindow(int recordSize)
    {
        if (recordSize <= 0 || recordSize > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(recordSize));

        _recordSize = recordSize;
        _slots = GC.AllocateUninitializedArray<byte>(
            checked(recordSize * Capacity));
    }

    public int RecordSize => _recordSize;

    public int Count =>
        (int)Math.Min(
            Volatile.Read(ref _publishedSequence),
            Capacity);

    public long RecordsObserved =>
        Volatile.Read(ref _publishedSequence);

    public long RecordsOverwritten =>
        Math.Max(
            0,
            RecordsObserved - Capacity);

    /// <summary>
    /// Exact-one-record Latest hot path. The caller has already established
    /// record size at the transport/session boundary. No length calculation,
    /// framing, slicing loop or record-count work is performed here.
    /// </summary>
    internal void ReceiveLatestRecordSingleWriter(
        ReadOnlySpan<byte> record,
        Action<ReadOnlySpan<byte>> consumer)
    {
        int recordSize = _recordSize;
        int slot = _nextWriterSlot;
        long sequence = _publishedSequence + 1;

        Volatile.Write(
            ref _slotVersions[slot],
            sequence * 2 - 1);

        record.CopyTo(
            _slots.AsSpan(
                slot * recordSize,
                recordSize));

        Volatile.Write(
            ref _slotVersions[slot],
            sequence * 2);

        int nextSlot = slot + 1;
        if (nextSlot == Capacity)
            nextSlot = 0;

        _nextWriterSlot = nextSlot;

        Volatile.Write(
            ref _publishedSequence,
            sequence);

        consumer(
            _slots.AsSpan(
                slot * recordSize,
                recordSize));
    }

    /// <summary>
    /// Canonical single-writer Latest hot path. Each complete received record
    /// is copied once from transport-owned packet storage into the physical
    /// arrival Ring-3. Slot rotation is maintained as 0->1->2->0 without a
    /// modulo/division. Sweeper/grabber publication is metadata-only.
    /// </summary>
    internal void ReceiveLatestPacketSingleWriter(
        ReadOnlySpan<byte> completeRecords,
        Action<ReadOnlySpan<byte>> consumer)
    {
        int recordSize = _recordSize;
        int slot = _nextWriterSlot;
        long sequence = _publishedSequence;

        for (int offset = 0;
             offset < completeRecords.Length;
             offset += recordSize)
        {
            sequence++;

            Volatile.Write(
                ref _slotVersions[slot],
                sequence * 2 - 1);

            completeRecords.Slice(
                    offset,
                    recordSize)
                .CopyTo(
                    _slots.AsSpan(
                        slot * recordSize,
                        recordSize));

            Volatile.Write(
                ref _slotVersions[slot],
                sequence * 2);

            slot++;
            if (slot == Capacity)
                slot = 0;
        }

        _nextWriterSlot = slot;
        Volatile.Write(
            ref _publishedSequence,
            sequence);

        int newestSlot =
            slot == 0
                ? Capacity - 1
                : slot - 1;

        consumer(
            _slots.AsSpan(
                newestSlot * recordSize,
                recordSize));
    }

    /// <summary>
    /// Begin filling the next physical arrival slot in Ring-3. The returned
    /// span is the receive slot itself; callers write one complete received
    /// record into it and then call <see cref="CommitSweep"/>. The historical
    /// method name is retained for API compatibility.
    /// </summary>
    public Span<byte> BeginSweep()
    {
        if (_sweepActive != 0)
            throw new InvalidOperationException(
                "A Latest sweep is already in progress.");

        long sequence =
            Volatile.Read(ref _publishedSequence) + 1;

        int slot = _nextWriterSlot;

        _sweepActive = 1;
        _pendingSequence = sequence;
        _pendingSlot = slot;

        // Odd means that this physical slot is currently being swept/written.
        Volatile.Write(
            ref _slotVersions[slot],
            sequence * 2 - 1);

        return _slots.AsSpan(
            slot * _recordSize,
            _recordSize);
    }

    /// <summary>
    /// Publish the arrival slot completed by <see cref="BeginSweep"/>. This is
    /// the only pointer advance required before the sweeper/grabber can observe
    /// the newly received record.
    /// </summary>
    public void CommitSweep()
    {
        if (_sweepActive == 0)
            throw new InvalidOperationException(
                "No Latest sweep is in progress.");

        long sequence = _pendingSequence;
        int slot = _pendingSlot;

        Volatile.Write(
            ref _slotVersions[slot],
            sequence * 2);

        // Readers only discover the new slot after its bytes are stable.
        Volatile.Write(
            ref _publishedSequence,
            sequence);

        slot++;
        if (slot == Capacity)
            slot = 0;

        _nextWriterSlot = slot;
        _sweepActive = 0;
    }

    /// <summary>
    /// Abandon an uncommitted arrival write. The partially written slot remains
    /// unavailable to the sweeper/grabbers until a later complete arrival
    /// replaces it. This never rolls back or blocks the published pointer.
    /// </summary>
    public void CancelSweep()
    {
        if (_sweepActive == 0)
            return;

        // The bytes may already be partially overwritten, so never make the
        // previous version visible again. A smoothing grabber may retry until
        // this physical slot is replaced by a later completed sweep.
        Volatile.Write(
            ref _slotVersions[_pendingSlot],
            0);

        _sweepActive = 0;
    }

    /// <summary>
    /// Convenience path for callers that already have one complete received
    /// record. Direct receive integrations should prefer BeginSweep /
    /// CommitSweep so the transport writes directly into the physical arrival
    /// slot.
    /// </summary>
    public void Sweep(ReadOnlySpan<byte> record)
    {
        if (record.Length != _recordSize)
            throw new ArgumentException(
                $"Latest sweep requires exactly {_recordSize} bytes.",
                nameof(record));

        Span<byte> slot = BeginSweep();

        try
        {
            record.CopyTo(slot);
            CommitSweep();
        }
        catch
        {
            CancelSweep();
            throw;
        }
    }

    /// <summary>
    /// Snapshot compatibility API for the newest fully received slot.
    /// This copies into caller-owned storage and is not used by the canonical
    /// zero-copy Latest grabber hot path.
    /// </summary>
    public int CopyLatestTo(Span<byte> destination)
    {
        if (destination.Length < _recordSize)
            throw new ArgumentException(
                $"Destination must fit {_recordSize} bytes.",
                nameof(destination));

        var spinner = new SpinWait();

        while (true)
        {
            long sequence =
                Volatile.Read(ref _publishedSequence);

            if (sequence == 0)
                return 0;

            int slot =
                (int)((sequence - 1) % Capacity);

            long expectedVersion =
                sequence * 2;

            long before =
                Volatile.Read(ref _slotVersions[slot]);

            if (before != expectedVersion)
            {
                spinner.SpinOnce();
                continue;
            }

            _slots.AsSpan(
                    slot * _recordSize,
                    _recordSize)
                .CopyTo(
                    destination[.._recordSize]);

            long after =
                Volatile.Read(ref _slotVersions[slot]);

            if (after == expectedVersion)
                return 1;

            spinner.SpinOnce();
        }
    }


    /// <summary>
    /// Single-producer helper used by the Sequential grabber immediately after
    /// CommitSweep and before the producer starts another sweep. The returned
    /// span aliases the current physical slot and must not escape that handoff.
    /// </summary>
    internal ReadOnlySpan<byte> GetLatestPublishedSlotSingleWriter()
    {
        if (_sweepActive != 0)
            throw new InvalidOperationException(
                "Cannot grab a slot while a sweep is still in progress.");

        long sequence =
            Volatile.Read(ref _publishedSequence);

        if (sequence == 0)
            return ReadOnlySpan<byte>.Empty;

        int slot =
            (int)((sequence - 1) % Capacity);

        long expectedVersion =
            sequence * 2;

        if (Volatile.Read(ref _slotVersions[slot]) != expectedVersion)
        {
            throw new InvalidOperationException(
                "Latest published sweep slot is not stable.");
        }

        return _slots.AsSpan(
            slot * _recordSize,
            _recordSize);
    }

    /// <summary>
    /// Synchronous single-producer grabber used by the canonical server path.
    /// The callback receives the currently published physical slot directly;
    /// no copy or allocation is required. The callback must not retain the span.
    /// </summary>
    internal int ConsumeLatestPublishedSlotSingleWriter(
        Action<ReadOnlySpan<byte>> consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        ReadOnlySpan<byte> slot =
            GetLatestPublishedSlotSingleWriter();

        if (slot.IsEmpty)
            return 0;

        consumer(slot);
        return 1;
    }

    /// <summary>
    /// Native-smoothing sweep/grab. Returns zero until three complete arrivals
    /// exist. Once available, copies exactly N-2/N-1/N in chronological order.
    /// A race may delay/retry the reader, but never the receive writer.
    /// </summary>
    /// <summary>
    /// Zero-copy Native Smoothing grabber for the single-writer synchronous
    /// receive path. The three spans alias the physical arrival Ring-3 slots
    /// in chronological N-2/N-1/N order and are valid only for the callback.
    /// No record bytes are copied by the sweeper or grabber.
    /// </summary>
    internal int ConsumeCompletedWindow3SingleWriter(
        DhmpWindow3Consumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);

        if (_sweepActive != 0)
            throw new InvalidOperationException(
                "Cannot grab the smoothing window while an arrival write is in progress.");

        long newest =
            Volatile.Read(ref _publishedSequence);

        if (newest < Capacity)
            return 0;

        long firstSequence =
            newest - Capacity + 1;

        ReadOnlySpan<byte> first = GetStableSlotSingleWriter(firstSequence);
        ReadOnlySpan<byte> second = GetStableSlotSingleWriter(firstSequence + 1);
        ReadOnlySpan<byte> third = GetStableSlotSingleWriter(firstSequence + 2);

        consumer(first, second, third);
        return Capacity;
    }

    private ReadOnlySpan<byte> GetStableSlotSingleWriter(
        long sequence)
    {
        int slot =
            (int)((sequence - 1) % Capacity);

        long expectedVersion =
            sequence * 2;

        if (Volatile.Read(ref _slotVersions[slot]) != expectedVersion)
        {
            throw new InvalidOperationException(
                "Requested receive-ring slot is not stable.");
        }

        return _slots.AsSpan(
            slot * _recordSize,
            _recordSize);
    }

    /// <summary>
    /// Snapshot compatibility API. This copies Ring-3 bytes into caller-owned
    /// contiguous storage. It is not used by the canonical sweeper/grabber hot
    /// path; use ConsumeCompletedWindow3SingleWriter for zero-copy handoff.
    /// </summary>
    public int CopyCompletedWindow3To(
        Span<byte> destination)
    {
        int required =
            Capacity *
            _recordSize;

        if (destination.Length < required)
            throw new ArgumentException(
                $"Destination must fit {required} bytes.",
                nameof(destination));

        var spinner = new SpinWait();

        while (true)
        {
            long newest =
                Volatile.Read(ref _publishedSequence);

            if (newest < Capacity)
                return 0;

            long first =
                newest - Capacity + 1;

            bool retry = false;

            for (int index = 0;
                 index < Capacity;
                 index++)
            {
                long sequence =
                    first + index;

                int slot =
                    (int)((sequence - 1) % Capacity);

                long expectedVersion =
                    sequence * 2;

                long before =
                    Volatile.Read(ref _slotVersions[slot]);

                if (before != expectedVersion)
                {
                    if (before == 0)
                        return 0;

                    retry = true;
                    break;
                }

                _slots.AsSpan(
                        slot * _recordSize,
                        _recordSize)
                    .CopyTo(
                        destination.Slice(
                            index * _recordSize,
                            _recordSize));

                long after =
                    Volatile.Read(ref _slotVersions[slot]);

                if (after != expectedVersion)
                {
                    retry = true;
                    break;
                }
            }

            if (!retry)
                return Capacity;

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// Compatibility API: copy up to the newest three published sweep slots.
    /// New Native Smoothing code should use CopyCompletedWindow3To so a
    /// partial window is never consumed as a smoothing window.
    /// </summary>
    public int CopyNewestTo(Span<byte> destination)
    {
        long newest =
            Volatile.Read(ref _publishedSequence);

        int count =
            (int)Math.Min(
                newest,
                Capacity);

        if (count == 0)
            return 0;

        int required =
            count *
            _recordSize;

        if (destination.Length < required)
            throw new ArgumentException(
                $"Destination must fit {required} bytes for the current Latest state window.",
                nameof(destination));

        if (count == Capacity)
            return CopyCompletedWindow3To(destination);

        // During startup only one/two completed slots exist. There cannot yet
        // be an overwrite of those slots, but still validate versions.
        var spinner = new SpinWait();

        while (true)
        {
            newest =
                Volatile.Read(ref _publishedSequence);

            count =
                (int)Math.Min(
                    newest,
                    Capacity);

            long first =
                newest - count + 1;

            bool retry = false;

            for (int index = 0;
                 index < count;
                 index++)
            {
                long sequence = first + index;
                int slot =
                    (int)((sequence - 1) % Capacity);
                long expectedVersion = sequence * 2;

                long before =
                    Volatile.Read(ref _slotVersions[slot]);

                if (before != expectedVersion)
                {
                    retry = true;
                    break;
                }

                _slots.AsSpan(
                        slot * _recordSize,
                        _recordSize)
                    .CopyTo(
                        destination.Slice(
                            index * _recordSize,
                            _recordSize));

                if (Volatile.Read(
                        ref _slotVersions[slot]) != expectedVersion)
                {
                    retry = true;
                    break;
                }
            }

            if (!retry)
                return count;

            spinner.SpinOnce();
        }
    }

    /// <summary>
    /// Compatibility helper for an already-complete packet. This is not the
    /// zero-copy sweeper path; it preserves the old public API for callers that
    /// explicitly want to copy packet records into the sweep slots.
    /// </summary>
    public void PublishPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.IsEmpty ||
            packet.Length % _recordSize != 0)
        {
            throw new ArgumentException(
                "Latest state window requires only complete records.",
                nameof(packet));
        }

        PublishValidatedPacket(packet);
    }

    public void PublishValidatedPacket(
        ReadOnlySpan<byte> packet)
    {
        PublishValidatedPacketSingleWriter(packet);
    }

    internal void PublishValidatedPacketSingleWriter(
        ReadOnlySpan<byte> packet)
    {
        int records =
            packet.Length /
            _recordSize;

        if (records <= 0)
            return;

        int keep =
            Math.Min(
                records,
                Capacity);

        int first =
            records - keep;

        long baseSequence =
            Volatile.Read(ref _publishedSequence);

        long sequence =
            baseSequence +
            records -
            keep;

        // Write only the records that can still be visible in the final
        // three-slot window, but do not publish the pointer until all are
        // complete.
        for (int index = first;
             index < records;
             index++)
        {
            sequence++;

            int slot =
                (int)((sequence - 1) % Capacity);

            long stableVersion =
                sequence * 2;

            Volatile.Write(
                ref _slotVersions[slot],
                stableVersion - 1);

            packet.Slice(
                    index * _recordSize,
                    _recordSize)
                .CopyTo(
                    _slots.AsSpan(
                        slot * _recordSize,
                        _recordSize));

            Volatile.Write(
                ref _slotVersions[slot],
                stableVersion);
        }

        Volatile.Write(
            ref _publishedSequence,
            baseSequence + records);
    }
}
