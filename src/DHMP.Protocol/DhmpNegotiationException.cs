namespace DHMP.Protocol;

public sealed class DhmpNegotiationException : InvalidOperationException
{
    public DhmpNegotiationException(
        DhmpControlRejectReason reason,
        string? message = null)
        : base(message ?? $"DHMP negotiation rejected: {reason}.")
    {
        Reason = reason;
    }

    public DhmpControlRejectReason Reason { get; }
}
