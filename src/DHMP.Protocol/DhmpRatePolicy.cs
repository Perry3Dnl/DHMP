namespace DHMP.Protocol;

/// <summary>
/// Local sender behavior when enforcing Pmax.
/// This is endpoint policy and never appears on the DHMP wire.
/// </summary>
public enum DhmpRatePolicy
{
    /// <summary>
    /// Preserve the original fixed one-second budget behavior:
    /// reject sends that exceed the current Pmax window.
    /// </summary>
    RejectWindow = 0,

    /// <summary>
    /// Smooth sends over time at the configured Pmax rate instead of
    /// intentionally allowing a full one-second burst.
    /// </summary>
    SmoothPacing = 1,

    /// <summary>
    /// Apply no local message-rate budget or pacing. Packets are submitted to the
    /// configured sender as quickly as the caller/backend can process them.
    /// </summary>
    Unlimited = 2
}
