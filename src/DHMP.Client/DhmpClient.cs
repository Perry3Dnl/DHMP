using DHMP.Protocol;

namespace DHMP.Client;

/// <summary>
/// Validates and budgets headerless DHMP packets before handing them to an explicit direct-IP sender.
/// </summary>
public sealed class DhmpClient
{
    private readonly IDhmpPacketSender _sender;
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpSendPolicy _sendPolicy;
    private readonly DhmpPmaxBudget _budget;

    public DhmpClient(
        IDhmpPacketSender sender,
        DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy)
    {
        ArgumentNullException.ThrowIfNull(sender);
        wireContract.Validate();
        sendPolicy.Validate(wireContract);

        if (sender.MaximumPayloadBytes < sendPolicy.MaximumPayloadBytes)
            throw new ArgumentException(
                "Local send policy exceeds the sender backend payload limit.",
                nameof(sendPolicy));

        _sender = sender;
        _wireContract = wireContract;
        _sendPolicy = sendPolicy;
        _budget = new DhmpPmaxBudget(sendPolicy.Pmax);
    }

    public DhmpWireContract WireContract => _wireContract;
    public DhmpSendPolicy SendPolicy => _sendPolicy;

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> record,
        CancellationToken cancellationToken = default)
    {
        _wireContract.ValidateRecord(record.Length);
        return SendBatchAsync(record, cancellationToken);
    }

    public ValueTask SendBatchAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _wireContract.ValidatePacket(
            packet.Length,
            _sendPolicy.MaximumPayloadBytes);

        if (!_budget.TryConsume(packet.Length / _wireContract.RecordSize))
            throw new DhmpProtocolException(
                "Configured local DHMP send budget exhausted.");

        return _sender.SendPacketAsync(packet, cancellationToken);
    }
}
