using System;
using System.Diagnostics;

namespace Dhmp.Protocol;

/// <summary>
/// Hard send-side DHMP invariants. This guard never queues, fragments, retries,
/// or waits for acknowledgement.
/// </summary>
public sealed class DhmpSendPolicy
{
    public const long MeasuredLatestPmax = 130_620_000;
    public const long MeasuredEveryPmax = 129_800_000;

    private readonly object _gate = new();
    private readonly double _tokensPerTick;
    private readonly double _capacity;
    private double _tokens;
    private long _lastTimestamp;

    public int MaxPayloadBytes { get; }
    public long PmaxMessagesPerSecond { get; }
    public double SafetyFactor { get; }
    public long AllowedMessagesPerSecond { get; }

    public DhmpSendPolicy(
        int maxPayloadBytes,
        long pmaxMessagesPerSecond,
        double safetyFactor = 0.90,
        TimeSpan? maximumBurst = null)
    {
        if (maxPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        if (pmaxMessagesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(pmaxMessagesPerSecond));
        if (safetyFactor is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(safetyFactor));

        MaxPayloadBytes = maxPayloadBytes;
        PmaxMessagesPerSecond = pmaxMessagesPerSecond;
        SafetyFactor = safetyFactor;
        AllowedMessagesPerSecond = Math.Max(1, (long)Math.Floor(pmaxMessagesPerSecond * safetyFactor));

        var burst = maximumBurst ?? TimeSpan.FromMilliseconds(1);
        if (burst <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumBurst));

        _tokensPerTick = (double)AllowedMessagesPerSecond / Stopwatch.Frequency;
        _capacity = Math.Max(1.0, AllowedMessagesPerSecond * burst.TotalSeconds);
        _tokens = _capacity;
        _lastTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Rejects an application message that cannot fit in one DHMP logical message.
    /// DHMP MUST NOT fragment or reassemble it.
    /// </summary>
    public void ValidatePayload(int payloadLength)
    {
        if (payloadLength < 0)
            throw new ArgumentOutOfRangeException(nameof(payloadLength));

        if (payloadLength > MaxPayloadBytes)
            throw new DhmpPayloadTooLargeException(payloadLength, MaxPayloadBytes);
    }

    /// <summary>
    /// Atomically reserves send budget for a batch. Returns false immediately
    /// when the Pmax-derived budget is exhausted. It does not queue or block.
    /// Call once per outgoing batch, then send only that many validated messages.
    /// </summary>
    public bool TryReserve(int messageCount)
    {
        if (messageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(messageCount));
        if (messageCount > _capacity)
            return false;

        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = now - _lastTimestamp;
            if (elapsed > 0)
            {
                _tokens = Math.Min(_capacity, _tokens + elapsed * _tokensPerTick);
                _lastTimestamp = now;
            }

            if (_tokens < messageCount)
                return false;

            _tokens -= messageCount;
            return true;
        }
    }
}

public sealed class DhmpPayloadTooLargeException : ArgumentException
{
    public int PayloadLength { get; }
    public int MaxPayloadBytes { get; }

    public DhmpPayloadTooLargeException(int payloadLength, int maxPayloadBytes)
        : base($"DHMP payload is {payloadLength} bytes; the negotiated maximum is {maxPayloadBytes} bytes. DHMP does not fragment messages.")
    {
        PayloadLength = payloadLength;
        MaxPayloadBytes = maxPayloadBytes;
    }
}
