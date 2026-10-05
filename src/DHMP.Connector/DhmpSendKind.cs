namespace DHMP.Connector;

/// <summary>
/// High-level Connector send intent. All modes still use the unchanged DHMP V1 fixed-record data plane.
/// </summary>
public enum DhmpSendKind
{
    /// <summary>Submit once and return after the local backend accepts the record.</summary>
    FireAndForget = 0,

    /// <summary>
    /// Submit once and wait for an application-owned confirmation ID to be observed.
    /// No retransmission is performed.
    /// </summary>
    Confirmed = 1
}
