using System.Diagnostics;
using DHMP.Protocol;

namespace DHMP.Client;

/// <summary>
/// Validates, rate-controls and sends headerless DHMP packets through an explicit direct-IP sender.
/// </summary>
/// <remarks>
/// Send calls must be serialized. Smooth pacing spaces packet submissions according to logical
/// message count; it is not congestion control and has no receiver feedback.
/// </remarks>
public sealed class DhmpClient
{
    private readonly IDhmpPacketSender _sender;
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpSendPolicy _sendPolicy;
    private readonly DhmpPmaxBudget? _budget;
    private readonly DhmpPacingSchedule? _pacer;

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

        if (sendPolicy.RatePolicy == DhmpRatePolicy.SmoothPacing)
            _pacer = new DhmpPacingSchedule(sendPolicy.Pmax);
        else
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

    public async ValueTask SendBatchAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _wireContract.ValidatePacket(
            packet.Length,
            _sendPolicy.MaximumPayloadBytes);

        int messages =
            packet.Length / _wireContract.RecordSize;

        if (_sendPolicy.RatePolicy == DhmpRatePolicy.SmoothPacing)
        {
            await PaceAsync(
                messages,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!_budget!.TryConsume(messages))
        {
            throw new DhmpProtocolException(
                "Configured local DHMP send budget exhausted.");
        }

        await _sender.SendPacketAsync(
            packet,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PaceAsync(
        int messages,
        CancellationToken cancellationToken)
    {
        long now = Stopwatch.GetTimestamp();
        TimeSpan delay =
            _pacer!.GetDelay(messages, now);

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(
                delay,
                cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        _pacer.Commit(
            messages,
            Stopwatch.GetTimestamp());
    }
}
