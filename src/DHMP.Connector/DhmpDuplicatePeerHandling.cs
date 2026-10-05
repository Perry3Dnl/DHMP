namespace DHMP.Connector;

/// <summary>
/// Controls how one Connector handles more than one logical connection
/// arriving from the same source IPv6 address.
/// </summary>
public enum DhmpDuplicatePeerHandling
{
    /// <summary>Reject a second connection from an already registered source IPv6 address.</summary>
    Reject = 0,

    /// <summary>
    /// Resolve duplicate-source sessions with an application-owned 64-bit ConnectionId field.
    /// The field location is included in Connector schema negotiation and the value is derived
    /// from the authenticated session. DHMP V1 framing remains unchanged.
    /// </summary>
    ResolveWithConnectionId = 1
}
