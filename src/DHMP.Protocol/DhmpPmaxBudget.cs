using System.Diagnostics;
using System.Runtime.CompilerServices;

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
        if (pmax <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(pmax));

        _pmax = pmax;
        _windowStart =
            Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryConsume(
        int messages = 1)
    {
        if (messages == 1)
        {
            long now =
                Stopwatch.GetTimestamp();

            if (now - _windowStart >=
                Stopwatch.Frequency)
            {
                _windowStart = now;
                _count = 0;
            }

            if (_count >= _pmax)
                return false;

            _count++;
            return true;
        }

        if (messages <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(messages));

        if (messages > _pmax)
            return false;

        long batchNow =
            Stopwatch.GetTimestamp();

        if (batchNow - _windowStart >=
            Stopwatch.Frequency)
        {
            _windowStart =
                batchNow;
            _count = 0;
        }

        if (messages >
            _pmax - _count)
            return false;

        _count += messages;
        return true;
    }
}
