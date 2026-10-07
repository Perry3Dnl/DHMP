namespace DHMP.Connector;

/// <summary>The concrete network path selected for an established DHMP connection.</summary>
public enum DhmpTransportKind
{
    RawIpv6 = 0,
    UdpCompatibility = 1
}
