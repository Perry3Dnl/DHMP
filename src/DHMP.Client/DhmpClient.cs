using DHMP.Protocol;

namespace DHMP.Client;

/// <summary>Validates and budgets complete packets before handing them to an explicit direct-IP sender.</summary>
/// <remarks>
/// Use one client per preconfigured session and serialize sends through completion.
/// The caller owns the sender lifetime. There is no implicit endpoint, connection or network backend.
/// A failed backend send is not retried and its reserved budget is not refunded.
/// </remarks>
public sealed class DhmpClient
{
    private readonly IDhmpPacketSender _sender;
    private readonly DhmpFixedContract _contract;
    private readonly DhmpPmaxBudget _budget;

    public DhmpClient(IDhmpPacketSender sender, DhmpFixedContract contract)
    {
        ArgumentNullException.ThrowIfNull(sender);
        contract.Validate();
        if (sender.MaximumPayloadBytes < contract.MaxPacketPayloadBytes)
            throw new ArgumentException("Contract exceeds the sender's packet payload limit.", nameof(contract));
        _sender = sender;
        _contract = contract;
        _budget = new DhmpPmaxBudget(contract.Pmax);
    }

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
            throw new DhmpProtocolException("Configured message budget exhausted.");
        return _sender.SendPacketAsync(packet, cancellationToken);
    }
}
