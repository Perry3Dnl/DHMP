using DHMP.Protocol;

namespace DHMP.Client;

/// <summary>
/// Validates and budgets headerless DHMP data packets before handing them to an explicit direct-IP sender.
/// </summary>
/// <remarks>
/// Use one client per configured DHMP session and serialize sends through completion.
/// The caller owns the sender lifetime. There is no implicit endpoint, connection, compatibility transport
/// or network backend. A failed backend send is not retried and its reserved budget is not refunded.
/// </remarks>
public sealed class DhmpClient
{
    private readonly IDhmpPacketSender _sender;
    private readonly DhmpSessionContract _session;
    private readonly DhmpFixedContract _contract;
    private readonly DhmpPmaxBudget _budget;

    public DhmpClient(IDhmpPacketSender sender, DhmpSessionContract session)
    {
        ArgumentNullException.ThrowIfNull(sender);
        session.Validate();

        if (sender.MaximumPayloadBytes < session.MaxPacketPayloadBytes)
            throw new ArgumentException("Session exceeds the sender's direct-IP payload limit.", nameof(session));

        _sender = sender;
        _session = session;
        _contract = session.FixedContract;
        _budget = new DhmpPmaxBudget(session.Pmax);
    }

    public DhmpSessionContract Session => _session;

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        _contract.ValidatePayload(payload.Length);
        return SendBatchAsync(payload, cancellationToken);
    }

    public ValueTask SendBatchAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _contract.ValidatePacket(packet.Length);

        if (!_budget.TryConsume(packet.Length / _contract.PayloadSize))
            throw new DhmpProtocolException("Configured DHMP session message budget exhausted.");

        return _sender.SendPacketAsync(packet, cancellationToken);
    }
}
