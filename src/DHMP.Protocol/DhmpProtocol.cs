namespace DHMP.Protocol;

/// <summary>Pre-1.0 protocol identity for the current standalone DHMP work.</summary>
public static class DhmpProtocol
{
    /// <summary>Current pre-1.0 headerless data wire version.</summary>
    public const byte CurrentVersion = 1;

    /// <summary>Current pre-1.0 control-plane packet version.</summary>
    public const byte ControlVersion = 1;

    /// <summary>
    /// Experimental IPv6 protocol/Next Header used by DHMP V1 data packets.
    /// IANA reserves 253 for explicitly configured experimentation/testing; this is not a permanent
    /// or standardized DHMP assignment and must not be treated as a production default.
    /// </summary>
    public const byte ExperimentalIpv6DataNextHeader = 253;

    /// <summary>
    /// Experimental IPv6 protocol/Next Header used by DHMP control packets.
    /// IANA reserves 254 for explicitly configured experimentation/testing; this is not a permanent
    /// or standardized DHMP assignment and must not be treated as a production default.
    /// </summary>
    public const byte ExperimentalIpv6ControlNextHeader = 254;

    /// <summary>Fixed size of one DHMP V1 control packet.</summary>
    public const int ControlPacketSize = 32;
}
