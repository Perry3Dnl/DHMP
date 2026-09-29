using DHMP.Protocol;

namespace DHMP.Server;

/// <summary>
/// Converts changes in bounded receive-dispatch pressure into a conservative
/// multiplicative pacing recommendation.
/// </summary>
public static class DhmpCongestionAdvisor
{
    public static DhmpCongestionFeedback Evaluate(
        DhmpReceiveDispatchSnapshot previous,
        DhmpReceiveDispatchSnapshot current)
    {
        if (previous.Mode != current.Mode ||
            previous.Capacity != current.Capacity)
            throw new ArgumentException(
                "Congestion snapshots must describe the same dispatcher configuration.");

        if (current.AcceptedBatches < previous.AcceptedBatches ||
            current.ConsumedBatches < previous.ConsumedBatches ||
            current.SaturationDrops < previous.SaturationDrops ||
            current.ReplacedBatches < previous.ReplacedBatches)
            throw new ArgumentException(
                "Congestion snapshots must be monotonic.");

        long saturationDelta =
            current.SaturationDrops -
            previous.SaturationDrops;

        long replacementDelta =
            current.ReplacedBatches -
            previous.ReplacedBatches;

        DhmpCongestionPressure pressure;
        ushort scale;

        if (saturationDelta > 0 ||
            current.IsSaturated)
        {
            pressure =
                DhmpCongestionPressure.Hard;
            scale = 500;
        }
        else if (replacementDelta > 0 ||
                 IsHighPendingPressure(current))
        {
            pressure =
                DhmpCongestionPressure.Soft;
            scale = 750;
        }
        else
        {
            pressure =
                DhmpCongestionPressure.None;
            scale =
                DhmpCongestionFeedback
                    .MaximumScalePermille;
        }

        return new DhmpCongestionFeedback(
            pressure,
            scale,
            current.PendingBatches,
            current.Capacity,
            current.LostPendingWork);
    }

    private static bool IsHighPendingPressure(
        DhmpReceiveDispatchSnapshot snapshot)
        => snapshot.Capacity > 1 &&
           (long)snapshot.PendingBatches * 4 >=
           (long)snapshot.Capacity * 3;
}
