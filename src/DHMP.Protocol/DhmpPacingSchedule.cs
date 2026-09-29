using System.Diagnostics;

namespace DHMP.Protocol;

/// <summary>
/// Single-owner pacing schedule expressed in logical messages.
/// It is a pacing primitive, not congestion control or receiver feedback.
/// </summary>
/// <remarks>Calls must be serialized.</remarks>
public sealed class DhmpPacingSchedule
{
    private readonly int _messagesPerSecond;
    private long _nextTimestamp;

    public DhmpPacingSchedule(int messagesPerSecond)
    {
        if (messagesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(messagesPerSecond));

        _messagesPerSecond = messagesPerSecond;
    }

    public TimeSpan GetDelay(
        int messages,
        long timestamp)
    {
        ValidateMessages(messages);

        if (_nextTimestamp == 0 ||
            timestamp >= _nextTimestamp)
            return TimeSpan.Zero;

        double seconds =
            (double)(_nextTimestamp - timestamp) /
            Stopwatch.Frequency;

        return TimeSpan.FromSeconds(seconds);
    }

    public void Commit(
        int messages,
        long timestamp)
    {
        ValidateMessages(messages);

        long start =
            _nextTimestamp > timestamp
                ? _nextTimestamp
                : timestamp;

        long intervalTicks = checked(
            (long)Math.Ceiling(
                (double)messages *
                Stopwatch.Frequency /
                _messagesPerSecond));

        _nextTimestamp = checked(start + intervalTicks);
    }

    public void Reset()
        => _nextTimestamp = 0;

    private static void ValidateMessages(int messages)
    {
        if (messages <= 0)
            throw new ArgumentOutOfRangeException(nameof(messages));
    }
}
