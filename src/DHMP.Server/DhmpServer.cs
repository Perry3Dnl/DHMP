using System.Buffers;
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
    private readonly DhmpLatestStateWindow? _receiveSweepSlots;
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

        // Normal Sequential and Latest own the physical three-slot arrival
        // ring. UnsafeSequential intentionally owns no Ring-3 payload storage:
        // its fixed-slot transport target is the FIFO tail itself.
        _receiveSweepSlots =
            receivePolicy.Mode ==
                DhmpProcessingMode.UnsafeSequential
                ? null
                : new DhmpLatestStateWindow(
                    wireContract.RecordSize);
    }

    public DhmpWireContract WireContract =>
        _wireContract;

    public DhmpReceivePolicy ReceivePolicy =>
        _receivePolicy;

    public bool LatestGrabberAvailable =>
        _receivePolicy.Mode == DhmpProcessingMode.Latest;

    public bool SequentialGrabberAvailable =>
        IsSequentialFamilyMode();

    public long SequentialBacklogCount =>
        _sequentialBacklog?.Count ?? 0;

    public long SequentialBacklogDroppedRecords =>
        _sequentialBacklog?.RecordsDropped ?? 0;

    public long SequentialBackpressureWaits =>
        _sequentialBacklog?.BackpressureWaits ?? 0;

    /// <summary>
    /// Number of records completed by the shared physical receive sweeper.
    /// This advances for Sequential, Latest and Native Smoothing alike.
    /// </summary>
    public long ReceiveSweepRecordsObserved =>
        _receiveSweepSlots?.RecordsObserved ?? 0;

    public bool NativeSmoothingEnabled =>
        _receivePolicy.NativeSmoothing;

    public int NativeSmoothingRecordCount =>
        NativeSmoothingEnabled
            ? GetReceiveSweepSlots().Count
            : 0;

    /// <summary>
    /// Prevalidated exact-one-record hot path for negotiated fixed-slot
    /// transports. The caller must supply exactly one negotiated record.
    /// No packet framing/record-count calculation is performed here.
    /// </summary>
    public void ProcessNegotiatedRecord(
        ReadOnlySpan<byte> record,
        Action<ReadOnlySpan<byte>> publishBatch)
    {
        if (_receivePolicy.Mode == DhmpProcessingMode.Latest)
        {
            GetReceiveSweepSlots().ReceiveLatestRecordSingleWriter(
                record,
                publishBatch);
            return;
        }

        DhmpSequentialBacklog backlog =
            GetSequentialBacklog();

        if (_receivePolicy.Mode ==
            DhmpProcessingMode.UnsafeSequential)
        {
            // Compatibility/pre-buffered path: the record already exists
            // elsewhere, so one copy into FIFO ownership is unavoidable.
            // Direct transports avoid even this copy via Begin/Commit slot.
            backlog.Enqueue(record);
        }
        else
        {
            Span<byte> slot =
                GetReceiveSweepSlots().BeginSweep();

            record.CopyTo(slot);

            backlog.Enqueue(
                _receiveSweepSlots
                    .CommitSweepAndGetSlotSingleWriter());
        }

        if (!backlog.TryConsume(publishBatch))
        {
            throw new InvalidOperationException(
                "Sequential grabber lost a negotiated record before synchronous publication.");
        }
    }

    /// <summary>
    /// Reserve the negotiated fixed-size transport receive destination.
    /// Sequential/Latest return Ring-3 memory. UnsafeSequential returns the
    /// next FIFO tail slot directly, eliminating Ring-3 payload ownership.
    /// The caller must commit or cancel exactly once.
    /// </summary>
    internal Memory<byte> BeginNegotiatedReceiveSlot()
    {
        if (_receivePolicy.Mode ==
            DhmpProcessingMode.UnsafeSequential)
        {
            return GetSequentialBacklog()
                .BeginDirectWrite();
        }

        return GetReceiveSweepSlots()
            .BeginSweepMemorySingleWriter();
    }

    /// <summary>
    /// Publish a directly received negotiated slot. Latest hands the exact
    /// Ring-3 slot to the consumer. Sequential transfers ownership into its
    /// FIFO before synchronous compatibility publication.
    /// </summary>
    internal void CommitNegotiatedReceiveSlot(
        Action<ReadOnlySpan<byte>> publishBatch)
    {
        ArgumentNullException.ThrowIfNull(publishBatch);

        if (_receivePolicy.Mode ==
            DhmpProcessingMode.UnsafeSequential)
        {
            DhmpSequentialBacklog unsafeBacklog =
                GetSequentialBacklog();

            unsafeBacklog.CommitDirectWrite();

            if (!unsafeBacklog.TryConsume(publishBatch))
            {
                throw new InvalidOperationException(
                    "UnsafeSequential FIFO lost a committed record before synchronous publication.");
            }

            return;
        }

        ReadOnlySpan<byte> slot =
            _receiveSweepSlots.CommitSweepAndGetSlotSingleWriter();

        if (_receivePolicy.Mode == DhmpProcessingMode.Latest)
        {
            publishBatch(slot);
            return;
        }

        DhmpSequentialBacklog backlog =
            GetSequentialBacklog();

        backlog.Enqueue(slot);

        if (!backlog.TryConsume(publishBatch))
        {
            throw new InvalidOperationException(
                "Sequential grabber lost a negotiated record before synchronous publication.");
        }
    }

    internal void CancelNegotiatedReceiveSlot()
    {
        if (_receivePolicy.Mode ==
            DhmpProcessingMode.UnsafeSequential)
        {
            GetSequentialBacklog()
                .CancelDirectWrite();
            return;
        }

        GetReceiveSweepSlots().CancelSweep();
    }

    internal void CommitNegotiatedReceiveSlotToSequentialBacklog()
    {
        EnsureSequentialFamilyMode();

        DhmpSequentialBacklog backlog =
            GetSequentialBacklog();

        if (_receivePolicy.Mode ==
            DhmpProcessingMode.UnsafeSequential)
        {
            backlog.CommitDirectWrite();
            return;
        }

        ReadOnlySpan<byte> slot =
            _receiveSweepSlots.CommitSweepAndGetSlotSingleWriter();

        backlog.Enqueue(slot);
    }

    internal void ConsumeSequentialUntilCancelled(
        Action<ReadOnlySpan<byte>> consumer,
        CancellationToken cancellationToken)
    {
        EnsureSequentialFamilyMode();

        GetSequentialBacklog()
            .ConsumeUntilCancelled(
                consumer,
                cancellationToken);
    }

    /// <summary>
    /// Canonical receive path. Complete records first enter the shared
    /// three-slot arrival ring. The sweeper/grabber policy then consumes those
    /// completed slots. Sequential moves every record through its FIFO backlog,
    /// Latest publishes the newest completed slot, and Native Smoothing exposes
    /// the completed N-2/N-1/N arrival window to its downstream grabber.
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
                    GetReceiveSweepSlots().BeginSweep();

                try
                {
                    complete.Slice(
                            offset,
                            recordSize)
                        .CopyTo(slot);

                    backlog.Enqueue(
                        _receiveSweepSlots
                            .CommitSweepAndGetSlotSingleWriter());
                }
                catch
                {
                    GetReceiveSweepSlots().CancelSweep();
                    throw;
                }
            }

            PublishSequentialCompatibilityBatch(
                backlog,
                completeBytes,
                recordSize,
                publishBatch);

            return;
        }

        if (_receivePolicy.Mode ==
            DhmpProcessingMode.UnsafeSequential)
        {
            DhmpSequentialBacklog backlog =
                GetSequentialBacklog();

            for (int offset = 0;
                 offset < complete.Length;
                 offset += recordSize)
            {
                backlog.Enqueue(
                    complete.Slice(
                        offset,
                        recordSize));
            }

            PublishSequentialCompatibilityBatch(
                backlog,
                completeBytes,
                recordSize,
                publishBatch);

            return;
        }

        // Fused Latest fast path: one packet->Ring-3 copy per record, then
        // metadata-only publication/grab. No Begin/Commit/Grab helper chain,
        // no per-record modulo and no redundant read-back of the published slot.
        GetReceiveSweepSlots()
            .ReceiveLatestPacketSingleWriter(
                complete,
                publishBatch);
    }

    private static void PublishSequentialCompatibilityBatch(
        DhmpSequentialBacklog backlog,
        int completeBytes,
        int recordSize,
        Action<ReadOnlySpan<byte>> publishBatch)
    {
        int recordCount =
            completeBytes /
            recordSize;

        byte[] publicationBuffer =
            ArrayPool<byte>.Shared.Rent(
                completeBytes);

        try
        {
            Span<byte> publication =
                publicationBuffer.AsSpan(
                    0,
                    completeBytes);

            for (int index = 0;
                 index < recordCount;
                 index++)
            {
                bool dequeued =
                    backlog.TryDequeue(
                        publication.Slice(
                            index * recordSize,
                            recordSize));

                if (!dequeued)
                {
                    throw new InvalidOperationException(
                        "Sequential grabber lost a record before synchronous publication.");
                }
            }

            publishBatch(publication);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(
                publicationBuffer,
                clearArray: false);
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
    /// Zero-copy Native Smoothing grabber. The callback receives direct spans
    /// over the three physical arrival-ring slots in N-2/N-1/N order.
    /// The spans are borrowed and valid only for the callback.
    /// </summary>
    public int ConsumeNativeSmoothingWindow(
        DhmpWindow3Consumer consumer)
    {
        if (!NativeSmoothingEnabled)
            return 0;

        return GetLatestSweepSlots()
            .ConsumeCompletedWindow3SingleWriter(
                consumer);
    }

    /// <summary>
    /// Snapshot compatibility helper. Copies the current three-record window
    /// into caller-owned contiguous storage. This is intentionally outside the
    /// canonical zero-copy sweeper/grabber path.
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
        EnsureRingSequentialMode();
        return GetReceiveSweepSlots().BeginSweep();
    }

    /// <summary>
    /// Commit one Sequential sweep and let the grabber move that exact record
    /// into FIFO storage. Backpressure waits only when a fixed lossless backlog
    /// is full. DropOldest and Unbounded never wait for consumer capacity.
    /// </summary>
    public void CommitSequentialSweep()
    {
        EnsureRingSequentialMode();

        GetSequentialBacklog()
            .Enqueue(
                _receiveSweepSlots
                    .CommitSweepAndGetSlotSingleWriter());
    }

    public void CancelSequentialSweep()
    {
        EnsureRingSequentialMode();
        GetReceiveSweepSlots().CancelSweep();
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
        EnsureSequentialFamilyMode();

        _processor.Process(
            packet,
            _receivePolicy.Mode ==
                DhmpProcessingMode.UnsafeSequential
                ? EnqueueUnsafeSequentialBatch
                : SweepSequentialBatch);
    }

    /// <summary>
    /// Pop the oldest Sequential record. Removing an item immediately frees
    /// fixed-backlog capacity and can release a backpressured sweeper.
    /// </summary>
    public bool TryDequeueSequential(
        Span<byte> destination)
    {
        EnsureSequentialFamilyMode();

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

    private void EnqueueUnsafeSequentialBatch(
        ReadOnlySpan<byte> batch)
    {
        int recordSize =
            _wireContract.RecordSize;

        DhmpSequentialBacklog backlog =
            GetSequentialBacklog();

        for (int offset = 0;
             offset < batch.Length;
             offset += recordSize)
        {
            backlog.Enqueue(
                batch.Slice(
                    offset,
                    recordSize));
        }
    }

    private DhmpSequentialBacklog GetSequentialBacklog()
    {
        EnsureSequentialFamilyMode();

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

    private bool IsSequentialFamilyMode() =>
        _receivePolicy.Mode is
            DhmpProcessingMode.Sequential or
            DhmpProcessingMode.UnsafeSequential;

    private void EnsureSequentialFamilyMode()
    {
        if (!IsSequentialFamilyMode())
        {
            throw new InvalidOperationException(
                "Sequential FIFO APIs require Sequential or UnsafeSequential receive mode.");
        }
    }

    private void EnsureRingSequentialMode()
    {
        if (_receivePolicy.Mode !=
            DhmpProcessingMode.Sequential)
        {
            throw new InvalidOperationException(
                "Ring-3 Sequential sweep APIs require DhmpProcessingMode.Sequential.");
        }
    }

    private DhmpLatestStateWindow GetLatestSweepSlots()
    {
        if (_receivePolicy.Mode != DhmpProcessingMode.Latest)
        {
            throw new InvalidOperationException(
                "Latest sweep/grab APIs require DhmpProcessingMode.Latest.");
        }

        return GetReceiveSweepSlots();
    }

    private DhmpLatestStateWindow GetReceiveSweepSlots() =>
        _receiveSweepSlots ??
        throw new InvalidOperationException(
            "UnsafeSequential intentionally has no Ring-3 receive storage.");
}
