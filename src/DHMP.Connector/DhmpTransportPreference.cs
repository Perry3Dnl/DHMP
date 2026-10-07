namespace DHMP.Connector;

/// <summary>Controls how the DHMP connection resolver selects a network path.</summary>
public enum DhmpTransportPreference
{
    /// <summary>Prefer native Raw IPv6 when locally available and reachable, then fall back to UDP encapsulation.</summary>
    Auto = 0,

    /// <summary>Require the native Raw IPv6 backend. No compatibility fallback is attempted.</summary>
    RawIpv6Only = 1,

    /// <summary>Use the UDP compatibility backend directly.</summary>
    UdpCompatibilityOnly = 2
}
