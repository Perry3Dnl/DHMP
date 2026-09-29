namespace DHMP.Server;

/// <summary>
/// Explicit overload behavior for asynchronous application handoff.
/// </summary>
public enum DhmpReceiveDispatchMode
{
    /// <summary>
    /// Preserve accepted batch order until the bounded queue is full.
    /// New work is then rejected/dropped; existing queued work is never overwritten.
    /// </summary>
    SequentialReject = 0,

    /// <summary>
    /// Keep at most one pending batch. A newer batch replaces older pending work.
    /// Work already executing is never cancelled.
    /// </summary>
    LatestReplace = 1
}
