namespace Dhmp.Server;

/// <summary>Bounded preallocated receive-region pool. No growth occurs on the hot path.</summary>
public sealed class DHMPReceiveRegionPool
{
    private readonly DHMPReceiveRegion[] _regions;
    private int _cursor;
    public DHMPReceiveRegionPool(int regionCount, int regionBytes)
    {
        if(regionCount < 2) throw new ArgumentOutOfRangeException(nameof(regionCount));
        if(regionBytes <= 0) throw new ArgumentOutOfRangeException(nameof(regionBytes));
        _regions = Enumerable.Range(0, regionCount).Select(_ => new DHMPReceiveRegion(regionBytes)).ToArray();
    }
    public bool TryAcquire(out DHMPReceiveRegion? region)
    {
        for(int i=0;i<_regions.Length;i++)
        {
            int index=(_cursor+i)%_regions.Length;
            if(_regions[index].TryAcquireForReceive()){_cursor=(index+1)%_regions.Length;region=_regions[index];return true;}
        }
        region=null;return false;
    }
}
