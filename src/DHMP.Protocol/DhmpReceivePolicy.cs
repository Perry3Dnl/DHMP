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
            1408)
    {
    }

    public DhmpReceivePolicy(
        DhmpProcessingMode mode,
        int maximumPayloadBytes = 1408)
    {
        if (mode is not DhmpProcessingMode.Sequential and not DhmpProcessingMode.Latest)
            throw new ArgumentOutOfRangeException(nameof(mode));

        if (maximumPayloadBytes <= 0 ||
            maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        Mode = mode;
        MaximumPayloadBytes = maximumPayloadBytes;
    }

    public DhmpProcessingMode Mode { get; }
    public int MaximumPayloadBytes { get; }

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
    }
}
