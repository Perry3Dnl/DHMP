namespace DHMP.Protocol;

/// <summary>
/// Result of a successful control-plane compatibility exchange.
/// The effective send policy preserves local Pmax while clamping packet size
/// to the remote endpoint's advertised receive capability.
/// </summary>
public readonly record struct DhmpNegotiatedPeer
{
    public DhmpNegotiatedPeer(
        DhmpPeerProfile remoteProfile,
        DhmpSendPolicy effectiveSendPolicy)
    {
        remoteProfile.Validate();
        effectiveSendPolicy.Validate(remoteProfile.WireContract);

        RemoteProfile = remoteProfile;
        EffectiveSendPolicy = effectiveSendPolicy;
    }

    public DhmpPeerProfile RemoteProfile { get; }
    public DhmpSendPolicy EffectiveSendPolicy { get; }

    /// <summary>
    /// Apply the final local sender/backend ceiling after negotiation, for example
    /// after subtracting an explicit security envelope.
    /// </summary>
    public DhmpSendPolicy ConstrainToPayloadLimit(
        int maximumPayloadBytes)
    {
        if (maximumPayloadBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maximumPayloadBytes));

        int recordSize =
            RemoteProfile.WireContract.RecordSize;

        int rawMaximum =
            Math.Min(
                EffectiveSendPolicy.MaximumPayloadBytes,
                maximumPayloadBytes);

        int alignedMaximum =
            rawMaximum / recordSize * recordSize;

        if (alignedMaximum < recordSize)
            throw new ArgumentException(
                "Final sender payload limit cannot fit one complete DHMP record.",
                nameof(maximumPayloadBytes));

        return new DhmpSendPolicy(
            EffectiveSendPolicy.Pmax,
            alignedMaximum,
            EffectiveSendPolicy.RatePolicy);
    }
}
