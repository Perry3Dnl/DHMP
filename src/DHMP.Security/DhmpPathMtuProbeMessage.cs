namespace DHMP.Security;

public enum DhmpPathMtuProbeType : byte
{
    Request = 1,
    Response = 2
}

/// <summary>
/// Authenticated DPLPMTUD probe metadata carried only on the DHMP control binding.
/// Request packets are padded to <see cref="ProbedPayloadBytes"/>; responses are compact.
/// </summary>
public readonly record struct DhmpPathMtuProbeMessage
{
    public DhmpPathMtuProbeMessage(
        DhmpPathMtuProbeType type,
        ulong probeId,
        int probedPayloadBytes)
    {
        if (type is not DhmpPathMtuProbeType.Request and
            not DhmpPathMtuProbeType.Response)
            throw new ArgumentOutOfRangeException(nameof(type));

        if (probeId == 0)
            throw new ArgumentOutOfRangeException(nameof(probeId));

        if (probedPayloadBytes <
                DhmpPskChaCha20Poly1305Session.PathMtuProbeMinimumPacketSize ||
            probedPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(probedPayloadBytes));

        Type = type;
        ProbeId = probeId;
        ProbedPayloadBytes = probedPayloadBytes;
    }

    public DhmpPathMtuProbeType Type { get; }
    public ulong ProbeId { get; }

    /// <summary>
    /// Exact raw IPv6 upper-layer payload size tested by the request.
    /// The IPv6 base header is not included.
    /// </summary>
    public int ProbedPayloadBytes { get; }

    public void Validate()
    {
        if (Type is not DhmpPathMtuProbeType.Request and
            not DhmpPathMtuProbeType.Response)
            throw new ArgumentException("A supported DHMP path-MTU probe type is required.");

        if (ProbeId == 0 ||
            ProbedPayloadBytes <
                DhmpPskChaCha20Poly1305Session.PathMtuProbeMinimumPacketSize ||
            ProbedPayloadBytes > ushort.MaxValue)
            throw new ArgumentException("A valid DHMP path-MTU probe is required.");
    }
}
