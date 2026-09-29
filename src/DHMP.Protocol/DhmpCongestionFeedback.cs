namespace DHMP.Protocol;

/// <summary>
/// Receiver pressure evidence plus a bounded multiplicative pacing recommendation.
/// Authentication/replay protection belong to the selected control/security profile.
/// </summary>
public readonly record struct DhmpCongestionFeedback
{
    public const ushort MinimumScalePermille = 100;
    public const ushort MaximumScalePermille = 1000;

    public DhmpCongestionFeedback(
        DhmpCongestionPressure pressure,
        ushort rateScalePermille,
        int pendingBatches,
        int capacity,
        long lostPendingWork)
    {
        if (pressure is not DhmpCongestionPressure.None and
            not DhmpCongestionPressure.Soft and
            not DhmpCongestionPressure.Hard)
            throw new ArgumentOutOfRangeException(nameof(pressure));

        if (rateScalePermille < MinimumScalePermille ||
            rateScalePermille > MaximumScalePermille)
            throw new ArgumentOutOfRangeException(
                nameof(rateScalePermille));

        if (pendingBatches < 0)
            throw new ArgumentOutOfRangeException(
                nameof(pendingBatches));

        if (capacity <= 0 ||
            pendingBatches > capacity)
            throw new ArgumentOutOfRangeException(
                nameof(capacity));

        if (lostPendingWork < 0)
            throw new ArgumentOutOfRangeException(
                nameof(lostPendingWork));

        Pressure = pressure;
        RateScalePermille = rateScalePermille;
        PendingBatches = pendingBatches;
        Capacity = capacity;
        LostPendingWork = lostPendingWork;
    }

    public DhmpCongestionPressure Pressure { get; }
    public ushort RateScalePermille { get; }
    public int PendingBatches { get; }
    public int Capacity { get; }
    public long LostPendingWork { get; }

    public void Validate()
    {
        if (Pressure is not DhmpCongestionPressure.None and
            not DhmpCongestionPressure.Soft and
            not DhmpCongestionPressure.Hard)
            throw new ArgumentException(
                "A supported congestion pressure is required.");

        if (RateScalePermille < MinimumScalePermille ||
            RateScalePermille > MaximumScalePermille ||
            PendingBatches < 0 ||
            Capacity <= 0 ||
            PendingBatches > Capacity ||
            LostPendingWork < 0)
            throw new ArgumentException(
                "A valid DHMP congestion feedback value is required.");
    }
}
