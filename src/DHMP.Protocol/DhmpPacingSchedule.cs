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
    private long _singleMessageIntervalTicks;
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

        if (messagesPerSecond == Volatile.Read(ref _messagesPerSecond))
            return;

        // Precalculate the common one-record interval whenever the rate changes.
        // The minimum one-tick interval matches the existing ceiling calculation.
        long singleTicks = checked((long)Math.Ceiling(
            (double)Stopwatch.Frequency / messagesPerSecond));
        Volatile.Write(ref _singleMessageIntervalTicks, singleTicks);
        Volatile.Write(ref _messagesPerSecond, messagesPerSecond);
    }

    /// <summary>
    /// Fused single-record pacing fast path: one clock read, no per-record
    /// floating-point division when the rate has not changed.
    /// The caller serializes sends and awaits a positive delay before Commit.
    /// </summary>
    public TimeSpan TryCommitOne(long timestamp)
    {
        long next = _nextTimestamp;
        if (next != 0 && timestamp < next)
        {
            double seconds = (double)(next - timestamp) / Stopwatch.Frequency;
            return TimeSpan.FromSeconds(seconds);
        }

        _nextTimestamp = checked(timestamp + Volatile.Read(ref _singleMessageIntervalTicks));
        return TimeSpan.Zero;
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
