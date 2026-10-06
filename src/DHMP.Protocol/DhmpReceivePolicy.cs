namespace DHMP.Protocol;

/// <summary>Local inbound publication and packet-limit policy. It does not change DHMP V1 wire bytes.</summary>
public readonly record struct DhmpReceivePolicy
{
    public DhmpReceivePolicy() : this(DhmpProcessingMode.Sequential, 1408, nativeSmoothing: false) { }

    public DhmpReceivePolicy(
        DhmpProcessingMode mode,
        int maximumPayloadBytes = 1408,
        bool nativeSmoothing = false)
    {
        if (mode is not DhmpProcessingMode.Sequential and not DhmpProcessingMode.Latest)
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (maximumPayloadBytes <= 0 || maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        if (nativeSmoothing && mode != DhmpProcessingMode.Latest)
            throw new ArgumentException("Native smoothing is only valid with Latest receive mode.", nameof(nativeSmoothing));

        Mode = mode;
        MaximumPayloadBytes = maximumPayloadBytes;
        NativeSmoothing = nativeSmoothing;
    }

    public DhmpProcessingMode Mode { get; }
    public int MaximumPayloadBytes { get; }

    /// <summary>
    /// Retain the newest three complete received records in a bounded receive-side Ring-3
    /// while normal Latest publication still exposes only the newest record.
    /// </summary>
    public bool NativeSmoothing { get; }

    public void Validate(DhmpWireContract wireContract)
    {
        wireContract.Validate();
        if (Mode is not DhmpProcessingMode.Sequential and not DhmpProcessingMode.Latest)
            throw new ArgumentException("A supported DHMP receive mode is required.");
        if (MaximumPayloadBytes < wireContract.RecordSize || MaximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentException("A valid local DHMP receive packet limit is required.");
        if (NativeSmoothing && Mode != DhmpProcessingMode.Latest)
            throw new ArgumentException("Native smoothing requires Latest receive mode.");
    }
}
