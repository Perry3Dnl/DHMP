using System.Diagnostics;

namespace DHMP.Protocol;

/// <summary>
/// Single-owner pacing schedule expressed in logical messages.
/// The pacing rate may be updated concurrently by authenticated feedback.
/// </summary>
/// <remarks>GetDelay/Commit calls must be serialized by the sender.</remarks>
public sealed class DhmpPacingSchedule
{
    private long _messagesPerSecond;
    private long _nextTimestamp;

    public DhmpPacingSchedule(long messagesPerSecond)
    {
        UpdateRate(messagesPerSecond);
    }

    public long MessagesPerSecond =>
        Volatile.Read(ref _messagesPerSecond);

    public void UpdateRate(long messagesPerSecond)
    {
        if (messagesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(messagesPerSecond));

        Volatile.Write(
            ref _messagesPerSecond,
            messagesPerSecond);
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

        long rate =
            Volatile.Read(
                ref _messagesPerSecond);

        long intervalTicks = checked(
            (long)Math.Ceiling(
                (double)messages *
                Stopwatch.Frequency /
                rate));

        _nextTimestamp = checked(
            start + intervalTicks);
    }

    public void Reset()
        => _nextTimestamp = 0;

    private static void ValidateMessages(int messages)
    {
        if (messages <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(messages));
    }
}
