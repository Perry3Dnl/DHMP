namespace DHMP.Protocol;

/// <summary>
/// Local outbound policy. These values are not part of DHMP V1 wire identity.
/// </summary>
public readonly record struct DhmpSendPolicy
{
    public DhmpSendPolicy(
        long pmax,
        int maximumPayloadBytes = 1408,
        DhmpRatePolicy ratePolicy = DhmpRatePolicy.RejectWindow)
    {
        if (pmax <= 0)
            throw new ArgumentOutOfRangeException(nameof(pmax));

        if (maximumPayloadBytes <= 0 ||
            maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        if (ratePolicy is not DhmpRatePolicy.RejectWindow and
            not DhmpRatePolicy.SmoothPacing and
            not DhmpRatePolicy.Unlimited)
            throw new ArgumentOutOfRangeException(nameof(ratePolicy));

        Pmax = pmax;
        MaximumPayloadBytes = maximumPayloadBytes;
        RatePolicy = ratePolicy;
    }

    public long Pmax { get; }
    public int MaximumPayloadBytes { get; }
    public DhmpRatePolicy RatePolicy { get; }

    public void Validate(DhmpWireContract wireContract)
    {
        wireContract.Validate();

        if (Pmax <= 0 ||
            MaximumPayloadBytes < wireContract.RecordSize ||
            MaximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentException(
                "A valid local DHMP send policy is required.");

        if (RatePolicy is not DhmpRatePolicy.RejectWindow and
            not DhmpRatePolicy.SmoothPacing and
            not DhmpRatePolicy.Unlimited)
            throw new ArgumentException(
                "A supported DHMP rate policy is required.");
    }
}
