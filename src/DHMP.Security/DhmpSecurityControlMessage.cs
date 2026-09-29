namespace DHMP.Security;

public readonly record struct DhmpSecurityControlMessage
{
    public DhmpSecurityControlMessage(
        DhmpSecurityControlType type,
        DhmpSecuritySuite suite,
        Guid sessionId,
        uint keyId,
        uint correlationId,
        DhmpSecurityRejectReason rejectReason =
            DhmpSecurityRejectReason.None)
    {
        if (type is not DhmpSecurityControlType.Offer and
            not DhmpSecurityControlType.Accept and
            not DhmpSecurityControlType.Reject)
            throw new ArgumentOutOfRangeException(nameof(type));

        if (suite !=
            DhmpSecuritySuite.PskChaCha20Poly1305HkdfSha256)
            throw new ArgumentOutOfRangeException(nameof(suite));

        if (sessionId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(sessionId));

        if (keyId == 0)
            throw new ArgumentOutOfRangeException(nameof(keyId));

        if (correlationId == 0)
            throw new ArgumentOutOfRangeException(nameof(correlationId));

        if (!Enum.IsDefined(rejectReason))
            throw new ArgumentOutOfRangeException(nameof(rejectReason));

        if (type == DhmpSecurityControlType.Reject &&
            rejectReason == DhmpSecurityRejectReason.None)
            throw new ArgumentException(
                "A security REJECT requires a reason.",
                nameof(rejectReason));

        if (type != DhmpSecurityControlType.Reject &&
            rejectReason != DhmpSecurityRejectReason.None)
            throw new ArgumentException(
                "Only a security REJECT may carry a reason.",
                nameof(rejectReason));

        Type = type;
        Suite = suite;
        SessionId = sessionId;
        KeyId = keyId;
        CorrelationId = correlationId;
        RejectReason = rejectReason;
    }

    public DhmpSecurityControlType Type { get; }
    public DhmpSecuritySuite Suite { get; }
    public Guid SessionId { get; }
    public uint KeyId { get; }
    public uint CorrelationId { get; }
    public DhmpSecurityRejectReason RejectReason { get; }
}
