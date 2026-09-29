using System.Numerics;

namespace DHMP.Security;

internal enum DhmpReplayDecision
{
    AcceptedNewHighest = 0,
    AcceptedReordered = 1,
    RejectedInvalid = 2,
    RejectedDuplicate = 3,
    RejectedTooOld = 4
}

internal readonly record struct DhmpReplayWindowSnapshot(
    ulong HighestCounter,
    int WindowSpan,
    int MissingWithinWindow);

/// <summary>
/// Single-owner 64-packet replay window for monotonically increasing security counters.
/// </summary>
internal sealed class DhmpReplayWindow
{
    private ulong _highest;
    private ulong _bitmap;
    private bool _initialized;

    public bool TryAccept(ulong counter)
        => TryAccept(counter, out _);

    public bool TryAccept(
        ulong counter,
        out DhmpReplayDecision decision)
    {
        if (counter == 0)
        {
            decision =
                DhmpReplayDecision.RejectedInvalid;
            return false;
        }

        if (!_initialized)
        {
            _initialized = true;
            _highest = counter;
            _bitmap = 1;

            decision =
                DhmpReplayDecision.AcceptedNewHighest;
            return true;
        }

        if (counter > _highest)
        {
            ulong delta =
                counter - _highest;

            _bitmap =
                delta >= 64
                    ? 1
                    : (_bitmap << (int)delta) | 1UL;

            _highest = counter;

            decision =
                DhmpReplayDecision.AcceptedNewHighest;
            return true;
        }

        ulong behind =
            _highest - counter;

        if (behind >= 64)
        {
            decision =
                DhmpReplayDecision.RejectedTooOld;
            return false;
        }

        ulong mask =
            1UL << (int)behind;

        if ((_bitmap & mask) != 0)
        {
            decision =
                DhmpReplayDecision.RejectedDuplicate;
            return false;
        }

        _bitmap |= mask;

        decision =
            DhmpReplayDecision.AcceptedReordered;
        return true;
    }

    public DhmpReplayWindowSnapshot GetSnapshot()
    {
        if (!_initialized)
            return default;

        int span =
            _highest >= 64
                ? 64
                : checked((int)_highest);

        ulong mask =
            span == 64
                ? ulong.MaxValue
                : (1UL << span) - 1UL;

        int seen =
            BitOperations.PopCount(
                _bitmap & mask);

        return new DhmpReplayWindowSnapshot(
            _highest,
            span,
            span - seen);
    }
}
