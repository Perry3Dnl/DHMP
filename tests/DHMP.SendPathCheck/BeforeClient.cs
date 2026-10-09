// Test-only single-send baseline from main 678b2d2.
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
internal sealed class BeforeClient
{
    private readonly IDhmpPacketSender _sender;
    private readonly IDhmpDynamicPacketSender? _dynamicSender;
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpSendPolicy _sendPolicy;
    private readonly DhmpPmaxBudget? _budget;
    private readonly DhmpPacingSchedule? _pacer;
    private readonly DhmpAdaptiveRateController? _adaptiveRateController;

    public BeforeClient(
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
        _dynamicSender = sender as IDhmpDynamicPacketSender;
        _wireContract = wireContract;
        _sendPolicy = sendPolicy;
        _adaptiveRateController =
            adaptiveRateController;

        if (sendPolicy.RatePolicy ==
            DhmpRatePolicy.SmoothPacing)
        {
            _pacer = new DhmpPacingSchedule(
                adaptiveRateController is not null
                    ? adaptiveRateController.CurrentMessagesPerSecond
                    : sendPolicy.Pmax);
        }
        else if (sendPolicy.RatePolicy ==
                 DhmpRatePolicy.RejectWindow)
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

    public long CurrentMessagesPerSecond =>
        _adaptiveRateController is not null
            ? _adaptiveRateController.CurrentMessagesPerSecond
            : _sendPolicy.Pmax;

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
        if (_sendPolicy.RatePolicy != DhmpRatePolicy.Unlimited)
            return SendWithRatePolicyAsync(record, cancellationToken);

        // Preserve the original async boundary's ExecutionContext and
        // SynchronizationContext restoration without a resumable await state.
        var operation = new UnlimitedSendOperation(this, record, cancellationToken);
        var boundary = AsyncValueTaskMethodBuilder.Create();
        boundary.Start(ref operation);
        return operation.Result;
    }

    private struct UnlimitedSendOperation : IAsyncStateMachine
    {
        private readonly BeforeClient _client;
        private readonly ReadOnlyMemory<byte> _record;
        private readonly CancellationToken _cancellationToken;
        internal ValueTask Result;

        internal UnlimitedSendOperation(BeforeClient client, ReadOnlyMemory<byte> record, CancellationToken cancellationToken)
        {
            _client = client;
            _record = record;
            _cancellationToken = cancellationToken;
            Result = default;
        }

        public void MoveNext()
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();

                int recordSize = _client._wireContract.RecordSize;

                if (_record.Length != recordSize)
                    throw new DhmpProtocolException(
                        $"Expected one {recordSize}-byte DHMP record; received {_record.Length} bytes.");

                int senderMaximum = _client._sender.MaximumPayloadBytes;

                IDhmpDynamicPacketSender? dynamicSender = _client._dynamicSender;
                if (dynamicSender is not null)
                {
                    int liveMaximum = dynamicSender.CurrentMaximumPayloadBytes;
                    if (liveMaximum < senderMaximum)
                        senderMaximum = liveMaximum;
                }

                if (_client._sendPolicy.MaximumPayloadBytes < senderMaximum)
                    senderMaximum = _client._sendPolicy.MaximumPayloadBytes;

                if (senderMaximum < recordSize)
                    throw new DhmpProtocolException(
                        "Current DHMP path budget cannot fit one complete record.");

                ValueTask pending = _client._sender.SendPacketAsync(_record, _cancellationToken);
                if (!pending.IsCompletedSuccessfully)
                {
                    Result = AwaitSendAsync(pending);
                    return;
                }

                // Consume completed IValueTaskSource-backed sends exactly once,
                // as the original async method did before returning to the caller.
                pending.GetAwaiter().GetResult();
                Result = ValueTask.CompletedTask;
            }
            catch (Exception error)
            {
                // Preserve deferred exceptions and async cancellation classification.
                Result = CaptureSendFailureAsync(error);
            }
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) =>
            throw new NotSupportedException("The synchronous send boundary cannot be suspended.");
    }

    private static async ValueTask AwaitSendAsync(ValueTask pending) =>
        await pending.ConfigureAwait(false);

    private static async ValueTask CaptureSendFailureAsync(Exception error)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private async ValueTask SendWithRatePolicyAsync(
        ReadOnlyMemory<byte> record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int recordSize = _wireContract.RecordSize;

        if (record.Length != recordSize)
            throw new DhmpProtocolException(
                $"Expected one {recordSize}-byte DHMP record; received {record.Length} bytes.");

        int senderMaximum = _sender.MaximumPayloadBytes;

        if (_sender is IDhmpDynamicPacketSender dynamicSender)
        {
            int liveMaximum = dynamicSender.CurrentMaximumPayloadBytes;
            if (liveMaximum < senderMaximum)
                senderMaximum = liveMaximum;
        }

        if (_sendPolicy.MaximumPayloadBytes < senderMaximum)
            senderMaximum = _sendPolicy.MaximumPayloadBytes;

        if (senderMaximum < recordSize)
            throw new DhmpProtocolException(
                "Current DHMP path budget cannot fit one complete record.");

        if (_sendPolicy.RatePolicy == DhmpRatePolicy.SmoothPacing)
        {
            await PaceAsync(
                1,
                cancellationToken)
            .ConfigureAwait(false);
        }
        else if (_sendPolicy.RatePolicy == DhmpRatePolicy.RejectWindow &&
                 !_budget!.TryConsume(1))
        {
            throw new DhmpProtocolException(
                "Configured local DHMP send budget exhausted.");
        }

        await _sender.SendPacketAsync(
            record,
            cancellationToken)
        .ConfigureAwait(false);
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
        else if (_sendPolicy.RatePolicy ==
                 DhmpRatePolicy.RejectWindow &&
                 !_budget!.TryConsume(messages))
        {
            throw new DhmpProtocolException(
                "Configured local DHMP send budget exhausted.");
        }

        int recordSize =
            _wireContract.RecordSize;

        for (int offset = 0;
             offset < packet.Length;
             offset += recordSize)
        {
            await _sender.SendPacketAsync(
                packet.Slice(
                    offset,
                    recordSize),
                cancellationToken)
            .ConfigureAwait(false);
        }
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


