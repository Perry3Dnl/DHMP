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
}
