namespace DHMP.AspNetCore;

// Application request execution deduplication, independent of encrypted data-packet replay.
internal sealed class DhmpApiRequestWindow
{
    private const int Window = 1024;
    private readonly ulong[] _seen = new ulong[Window / 64];
    private ulong _highest;
    internal bool TryAccept(ulong sequence)
    {
        if (sequence == 0) return false;
        if (sequence > _highest)
        {
            ulong distance = sequence - _highest;
            if (distance >= Window) Array.Clear(_seen);
            else
            {
                int words = (int)distance / 64, bits = (int)distance % 64;
                for (int i = _seen.Length - 1; i >= 0; i--)
                {
                    ulong value = i >= words ? _seen[i - words] << bits : 0;
                    if (bits != 0 && i > words) value |= _seen[i - words - 1] >> (64 - bits);
                    _seen[i] = value;
                }
            }
            _highest = sequence;
            _seen[0] |= 1;
            return true;
        }
        ulong behind = _highest - sequence;
        if (behind >= Window) return false;
        int word = (int)behind / 64, bit = (int)behind % 64;
        ulong mask = 1UL << bit;
        if ((_seen[word] & mask) != 0) return false;
        _seen[word] |= mask;
        return true;
    }
}
