namespace DHMP.Protocol;

public enum DhmpProcessingMode
{
    /// <summary>Publish all complete received messages in arrival order; no network delivery guarantee.</summary>
    Sequential,
    /// <summary>Publish the last complete record of a packet, in arrival order. Wire freshness ordering is not implemented.</summary>
    Latest
}
