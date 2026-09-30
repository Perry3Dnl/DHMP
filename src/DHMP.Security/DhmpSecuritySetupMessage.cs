namespace DHMP.Security;

public enum DhmpSecuritySetupType : byte
{
    Offer = 1,
    Challenge = 2,
    Confirm = 3,
    Accept = 4
}

/// <summary>Authenticated PSK setup V2 transcript. Neither nonce is a data packet header.</summary>
public readonly record struct DhmpSecuritySetupMessage
{
    public DhmpSecuritySetupMessage(DhmpSecuritySetupType type, Guid initiatorNonce,
        Guid responderNonce, uint keyId, uint correlationId)
    {
        Type = type;
        InitiatorNonce = initiatorNonce;
        ResponderNonce = responderNonce;
        KeyId = keyId;
        CorrelationId = correlationId;
        Validate();
    }

    public DhmpSecuritySetupType Type { get; }
    public Guid InitiatorNonce { get; }
    public Guid ResponderNonce { get; }
    public uint KeyId { get; }
    public uint CorrelationId { get; }

    public void Validate()
    {
        if (Type is not DhmpSecuritySetupType.Offer and not DhmpSecuritySetupType.Challenge and
            not DhmpSecuritySetupType.Confirm and not DhmpSecuritySetupType.Accept)
            throw new ArgumentOutOfRangeException(nameof(Type));
        if (InitiatorNonce == Guid.Empty || KeyId == 0 || CorrelationId == 0)
            throw new ArgumentException("PSK setup requires a non-empty initiator nonce and non-zero identifiers.");
        if ((Type == DhmpSecuritySetupType.Offer) != (ResponderNonce == Guid.Empty))
            throw new ArgumentException("Only OFFER has an empty responder nonce.");
    }

    public DhmpSecuritySetupMessage WithType(DhmpSecuritySetupType type)
        => new(type, InitiatorNonce, ResponderNonce, KeyId, CorrelationId);

    public bool MatchesTranscript(DhmpSecuritySetupMessage other)
        => InitiatorNonce == other.InitiatorNonce && ResponderNonce == other.ResponderNonce &&
            KeyId == other.KeyId && CorrelationId == other.CorrelationId;
}
