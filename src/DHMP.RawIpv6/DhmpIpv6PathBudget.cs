using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// Converts an IPv6 path MTU into a safe raw-protocol payload budget.
/// Jumbograms are intentionally outside the current profile.
/// </summary>
public readonly record struct DhmpIpv6PathBudget
{
    public const int MinimumIpv6Mtu = 1280;
    public const int Ipv6BaseHeaderBytes = 40;
    public const int MaximumNonJumboIpv6PacketBytes =
        Ipv6BaseHeaderBytes + ushort.MaxValue;

    public DhmpIpv6PathBudget(
        int pathMtu,
        int additionalIpv6HeaderBytes = 0)
    {
        if (pathMtu < MinimumIpv6Mtu ||
            pathMtu > MaximumNonJumboIpv6PacketBytes)
            throw new ArgumentOutOfRangeException(
                nameof(pathMtu));

        if (additionalIpv6HeaderBytes < 0)
            throw new ArgumentOutOfRangeException(
                nameof(additionalIpv6HeaderBytes));

        int protocolPayloadBytes =
            pathMtu -
            Ipv6BaseHeaderBytes -
            additionalIpv6HeaderBytes;

        if (protocolPayloadBytes <= 0 ||
            protocolPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(additionalIpv6HeaderBytes));

        PathMtu = pathMtu;
        AdditionalIpv6HeaderBytes =
            additionalIpv6HeaderBytes;
        MaximumProtocolPayloadBytes =
            protocolPayloadBytes;
    }

    public int PathMtu { get; }
    public int AdditionalIpv6HeaderBytes { get; }
    public int MaximumProtocolPayloadBytes { get; }

    /// <summary>
    /// Return the largest whole-record plaintext payload that fits after
    /// subtracting an explicit packet envelope such as DHMP.Security.
    /// </summary>
    public int GetAlignedPlaintextPayloadBytes(
        DhmpWireContract wireContract,
        int packetEnvelopeBytes = 0)
    {
        wireContract.Validate();

        if (packetEnvelopeBytes < 0)
            throw new ArgumentOutOfRangeException(
                nameof(packetEnvelopeBytes));

        int available =
            MaximumProtocolPayloadBytes -
            packetEnvelopeBytes;

        int aligned =
            available /
            wireContract.RecordSize *
            wireContract.RecordSize;

        if (aligned < wireContract.RecordSize)
            throw new ArgumentException(
                "Path MTU cannot fit one complete DHMP record after packet overhead.");

        return aligned;
    }
}
