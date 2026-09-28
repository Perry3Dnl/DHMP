namespace Dhmp.Server;

/// <summary>
/// Controls how processed packages are exposed across the post-stream-processor handoff.
/// Neither mode is a delivery guarantee.
/// </summary>
public enum DHMPProcessingMode
{
    /// <summary>Older unconsumed state may be replaced by newer state.</summary>
    Latest,

    /// <summary>Complete received packages are offered to the model processor in receive order.</summary>
    Sequential
}
