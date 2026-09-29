namespace DHMP.Protocol;

/// <summary>
/// Thread-safe local sender rate controller driven by authenticated receiver feedback.
/// Remote feedback can reduce the current rate but can never raise it above the local maximum.
/// Recovery is gradual.
/// </summary>
public sealed class DhmpAdaptiveRateController
{
    private readonly int _minimumMessagesPerSecond;
    private readonly int _maximumMessagesPerSecond;
    private readonly int _recoveryPercent;

    private int _currentMessagesPerSecond;
    private long _feedbackCount;
    private long _decreaseCount;
    private long _recoveryCount;

    public DhmpAdaptiveRateController(
        int maximumMessagesPerSecond,
        int minimumMessagesPerSecond = 1,
        int recoveryPercent = 10)
    {
        if (maximumMessagesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maximumMessagesPerSecond));

        if (minimumMessagesPerSecond <= 0 ||
            minimumMessagesPerSecond >
                maximumMessagesPerSecond)
            throw new ArgumentOutOfRangeException(
                nameof(minimumMessagesPerSecond));

        if (recoveryPercent <= 0 ||
            recoveryPercent > 100)
            throw new ArgumentOutOfRangeException(
                nameof(recoveryPercent));

        _maximumMessagesPerSecond =
            maximumMessagesPerSecond;
        _minimumMessagesPerSecond =
            minimumMessagesPerSecond;
        _recoveryPercent =
            recoveryPercent;
        _currentMessagesPerSecond =
            maximumMessagesPerSecond;
    }

    public int MinimumMessagesPerSecond =>
        _minimumMessagesPerSecond;

    public int MaximumMessagesPerSecond =>
        _maximumMessagesPerSecond;

    public int CurrentMessagesPerSecond =>
        Volatile.Read(
            ref _currentMessagesPerSecond);

    public long FeedbackCount =>
        Interlocked.Read(ref _feedbackCount);

    public long DecreaseCount =>
        Interlocked.Read(ref _decreaseCount);

    public long RecoveryCount =>
        Interlocked.Read(ref _recoveryCount);

    public int ApplyFeedback(
        DhmpCongestionFeedback feedback)
    {
        feedback.Validate();

        Interlocked.Increment(
            ref _feedbackCount);

        while (true)
        {
            int current =
                Volatile.Read(
                    ref _currentMessagesPerSecond);

            int next =
                CalculateNextRate(
                    current,
                    feedback);

            if (next == current)
                return current;

            if (Interlocked.CompareExchange(
                    ref _currentMessagesPerSecond,
                    next,
                    current) != current)
                continue;

            if (next < current)
                Interlocked.Increment(
                    ref _decreaseCount);
            else
                Interlocked.Increment(
                    ref _recoveryCount);

            return next;
        }
    }

    private int CalculateNextRate(
        int current,
        DhmpCongestionFeedback feedback)
    {
        if (feedback.Pressure !=
            DhmpCongestionPressure.None ||
            feedback.RateScalePermille <
                DhmpCongestionFeedback.MaximumScalePermille)
        {
            long scaled =
                (long)current *
                feedback.RateScalePermille /
                DhmpCongestionFeedback.MaximumScalePermille;

            return Math.Clamp(
                checked((int)Math.Max(
                    scaled,
                    _minimumMessagesPerSecond)),
                _minimumMessagesPerSecond,
                _maximumMessagesPerSecond);
        }

        if (current >=
            _maximumMessagesPerSecond)
            return current;

        int increase =
            Math.Max(
                1,
                checked((int)Math.Min(
                    int.MaxValue,
                    (long)current *
                    _recoveryPercent /
                    100)));

        long recovered =
            (long)current + increase;

        return (int)Math.Min(
            _maximumMessagesPerSecond,
            recovered);
    }
}
