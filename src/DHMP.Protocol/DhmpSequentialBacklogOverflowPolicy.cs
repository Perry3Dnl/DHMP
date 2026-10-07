namespace DHMP.Protocol;

/// <summary>
/// Local overflow behavior for the Sequential receive backlog. This does not
/// change DHMP V1 data-plane bytes.
/// </summary>
public enum DhmpSequentialBacklogOverflowPolicy
{
    /// <summary>
    /// Preserve every accepted record in FIFO order. Once the fixed backlog is
    /// full, the receive sweeper waits until the consumer frees capacity.
    /// </summary>
    Backpressure = 0,

    /// <summary>
    /// Keep the receive sweeper running. When the fixed backlog is full, drop
    /// the oldest unread record before appending the newest record.
    /// </summary>
    DropOldest = 1,

    /// <summary>
    /// Grow the FIFO backlog when required. This avoids protocol backpressure
    /// but can create memory/GC pressure when a consumer remains slower than
    /// the producer for a sustained period.
    /// </summary>
    Unbounded = 2
}
