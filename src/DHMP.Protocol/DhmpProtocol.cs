namespace DHMP.Protocol;

/// <summary>Stable protocol identity for the current standalone DHMP work.</summary>
public static class DhmpProtocol
{
    /// <summary>Current pre-1.0 wire/session contract version.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>
    /// IPv6 experimental-use Next Header value used by the current raw-IP research backend.
    /// This is not an IANA-assigned permanent DHMP protocol number.
    /// </summary>
    public const byte ExperimentalIpv6NextHeader = 253;
}
