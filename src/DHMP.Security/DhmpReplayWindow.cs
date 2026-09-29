namespace DHMP.Security;

/// <summary>
/// Single-owner 64-packet replay window for monotonically increasing security counters.
/// </summary>
internal sealed class DhmpReplayWindow
{
    private ulong _highest;
    private ulong _bitmap;
    private bool _initialized;

    public bool TryAccept(ulong counter)
    {
        if (counter == 0)
            return false;

        if (!_initialized)
        {
            _initialized = true;
            _highest = counter;
            _bitmap = 1;
            return true;
        }

        if (counter > _highest)
        {
            ulong delta = counter - _highest;

            _bitmap =
                delta >= 64
                    ? 1
                    : (_bitmap << (int)delta) | 1UL;

            _highest = counter;
            return true;
        }

        ulong behind = _highest - counter;

        if (behind >= 64)
            return false;

        ulong mask = 1UL << (int)behind;

        if ((_bitmap & mask) != 0)
            return false;

        _bitmap |= mask;
        return true;
    }
}
