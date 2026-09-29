using System.Diagnostics;

namespace DHMP.Protocol;

public sealed class DhmpPmaxBudget
{
    private readonly int _pmax;
    private readonly long _windowTicks;
    private long _windowStart;
    private int _count;

    public DhmpPmaxBudget(int pmax)
    {
        if (pmax <= 0) throw new ArgumentOutOfRangeException(nameof(pmax));
        _pmax = pmax;
        _windowTicks = Stopwatch.Frequency;
        _windowStart = Stopwatch.GetTimestamp();
    }

    public bool TryConsume()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _windowStart >= _windowTicks)
        {
            _windowStart = now;
            _count = 0;
        }

        if (_count >= _pmax) return false;
        _count++;
        return true;
    }
}
