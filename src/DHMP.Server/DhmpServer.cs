using DHMP.Protocol;

namespace DHMP.Server;

/// <summary>
/// Receiver-side protocol facade for one configured DHMP wire contract and
/// local receive policy.
/// </summary>
public sealed class DhmpServer
{
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpReceivePolicy _receivePolicy;
    private readonly DhmpPacketProcessor _processor;
    private readonly DhmpLatestStateWindow _receiveSweepSlots;
    private readonly object _sequentialBacklogGate = new();
    private DhmpSequentialBacklog? _sequentialBacklog;

    public DhmpServer(
        DhmpWireContract wireContract,
        DhmpReceivePolicy receivePolicy = default)
    {
        wireContract.Validate();

        if (receivePolicy == default)
            receivePolicy =
                new DhmpReceivePolicy();

        receivePolicy.Validate(
            wireContract);

        _wireContract =
            wireContract;

        _receivePolicy =
            receivePolicy;

        _processor =
            new DhmpPacketProcessor(
                wireContract,
                receivePolicy);

        // All receive modes share the same physical three-slot sweeper.
        // Mode only changes grabber behavior after a slot is complete:
        // Latest grabs one, Native Smoothing grabs three, Sequential moves
        // every completed slot into its FIFO backlog.
        _receiveSweepSlots =
            new DhmpLatestStateWindow(
                wireContract.RecordSize);
    }

    public DhmpWireContract WireContract =>
        _wireContract;

    public DhmpReceivePolicy ReceivePolicy =>
        _receivePolicy;

    public bool LatestGrabberAvailable =>
        _receivePolicy.Mode == DhmpProcessingMode.Latest;

    public bool SequentialGrabberAvailable =>
        _receivePolicy.Mode == DhmpProcessingMode.Sequential;

    public long SequentialBacklogCount =>
        _sequentialBacklog?.Count ?? 0;

    public long SequentialBacklogDroppedRecords =>
        _sequentialBacklog?.RecordsDropped ?? 0;

    public long SequentialBackpressureWaits =>
        _sequentialBacklog?.BackpressureWaits ?? 0;

    public bool NativeSmoothingEnabled =>
        _receivePolicy.NativeSmoothing;

    public int NativeSmoothingRecordCount =>
        NativeSmoothingEnabled
            ? _receiveSweepSlots.Count
            : 0;

    /// <summary>
    /// Canonical receive path. All modes pass through the shared three-slot
    /// sweeper/grabber architecture; only the grabber policy differs.
    /// Sequential moves every complete record through its FIFO backlog,
    /// Latest publishes one completed slot, and Native Smoothing keeps the
    /// exact same Latest packet path while exposing the three-slot window to
    /// its downstream grabber.
    /// </summary>
    public void ProcessPacket(
        ReadOnlySpan<byte> packet,
        Action<ReadOnlySpan<byte>> publishBatch)
    {
        ArgumentNullException.ThrowIfNull(
            publishBatch);

        int recordSize =
            _wireContract.RecordSize;

        int completeBytes =
            packet.Length /
            recordSize *
            recordSize;

        if (completeBytes == 0)
            return;

        ReadOnlySpan<byte> complete =
            packet[..completeBytes];

        if (_receivePolicy.Mode ==
            DhmpProcessingMode.Sequential)
        {
            DhmpSequentialBacklog backlog =
                GetSequentialBacklog();

            for (int offset = 0;
                 offset < complete.Length;
                 offset += recordSize)
            {
                Span<byte> slot =
                    _receiveSweepSlots.BeginSweep();

                try
                {
                    complete.Slice(
                            offset,
                            recordSize)
                        .CopyTo(slot);

                    _receiveSweepSlots.CommitSweep();

                    backlog.Enqueue(
                        _receiveSweepSlots
                            .GetLatestPublishedSlotSingleWriter());
                }
                catch
                {
                    _receiveSweepSlots.CancelSweep();
                    throw;
                }
            }

            while (backlog.TryConsume(
                       publishBatch))
            {
            }

            return;
        }

        ReadOnlySpan<byte> latest =
            complete[^recordSize..];

        Span<byte> latestSlot =
            _receiveSweepSlots.BeginSweep();

        try
        {
            latest.CopyTo(
                latestSlot);

            _receiveSweepSlots.CommitSweep();

            _receiveSweepSlots
                .ConsumeLatestPublishedSlotSingleWriter(
                    publishBatch);
        }
        catch
        {
            _receiveSweepSlots.CancelSweep();
            throw;
        }
    }

    /// <summary>
    /// Sweeper API: obtain the next physical Latest slot and write the complete
    /// record directly into it. This does not depend on Native Smoothing.
    /// </summary>
    public Span<byte> BeginLatestSweep() =>
        GetLatestSweepSlots()
            .BeginSweep();

    public void CommitLatestSweep() =>
        GetLatestSweepSlots()
            .CommitSweep();

    public void CancelLatestSweep() =>
        GetLatestSweepSlots()
            .CancelSweep();

