using System.Diagnostics;
using DHMP.Protocol;

namespace DHMP.Client;

/// <summary>
/// Validates, rate-controls and sends headerless DHMP packets through an explicit direct-IP sender.
/// </summary>
/// <remarks>
/// Send calls must be serialized. Smooth pacing spaces packet submissions according to logical
/// message count. When an adaptive controller is supplied, authenticated feedback may lower the
/// pacing rate and no-pressure feedback may recover it gradually. The local Pmax remains authoritative.
/// </remarks>
public sealed class DhmpClient
{
    private readonly IDhmpPacketSender _sender;
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpSendPolicy _sendPolicy;
    private readonly DhmpPmaxBudget? _budget;
    private readonly DhmpPacingSchedule? _pacer;
    private readonly DhmpAdaptiveRateController? _adaptiveRateController;

    public DhmpClient(
        IDhmpPacketSender sender,
        DhmpWireContract wireContract,
        DhmpSendPolicy sendPolicy,
        DhmpAdaptiveRateController? adaptiveRateController = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        wireContract.Validate();
        sendPolicy.Validate(wireContract);

        if (sender.MaximumPayloadBytes <
            sendPolicy.MaximumPayloadBytes)
            throw new ArgumentException(
                "Local send policy exceeds the sender backend payload limit.",
                nameof(sendPolicy));

        if (adaptiveRateController is not null)
        {
            if (sendPolicy.RatePolicy !=
                DhmpRatePolicy.SmoothPacing)
                throw new ArgumentException(
                    "Adaptive rate control requires SmoothPacing.",
                    nameof(adaptiveRateController));

            if (adaptiveRateController.MaximumMessagesPerSecond >
                sendPolicy.Pmax)
                throw new ArgumentException(
                    "Adaptive controller maximum cannot exceed local Pmax.",
                    nameof(adaptiveRateController));
        }

        _sender = sender;
        _wireContract = wireContract;
        _sendPolicy = sendPolicy;
        _adaptiveRateController =
            adaptiveRateController;

        if (sendPolicy.RatePolicy ==
            DhmpRatePolicy.SmoothPacing)
        {
            _pacer = new DhmpPacingSchedule(
                adaptiveRateController?
                    .CurrentMessagesPerSecond ??
                sendPolicy.Pmax);
        }
        else
        {
            _budget =
                new DhmpPmaxBudget(
                    sendPolicy.Pmax);
        }
    }

    public DhmpWireContract WireContract =>
        _wireContract;

    public DhmpSendPolicy SendPolicy =>
        _sendPolicy;

    public DhmpAdaptiveRateController?
        AdaptiveRateController =>
            _adaptiveRateController;

    public int CurrentMessagesPerSecond =>
        _adaptiveRateController?
            .CurrentMessagesPerSecond ??
        _sendPolicy.Pmax;

    /// <summary>
    /// Current whole-record payload ceiling after combining local policy with any
    /// live sender/path limit.
    /// </summary>
    public int CurrentMaximumPayloadBytes
    {
        get
        {
            int senderMaximum =
                _sender.MaximumPayloadBytes;

            if (_sender is IDhmpDynamicPacketSender dynamicSender)
            {
                senderMaximum =
                    Math.Min(
                        senderMaximum,
                        dynamicSender.CurrentMaximumPayloadBytes);
            }

            int rawMaximum =
                Math.Min(
                    _sendPolicy.MaximumPayloadBytes,
                    senderMaximum);

            return
                rawMaximum /
                _wireContract.RecordSize *
                _wireContract.RecordSize;
        }
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> record,
        CancellationToken cancellationToken = default)
    {
        _wireContract.ValidateRecord(
            record.Length);

        return SendBatchAsync(
            record,
            cancellationToken);
    }

    public async ValueTask SendBatchAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        cancellationToken
            .ThrowIfCancellationRequested();

        int currentMaximumPayloadBytes =
            CurrentMaximumPayloadBytes;

        if (currentMaximumPayloadBytes <
            _wireContract.RecordSize)
            throw new DhmpProtocolException(
                "Current DHMP path budget cannot fit one complete record.");

        _wireContract.ValidatePacket(
            packet.Length,
            currentMaximumPayloadBytes);

        int messages =
            packet.Length /
            _wireContract.RecordSize;

        if (_sendPolicy.RatePolicy ==
            DhmpRatePolicy.SmoothPacing)
        {
            await PaceAsync(
                messages,
                cancellationToken)
            .ConfigureAwait(false);
        }
        else if (!_budget!.TryConsume(messages))
        {
            throw new DhmpProtocolException(
                "Configured local DHMP send budget exhausted.");
        }

        await _sender.SendPacketAsync(
            packet,
            cancellationToken)
        .ConfigureAwait(false);
    }

    private async ValueTask PaceAsync(
        int messages,
        CancellationToken cancellationToken)
    {
        _pacer!.UpdateRate(
            CurrentMessagesPerSecond);

        long now =
            Stopwatch.GetTimestamp();

        TimeSpan delay =
            _pacer.GetDelay(
                messages,
                now);

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(
                delay,
                cancellationToken)
            .ConfigureAwait(false);
        }

        cancellationToken
            .ThrowIfCancellationRequested();

        _pacer.UpdateRate(
            CurrentMessagesPerSecond);

        _pacer.Commit(
            messages,
            Stopwatch.GetTimestamp());
    }
}
