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
public sealed class DhmpClient
{
    private readonly IDhmpPacketSender _sender;
    private readonly IDhmpDynamicPacketSender? _dynamicSender;
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpSendPolicy _sendPolicy;
    private readonly int _recordSize;
    private readonly int _maximumPayloadBytes;
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
        _dynamicSender = sender as IDhmpDynamicPacketSender;
        _wireContract = wireContract;
        _sendPolicy = sendPolicy;
        _recordSize = wireContract.RecordSize;
        _maximumPayloadBytes =
            sendPolicy.MaximumPayloadBytes;
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
            int rawMaximum =
                _maximumPayloadBytes;

            IDhmpDynamicPacketSender? dynamicSender =
                _dynamicSender;

            if (dynamicSender is not null)
            {
                int liveMaximum =
                    dynamicSender.CurrentMaximumPayloadBytes;

                if (liveMaximum < rawMaximum)
                    rawMaximum = liveMaximum;
            }

            return
                rawMaximum /
                _recordSize *
                _recordSize;
        }
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> record,
        CancellationToken cancellationToken = default)
    {
        // Use the async builder only as a synchronous execution-context boundary.
        // The operation itself stays non-suspending unless pacing or backend I/O
        // actually returns incomplete.
        var operation =
            new SendOperation(
                this,
                record,
                cancellationToken);

        var boundary =
            AsyncValueTaskMethodBuilder.Create();

        boundary.Start(
            ref operation);

        return operation.Result;
    }

    private struct SendOperation : IAsyncStateMachine
    {
        private readonly DhmpClient _client;
        private readonly ReadOnlyMemory<byte> _record;
        private readonly CancellationToken _cancellationToken;

        internal ValueTask Result;

        internal SendOperation(
            DhmpClient client,
            ReadOnlyMemory<byte> record,
            CancellationToken cancellationToken)
        {
            _client = client;
            _record = record;
            _cancellationToken =
                cancellationToken;
            Result = default;
        }

        public void MoveNext()
        {
            try
            {
                _client.ValidateSingleRecord(
                    _record,
                    _cancellationToken);

                switch (_client._sendPolicy.RatePolicy)
                {
                    case DhmpRatePolicy.RejectWindow:
                        if (!_client._budget!.TryConsume(1))
                            throw new DhmpProtocolException(
                                "Configured local DHMP send budget exhausted.");
                        break;

                    case DhmpRatePolicy.SmoothPacing:
                        if (!_client.TryPaceSynchronously(
                                1,
                                _cancellationToken,
                                out TimeSpan delay))
                        {
                            Result =
                                _client.PaceThenSendAsync(
                                    _record,
                                    1,
                                    delay,
                                    _cancellationToken);
                            return;
                        }
                        break;

                    case DhmpRatePolicy.Unlimited:
                        break;

                    default:
                        throw new InvalidOperationException(
                            "Unsupported DHMP send rate policy.");
                }

                ValueTask pending =
                    _client._sender.SendPacketAsync(
                        _record,
                        _cancellationToken);

                if (!pending.IsCompletedSuccessfully)
                {
                    Result =
                        AwaitSendAsync(
                            pending);
                    return;
                }

                // Consume completed IValueTaskSource-backed sends exactly once,
                // matching normal await semantics without creating a resumable
                // state machine for the common completed-send path.
                pending.GetAwaiter()
                    .GetResult();

                Result =
                    ValueTask.CompletedTask;
            }
            catch (Exception error)
            {
                // Preserve deferred exceptions and async cancellation
                // classification rather than throwing from SendAsync itself.
                Result =
                    CaptureSendFailureAsync(
                        error);
            }
        }

        public void SetStateMachine(
            IAsyncStateMachine stateMachine) =>
            throw new NotSupportedException(
                "The synchronous send boundary cannot be suspended.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateSingleRecord(
        ReadOnlyMemory<byte> record,
        CancellationToken cancellationToken)
    {
        cancellationToken
            .ThrowIfCancellationRequested();

        int recordSize =
            _recordSize;

        if (record.Length != recordSize)
            throw new DhmpProtocolException(
                $"Expected one {recordSize}-byte DHMP record; received {record.Length} bytes.");

        IDhmpDynamicPacketSender? dynamicSender =
            _dynamicSender;

        if (dynamicSender is not null &&
            dynamicSender.CurrentMaximumPayloadBytes <
                recordSize)
            throw new DhmpProtocolException(
                "Current DHMP path budget cannot fit one complete record.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryPaceSynchronously(
        int messages,
        CancellationToken cancellationToken,
        out TimeSpan delay)
    {
        DhmpPacingSchedule pacer = _pacer!;
        DhmpAdaptiveRateController? controller = _adaptiveRateController;

        // Only the single-record fast path uses the fused schedule.
        // Batch pacing retains its existing multi-record rounding semantics.
        if (controller is not null)
            pacer.UpdateRate(controller.CurrentMessagesPerSecond);

        cancellationToken.ThrowIfCancellationRequested();

        if (controller is not null)
            pacer.UpdateRate(controller.CurrentMessagesPerSecond);

        delay = pacer.TryCommitOne(Stopwatch.GetTimestamp());
        return delay == TimeSpan.Zero;
    }

    private async ValueTask PaceThenSendAsync(
        ReadOnlyMemory<byte> record,
        int messages,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        await Task.Delay(
            delay,
            cancellationToken)
        .ConfigureAwait(false);

        cancellationToken
            .ThrowIfCancellationRequested();

        DhmpAdaptiveRateController? controller =
            _adaptiveRateController;

        if (controller is not null)
        {
            _pacer!.UpdateRate(
                controller.CurrentMessagesPerSecond);
        }

        _pacer!.Commit(
            messages,
            Stopwatch.GetTimestamp());

        await _sender.SendPacketAsync(
            record,
            cancellationToken)
        .ConfigureAwait(false);
    }

    private static async ValueTask AwaitSendAsync(
        ValueTask pending) =>
        await pending.ConfigureAwait(false);

    private static async ValueTask CaptureSendFailureAsync(
        Exception error)
    {
        await Task.CompletedTask
            .ConfigureAwait(false);

        System.Runtime.ExceptionServices.ExceptionDispatchInfo
            .Capture(error)
            .Throw();
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
        DhmpPacingSchedule pacer =
            _pacer!;

        DhmpAdaptiveRateController? controller =
            _adaptiveRateController;

        if (controller is not null)
        {
            pacer.UpdateRate(
                controller.CurrentMessagesPerSecond);
        }

        long now =
            Stopwatch.GetTimestamp();

        TimeSpan delay =
            pacer.GetDelay(
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

        if (controller is not null)
        {
            pacer.UpdateRate(
                controller.CurrentMessagesPerSecond);
        }

        pacer.Commit(
            messages,
            Stopwatch.GetTimestamp());
    }
}

