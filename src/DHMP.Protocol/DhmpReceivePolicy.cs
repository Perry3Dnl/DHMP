namespace DHMP.Protocol;

/// <summary>Local inbound publication and packet-limit policy. It does not change DHMP V1 wire bytes.</summary>
public readonly record struct DhmpReceivePolicy
{
    public DhmpReceivePolicy() : this(
        DhmpProcessingMode.Sequential,
        1408,
        nativeSmoothing: false,
        sequentialBacklogMillions: 1,
        sequentialBacklogOverflowPolicy:
            DhmpSequentialBacklogOverflowPolicy.Backpressure) { }

    public DhmpReceivePolicy(
        DhmpProcessingMode mode,
        int maximumPayloadBytes = 1408,
        bool nativeSmoothing = false,
        int sequentialBacklogMillions = 1,
        DhmpSequentialBacklogOverflowPolicy sequentialBacklogOverflowPolicy =
            DhmpSequentialBacklogOverflowPolicy.Backpressure)
    {
        if (mode is not DhmpProcessingMode.Sequential and not DhmpProcessingMode.Latest)
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (maximumPayloadBytes <= 0 || maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        if (nativeSmoothing && mode != DhmpProcessingMode.Latest)
            throw new ArgumentException("Native smoothing is only valid with Latest receive mode.", nameof(nativeSmoothing));
        if (sequentialBacklogMillions <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequentialBacklogMillions));
        if (!Enum.IsDefined(sequentialBacklogOverflowPolicy))
            throw new ArgumentOutOfRangeException(nameof(sequentialBacklogOverflowPolicy));

        Mode = mode;
        MaximumPayloadBytes = maximumPayloadBytes;
        NativeSmoothing = nativeSmoothing;
        SequentialBacklogMillions = sequentialBacklogMillions;
        SequentialBacklogOverflowPolicy = sequentialBacklogOverflowPolicy;
    }

    public DhmpProcessingMode Mode { get; }
    public int MaximumPayloadBytes { get; }

    /// <summary>
    /// Select the Native Smoothing grabber for Latest mode. Latest and Native
    /// Smoothing share the same fixed three sweeper slots; this flag does not
    /// add packet-path work. Latest grabs the currently published slot, while
    /// Native Smoothing consumes a complete N-2/N-1/N window.
    /// </summary>
    public bool NativeSmoothing { get; }

    /// <summary>
    /// Sequential FIFO capacity in units of one million records. Example:
    /// 10 means a 10,000,000-record backlog. Fixed modes allocate that storage
    /// lazily when the Sequential sweeper/grabber API is first used.
    /// </summary>
    public int SequentialBacklogMillions { get; }

    public long SequentialBacklogCapacityRecords =>
        checked((long)SequentialBacklogMillions * 1_000_000L);

    /// <summary>
    /// Backpressure preserves all records, DropOldest preserves producer speed
    /// by evicting the oldest unread record, and Unbounded grows as required.
    /// </summary>
    public DhmpSequentialBacklogOverflowPolicy SequentialBacklogOverflowPolicy { get; }

    public void Validate(DhmpWireContract wireContract)
    {
        wireContract.Validate();
        if (Mode is not DhmpProcessingMode.Sequential and not DhmpProcessingMode.Latest)
            throw new ArgumentException("A supported DHMP receive mode is required.");
        if (MaximumPayloadBytes < wireContract.RecordSize || MaximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentException("A valid local DHMP receive packet limit is required.");
        if (NativeSmoothing && Mode != DhmpProcessingMode.Latest)
            throw new ArgumentException("Native smoothing requires Latest receive mode.");
        if (SequentialBacklogMillions <= 0 ||
            !Enum.IsDefined(SequentialBacklogOverflowPolicy))
            throw new ArgumentException("A valid Sequential backlog policy is required.");

    }
}
