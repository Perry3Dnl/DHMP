namespace DHMP.Protocol;

/// <summary>
/// Conservative loss-only path advisor.
/// RTT is intentionally observability-only until real-path measurements justify an inflation model.
/// </summary>
public static class DhmpPathRateAdvisor
{
    public const int HardLossPermille = 100;
    public const int SoftLossPermille = 20;

    public static DhmpCongestionFeedback Evaluate(
        DhmpPathTelemetry telemetry)
    {
        DhmpCongestionPressure pressure;
        ushort scale;

        if (telemetry.LossPermille >=
            HardLossPermille)
        {
            pressure =
                DhmpCongestionPressure.Hard;
            scale = 500;
        }
        else if (telemetry.LossPermille >=
                 SoftLossPermille)
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
            pendingBatches: 0,
            capacity: 1,
            lostPendingWork:
                telemetry.MissingWithinWindow);
    }
}
