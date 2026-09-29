using DHMP.Protocol;
using DHMP.Security;
using DHMP.Server;

namespace DHMP.RawIpv6;

/// <summary>
/// Ongoing authenticated congestion-feedback exchange over experimental control protocol 254.
/// The channel is session-bound through the selected PSK security session.
/// </summary>
public sealed class DhmpRawIpv6CongestionChannel :
    IDisposable
{
    private readonly DhmpRawIpv6ControlChannel _channel;
    private readonly DhmpPskChaCha20Poly1305Session _securitySession;

    public DhmpRawIpv6CongestionChannel(
        DhmpRawIpv6Options options,
        DhmpPskChaCha20Poly1305Session securitySession)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(securitySession);

        _securitySession =
            securitySession;

        _channel =
            new DhmpRawIpv6ControlChannel(
                options);
    }

    public async ValueTask SendAsync(
        DhmpCongestionFeedback feedback,
        CancellationToken cancellationToken = default)
    {
        byte[] packet =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .CongestionFeedbackPacketSize];

        _securitySession
            .EncodeCongestionFeedback(
                feedback,
                packet);

        await _channel.SendPacketAsync(
            packet,
            cancellationToken)
        .ConfigureAwait(false);
    }

    public async ValueTask<DhmpCongestionFeedback>
        ReceiveAsync(
            CancellationToken cancellationToken = default)
    {
        byte[] packet =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .CongestionFeedbackPacketSize];

        int received =
            await _channel.ReceivePacketAsync(
                packet,
                cancellationToken)
            .ConfigureAwait(false);

        if (received != packet.Length ||
            !_securitySession
                .TryDecodeCongestionFeedback(
                    packet,
                    out var feedback))
            throw new DhmpSecurityException(
                "DHMP congestion feedback failed session authentication, replay or format validation.");

        return feedback;
    }

    public async Task RunAdaptiveReceiveLoopAsync(
        DhmpAdaptiveRateController controller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);

        while (!cancellationToken
            .IsCancellationRequested)
        {
            DhmpCongestionFeedback feedback;

            try
            {
                feedback =
                    await ReceiveAsync(
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken
                    .IsCancellationRequested)
            {
                break;
            }

            controller.ApplyFeedback(
                feedback);
        }
    }

    /// <summary>
    /// Periodically compare bounded application pressure and send one authenticated
    /// feedback report to the remote sender. This loop owns no application data.
    /// </summary>
    public async Task RunReporterLoopAsync(
        DhmpBoundedReceiveDispatcher dispatcher,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(interval));

        DhmpReceiveDispatchSnapshot previous =
            dispatcher.GetSnapshot();

        using var timer =
            new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(
                    cancellationToken)
                .ConfigureAwait(false))
            {
                DhmpReceiveDispatchSnapshot current =
                    dispatcher.GetSnapshot();

                DhmpCongestionFeedback feedback =
                    DhmpCongestionAdvisor.Evaluate(
                        previous,
                        current);

                await SendAsync(
                    feedback,
                    cancellationToken)
                .ConfigureAwait(false);

                previous = current;
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken
                .IsCancellationRequested)
        {
        }
    }

    public void Dispose()
        => _channel.Dispose();
}
