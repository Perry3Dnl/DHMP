namespace DHMP.Server;

/// <summary>
/// Three fixed record slots shared by the Latest sweeper and grabber.
/// The sweeper writes directly into the next slot and only advances the
/// published pointer after that slot is complete. Latest grabs one completed
/// slot immediately; Native Smoothing grabs the last three completed slots.
/// The grabber may retry when it races an overwrite, but the sweeper never
/// waits for a grabber.
/// </summary>
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
    /// Begin one sweeper pass. The returned span is the next physical Ring-3
    /// slot itself; callers should write the completed record directly into it
    /// and then call <see cref="CommitSweep"/>.
    /// </summary>
    public Span<byte> BeginSweep()
    {
        if (_sweepActive != 0)
            throw new InvalidOperationException(
                "A Latest sweep is already in progress.");

        long sequence =
            Volatile.Read(ref _publishedSequence) + 1;

        int slot =
            (int)((sequence - 1) % Capacity);

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
    /// Publish the slot completed by <see cref="BeginSweep"/>. This is the
    /// only pointer advance required by the sweeper.
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

        _sweepActive = 0;
    }

    /// <summary>
    /// Abandon an uncommitted sweep. The partially written slot remains
    /// unavailable to grabbers until a later completed sweep replaces it.
    /// This never rolls back or blocks the published Latest pointer.
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
    /// Convenience path for callers that already have a completed swept
    /// record. The core zero-copy integration should prefer BeginSweep /
    /// CommitSweep so the sweeper writes directly into the physical slot.
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
    /// Grab the exact Latest slot that was fully published when the grabber
    /// looked. An in-progress next sweep does not delay this operation.
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
    /// Native-smoothing grab. Returns zero until three complete sweeps exist.
    /// Once available, copies exactly N-2/N-1/N in chronological order.
    /// A race may delay/retry this grabber, but never the sweeper.
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