    /// <summary>
    /// Convenience path when a completed sweep record already exists elsewhere.
    /// Direct integrations should prefer BeginLatestSweep/CommitLatestSweep.
    /// </summary>
    public void SweepLatest(
        ReadOnlySpan<byte> record) =>
        GetLatestSweepSlots()
            .Sweep(record);

    /// <summary>
    /// Latest grabber: immediately copy the slot fully published at the moment
    /// of the grab. It does not wait for a three-slot smoothing window.
    /// </summary>
    public int CopyLatest(
        Span<byte> destination) =>
        GetLatestSweepSlots()
            .CopyLatestTo(destination);

    /// <summary>
    /// Native-smoothing grabber: consume exactly the last three fully swept
    /// slots. Returns zero until a complete three-slot window exists.
    /// </summary>
    public int CopyNativeSmoothingWindow(
        Span<byte> destination)
    {
        if (!NativeSmoothingEnabled)
            return 0;

        return GetLatestSweepSlots()
            .CopyCompletedWindow3To(
                destination);
    }

    /// <summary>
    /// Allocate/prepare the configured Sequential backlog during connection or
    /// application setup so the first received record does not pay allocation
    /// cost. Returns the configured record capacity; Unbounded returns
    /// long.MaxValue.
    /// </summary>
    public long PrepareSequentialBacklog()
    {
        DhmpSequentialBacklog backlog =
            GetSequentialBacklog();

        return backlog.CapacityRecords;
    }

    /// <summary>
    /// Sequential sweeper: obtain the next shared physical receive slot.
    /// The producer remains unrestricted until the FIFO backlog is full.
    /// </summary>
    public Span<byte> BeginSequentialSweep()
    {
        EnsureSequentialMode();
        return _receiveSweepSlots.BeginSweep();
    }

    /// <summary>
    /// Commit one Sequential sweep and let the grabber move that exact record
    /// into FIFO storage. Backpressure waits only when a fixed lossless backlog
    /// is full. DropOldest and Unbounded never wait for consumer capacity.
    /// </summary>
    public void CommitSequentialSweep()
    {
        EnsureSequentialMode();

        _receiveSweepSlots.CommitSweep();

        GetSequentialBacklog()
            .Enqueue(
                _receiveSweepSlots
                    .GetLatestPublishedSlotSingleWriter());
    }

    public void CancelSequentialSweep()
    {
        EnsureSequentialMode();
        _receiveSweepSlots.CancelSweep();
    }

    public void SweepSequential(
        ReadOnlySpan<byte> record)
    {
        Span<byte> slot =
            BeginSequentialSweep();

        try
        {
            record.CopyTo(slot);
            CommitSequentialSweep();
        }
        catch
        {
            CancelSequentialSweep();
            throw;
        }
    }

    /// <summary>
    /// Compatibility packet entry point for the new Sequential architecture.
    /// Packet validation remains in DhmpPacketProcessor; every complete record
    /// then passes through the shared sweeper and FIFO grabber in order.
    /// </summary>
    public void ProcessPacketToSequentialBacklog(
        ReadOnlySpan<byte> packet)
    {
        EnsureSequentialMode();

        _processor.Process(
            packet,
            SweepSequentialBatch);
    }

    /// <summary>
    /// Pop the oldest Sequential record. Removing an item immediately frees
    /// fixed-backlog capacity and can release a backpressured sweeper.
    /// </summary>
    public bool TryDequeueSequential(
        Span<byte> destination)
    {
        EnsureSequentialMode();

        return GetSequentialBacklog()
            .TryDequeue(destination);
    }

    private void SweepSequentialBatch(
        ReadOnlySpan<byte> batch)
    {
        int recordSize =
            _wireContract.RecordSize;

        for (int offset = 0;
             offset < batch.Length;
             offset += recordSize)
        {
            SweepSequential(
                batch.Slice(
                    offset,
                    recordSize));
        }
    }

    private DhmpSequentialBacklog GetSequentialBacklog()
    {
        EnsureSequentialMode();

        if (_sequentialBacklog is not null)
            return _sequentialBacklog;

        lock (_sequentialBacklogGate)
        {
            _sequentialBacklog ??=
                new DhmpSequentialBacklog(
                    _wireContract.RecordSize,
                    _receivePolicy.SequentialBacklogCapacityRecords,
                    _receivePolicy.SequentialBacklogOverflowPolicy);

            return _sequentialBacklog;
        }
    }

    private void EnsureSequentialMode()
    {
        if (_receivePolicy.Mode != DhmpProcessingMode.Sequential)
        {
            throw new InvalidOperationException(
                "Sequential sweep/backlog APIs require DhmpProcessingMode.Sequential.");
        }
    }

    private DhmpLatestStateWindow GetLatestSweepSlots()
    {
        if (_receivePolicy.Mode != DhmpProcessingMode.Latest)
        {
            throw new InvalidOperationException(
                "Latest sweep/grab APIs require DhmpProcessingMode.Latest.");
        }

        return _receiveSweepSlots;
    }
}
