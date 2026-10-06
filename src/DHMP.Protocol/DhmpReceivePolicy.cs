namespace DHMP.Protocol;

/// <summary>
/// Local inbound publication and packet-limit policy.
/// It does not change DHMP V1 wire bytes.
/// </summary>
public readonly record struct DhmpReceivePolicy
{
    public DhmpReceivePolicy()
        : this(
            DhmpProcessingMode.Sequential,
            1408,
            1)
    {
    }

    public DhmpReceivePolicy(
        DhmpProcessingMode mode,
        int maximumPayloadBytes = 1408,
        int latestHistoryRecords = 1)
    {
        if (mode is not DhmpProcessingMode.Sequential and not DhmpProcessingMode.Latest)
            throw new ArgumentOutOfRangeException(nameof(mode));

        if (maximumPayloadBytes <= 0 ||
            maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        if (latestHistoryRecords <= 0 ||
            latestHistoryRecords > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(latestHistoryRecords));

        Mode = mode;
        MaximumPayloadBytes = maximumPayloadBytes;
        LatestHistoryRecords = latestHistoryRecords;
    }

    public DhmpProcessingMode Mode { get; }
    public int MaximumPayloadBytes { get; }

    /// <summary>
    /// Number of already-received tail records exposed when <see cref="Mode"/> is
    /// <see cref="DhmpProcessingMode.Latest"/>. A value of 1 preserves normal
    /// Latest behavior. Values above 1 expose older records from the same packet
    /// before the newest record, without waiting for future packets and without
    /// adding DHMP wire metadata.
    /// </summary>
    public int LatestHistoryRecords { get; }

    public void Validate(DhmpWireContract wireContract)
    {
        wireContract.Validate();

        if (Mode is not DhmpProcessingMode.Sequential and not DhmpProcessingMode.Latest)
            throw new ArgumentException(
                "A supported DHMP receive mode is required.");

        if (MaximumPayloadBytes < wireContract.RecordSize ||
            MaximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentException(
                "A valid local DHMP receive packet limit is required.");

        if (LatestHistoryRecords <= 0 ||
            LatestHistoryRecords > ushort.MaxValue)
            throw new ArgumentException(
                "Latest history must request at least one record.");
    }
}
