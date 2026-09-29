namespace DHMP.Server;

/// <summary>Bounded preallocated receive-region pool. No growth occurs on the hot path.</summary>
public sealed class DHMPReceiveRegionPool
{
    private readonly DHMPReceiveRegion[] _regions;
    private int _cursor = -1;

    public DHMPReceiveRegionPool(
        int regionCount,
        int regionBytes)
    {
        if (regionCount < 2)
            throw new ArgumentOutOfRangeException(
                nameof(regionCount));

        if (regionBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(regionBytes));

        _regions =
            Enumerable.Range(0, regionCount)
                .Select(_ =>
                    new DHMPReceiveRegion(
                        regionBytes))
                .ToArray();
    }

    public bool TryAcquire(
        out DHMPReceiveRegion? region)
    {
        int start =
            (int)(
                (uint)Interlocked.Increment(
                    ref _cursor) %
                (uint)_regions.Length);

        for (int i = 0; i < _regions.Length; i++)
        {
            int index =
                (start + i) %
                _regions.Length;

            if (!_regions[index]
                    .TryAcquireForReceive())
                continue;

            region = _regions[index];
            return true;
        }

        region = null;
        return false;
    }
}
