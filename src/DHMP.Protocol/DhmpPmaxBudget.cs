using System.Diagnostics;

namespace DHMP.Protocol;

/// <summary>Single-owner, fixed one-second window budget. Reserve once per packet batch.</summary>
/// <remarks>Not a congestion controller or pacing guarantee. Calls must be serialized.</remarks>
public sealed class DhmpPmaxBudget
{
    private readonly long _pmax;
    private long _windowStart;
    private long _count;

    public DhmpPmaxBudget(long pmax)
    {
        if (pmax <= 0) throw new ArgumentOutOfRangeException(nameof(pmax));
        _pmax = pmax;
        _windowStart = Stopwatch.GetTimestamp();
    }

    public bool TryConsume(int messages = 1)
    {
        if (messages <= 0) throw new ArgumentOutOfRangeException(nameof(messages));
        if (messages > _pmax) return false;
        var now = Stopwatch.GetTimestamp();
        if (now - _windowStart >= Stopwatch.Frequency)
        {
            _windowStart = now;
            _count = 0;
        }
        if (messages > _pmax - _count) return false;
        _count += messages;
        return true;
    }
}
