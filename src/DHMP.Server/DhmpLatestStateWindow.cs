namespace DHMP.Server;

/// <summary>
/// Fixed three-record receive-side state window for Latest-mode smoothing.
/// Uses permanent slots plus versioned optimistic reads; there is no monitor lock
/// and no steady-state allocation.
/// </summary>
public sealed class DhmpLatestStateWindow
{
    public const int Capacity = 3;

    private readonly int _recordSize;
    private readonly byte[] _slots;
    private readonly long[] _slotVersions = new long[Capacity];

    private int _writerActive;
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
    /// Roll the newest complete records from one already-validated packet into
    /// the fixed Ring-3. Only the newest three records in a larger packet can
    /// affect the retained state window.
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

    /// <summary>
    /// Internal fast path. Caller guarantees a non-empty span containing only complete records.
    /// </summary>
    public void PublishValidatedPacket(ReadOnlySpan<byte> packet)
    {
        var spinner = new SpinWait();

        while (Interlocked.CompareExchange(
                   ref _writerActive,
                   1,
                   0) != 0)
        {
            spinner.SpinOnce();
        }

        try
        {
            PublishValidatedPacketSingleWriter(
                packet);
        }
        finally
        {
            Volatile.Write(
                ref _writerActive,
                0);
        }
    }

    /// <summary>
    /// Single-producer fast path used by one DhmpServer receive path.
    /// Readers remain lock-free and protected by per-slot versions.
    /// </summary>
    internal void PublishValidatedPacketSingleWriter(
        ReadOnlySpan<byte> packet)
    {
        int records =
            packet.Length /
            _recordSize;

        int keep =
            Math.Min(
                records,
                Capacity);

        int first =
            records - keep;

        long sequence =
            Volatile.Read(
                ref _publishedSequence);

        // Obsolete records still advance the logical sequence, but only the
        // newest three records need to be copied into the physical ring.
        sequence += records - keep;

        for (int index = first;
             index < records;
             index++)
        {
            sequence++;

            int slot =
                (int)((sequence - 1) %
                      Capacity);

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

        // Publish the entire packet atomically to readers only after all
        // retained slots are stable. This is also the total records-observed
        // sequence, so no separate Interlocked counter is needed.
        Volatile.Write(
            ref _publishedSequence,
            sequence);
    }

    /// <summary>
    /// Copy N-2/N-1/N in chronological order. If the producer rolls a slot while
    /// it is being copied, the reader retries rather than returning torn state.
    /// </summary>
    public int CopyNewestTo(Span<byte> destination)
    {
        var spinner = new SpinWait();

        while (true)
        {
            long newest =
                Volatile.Read(
                    ref _publishedSequence);

            int count =
                (int)Math.Min(
                    newest,
                    Capacity);

            int required =
                count *
                _recordSize;

            if (destination.Length < required)
            {
                throw new ArgumentException(
                    $"Destination must fit {required} bytes for the current Latest state window.",
                    nameof(destination));
            }

            if (count == 0)
                return 0;

            long first =
                newest - count + 1;

            bool retry = false;

            for (int index = 0;
                 index < count;
                 index++)
            {
                long sequence =
                    first + index;

                int slot =
                    (int)((sequence - 1) %
                          Capacity);

                long expectedVersion =
                    sequence * 2;

                long before =
                    Volatile.Read(
                        ref _slotVersions[slot]);

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

                long after =
                    Volatile.Read(
                        ref _slotVersions[slot]);

                if (after != expectedVersion)
                {
                    retry = true;
                    break;
                }
            }

            if (!retry)
            {
                return count;
            }

            spinner.SpinOnce();
        }
    }
}
