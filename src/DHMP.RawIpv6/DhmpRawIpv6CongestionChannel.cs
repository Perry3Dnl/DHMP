using System.Collections.Concurrent;
using System.Diagnostics;
using DHMP.Protocol;
using DHMP.Security;
using DHMP.Server;

namespace DHMP.RawIpv6;

/// <summary>
/// Ongoing authenticated control exchange over experimental protocol 254.
/// One receive loop demultiplexes congestion feedback and RTT/path probes so
/// multiple raw control sockets do not compete for the same packets.
/// </summary>
public sealed class DhmpRawIpv6CongestionChannel :
    IDisposable
{
    private readonly IDhmpControlPacketChannel _channel;
    private readonly DhmpPskChaCha20Poly1305Session _securitySession;
    private readonly ConcurrentDictionary<
        ulong,
        TaskCompletionSource<DhmpPathTelemetry>>
        _pendingProbes = new();

    private readonly ConcurrentDictionary<
        ulong,
        TaskCompletionSource<int>>
        _pendingPathMtuProbes = new();

    private long _probeSequence;
    private long _pathMtuProbeSequence;

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

    internal DhmpRawIpv6CongestionChannel(
        IDhmpControlPacketChannel channel,
        DhmpPskChaCha20Poly1305Session securitySession)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(securitySession);

        _channel = channel;
        _securitySession = securitySession;
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

    public async Task<DhmpPathTelemetry> ProbeAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(timeout));

        long next =
            Interlocked.Increment(
                ref _probeSequence);

        if (next <= 0)
            throw new InvalidOperationException(
                "DHMP path probe sequence exhausted.");

        ulong probeId =
            checked((ulong)next);

        long timestamp =
            Stopwatch.GetTimestamp();

        if (timestamp <= 0)
            throw new InvalidOperationException(
                "Monotonic timestamp source returned an invalid value.");

        var snapshot =
            _securitySession.GetReceiveSnapshot();

        var request =
            new DhmpPathProbeMessage(
                DhmpPathProbeType.Request,
                probeId,
                checked((ulong)timestamp),
                snapshot.HighestPacketCounter,
                snapshot.WindowSpan,
                snapshot.MissingWithinWindow,
                snapshot.AcceptedPackets);

        byte[] packet =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .PathProbePacketSize];

        _securitySession.EncodePathProbe(
            request,
            packet);

        var completion =
            new TaskCompletionSource<DhmpPathTelemetry>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

        if (!_pendingProbes.TryAdd(
                probeId,
                completion))
            throw new InvalidOperationException(
                "Duplicate DHMP path probe identifier.");

        try
        {
            await _channel.SendPacketAsync(
                packet,
                cancellationToken)
            .ConfigureAwait(false);

            return await completion.Task.WaitAsync(
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
        }
        finally
        {
            _pendingProbes.TryRemove(
                probeId,
                out _);
        }
    }

    /// <summary>
    /// Perform one authenticated RFC 8899-style Datagram PLPMTUD search.
    /// RunAdaptiveReceiveLoopAsync must be active on both peers so requests can be
    /// acknowledged and responses can complete the local probes.
    /// </summary>
    public Task<DhmpPathMtuDiscoveryResult> DiscoverPathMtuAsync(
        DhmpPathMtuDiscoveryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return DhmpPathMtuSearch.RunAsync(
            options,
            (pathMtu, token) =>
                ProbePathMtuOnceAsync(
                    pathMtu,
                    options.ProbeTimeout,
                    token),
            cancellationToken);
    }

    /// <summary>
    /// Re-confirm one previously selected path MTU. This is useful for black-hole
    /// detection before an application continues using a larger discovered budget.
    /// </summary>
    public async Task<bool> ConfirmPathMtuAsync(
        int pathMtu,
        DhmpPathMtuDiscoveryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (pathMtu < DhmpIpv6PathBudget.MinimumIpv6Mtu ||
            pathMtu > options.MaximumPathMtu)
            throw new ArgumentOutOfRangeException(nameof(pathMtu));

        for (int attempt = 0;
             attempt < options.MaximumProbeAttempts;
             attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await ProbePathMtuOnceAsync(
                    pathMtu,
                    options.ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Receive authenticated congestion feedback and path probes on one control socket.
    /// Path loss feedback is applied to the same adaptive controller; RTT is exposed
    /// through the optional observer but is not yet used as an independent throttle signal.
    /// </summary>
    public async Task RunAdaptiveReceiveLoopAsync(
        DhmpAdaptiveRateController controller,
        Action<DhmpPathTelemetry>? pathTelemetryObserver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);

        byte[] packet =
            new byte[ushort.MaxValue];

        while (!cancellationToken
            .IsCancellationRequested)
        {
            int received;

            try
            {
                received =
                    await _channel.ReceivePacketAsync(
                        packet,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken
                    .IsCancellationRequested)
            {
                break;
            }

            if (received ==
                    DhmpPskChaCha20Poly1305Session
                        .CongestionFeedbackPacketSize &&
                _securitySession
                    .TryDecodeCongestionFeedback(
                        packet.AsSpan(0, received),
                        out var feedback))
            {
                controller.ApplyFeedback(
                    feedback);

                continue;
            }

            if (received ==
                    DhmpPskChaCha20Poly1305Session
                        .PathProbePacketSize &&
                _securitySession.TryDecodePathProbe(
                    packet.AsSpan(0, received),
                    out var probe))
            {
                if (probe.Type ==
                    DhmpPathProbeType.Request)
                {
                    await SendProbeResponseAsync(
                        probe,
                        cancellationToken)
                    .ConfigureAwait(false);

                    continue;
                }

                CompleteProbe(
                    probe,
                    controller,
                    pathTelemetryObserver);

                continue;
            }

            if (received >=
                    DhmpPskChaCha20Poly1305Session
                        .PathMtuProbeMinimumPacketSize &&
                _securitySession.TryDecodePathMtuProbe(
                    packet.AsSpan(0, received),
                    out var pathMtuProbe))
            {
                if (pathMtuProbe.Type ==
                    DhmpPathMtuProbeType.Request)
                {
                    await SendPathMtuProbeResponseAsync(
                        pathMtuProbe,
                        cancellationToken)
                    .ConfigureAwait(false);

                    continue;
                }

                CompletePathMtuProbe(
                    pathMtuProbe);

                continue;
            }

            // Other control families may share protocol 254 before/around this
            // ongoing loop. Unknown or unauthenticated payloads do not alter rate state.
        }
    }

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

    private async ValueTask<bool> ProbePathMtuOnceAsync(
        int pathMtu,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        int payloadBytes =
            checked(
                pathMtu -
                DhmpIpv6PathBudget.Ipv6BaseHeaderBytes);

        if (payloadBytes <
                DhmpPskChaCha20Poly1305Session
                    .PathMtuProbeMinimumPacketSize ||
            payloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pathMtu));

        long next =
            Interlocked.Increment(
                ref _pathMtuProbeSequence);

        if (next <= 0)
            throw new InvalidOperationException(
                "DHMP path-MTU probe sequence exhausted.");

        ulong probeId =
            checked((ulong)next);

        var request =
            new DhmpPathMtuProbeMessage(
                DhmpPathMtuProbeType.Request,
                probeId,
                payloadBytes);

        byte[] packet =
            new byte[payloadBytes];

        _securitySession.EncodePathMtuProbe(
            request,
            packet);

        var completion =
            new TaskCompletionSource<int>(
                TaskCreationOptions
                    .RunContinuationsAsynchronously);

        if (!_pendingPathMtuProbes.TryAdd(
                probeId,
                completion))
            throw new InvalidOperationException(
                "Duplicate DHMP path-MTU probe identifier.");

        try
        {
            try
            {
                await _channel.SendPacketAsync(
                    packet,
                    cancellationToken)
                .ConfigureAwait(false);
            }
            catch (System.Net.Sockets.SocketException error)
                when (error.SocketErrorCode ==
                    System.Net.Sockets.SocketError.MessageSize)
            {
                return false;
            }

            try
            {
                int acknowledgedBytes =
                    await completion.Task.WaitAsync(
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                return acknowledgedBytes ==
                    payloadBytes;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
        finally
        {
            _pendingPathMtuProbes.TryRemove(
                probeId,
                out _);
        }
    }

    private async ValueTask SendPathMtuProbeResponseAsync(
        DhmpPathMtuProbeMessage request,
        CancellationToken cancellationToken)
    {
        var response =
            new DhmpPathMtuProbeMessage(
                DhmpPathMtuProbeType.Response,
                request.ProbeId,
                request.ProbedPayloadBytes);

        byte[] packet =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .PathMtuProbeMinimumPacketSize];

        _securitySession.EncodePathMtuProbe(
            response,
            packet);

        await _channel.SendPacketAsync(
            packet,
            cancellationToken)
        .ConfigureAwait(false);
    }

    private void CompletePathMtuProbe(
        DhmpPathMtuProbeMessage response)
    {
        if (!_pendingPathMtuProbes.TryRemove(
                response.ProbeId,
                out var completion))
            return;

        completion.TrySetResult(
            response.ProbedPayloadBytes);
    }

    private async ValueTask SendProbeResponseAsync(
        DhmpPathProbeMessage request,
        CancellationToken cancellationToken)
    {
        var snapshot =
            _securitySession.GetReceiveSnapshot();

        var response =
            new DhmpPathProbeMessage(
                DhmpPathProbeType.Response,
                request.ProbeId,
                request.SenderTimestamp,
                snapshot.HighestPacketCounter,
                snapshot.WindowSpan,
                snapshot.MissingWithinWindow,
                snapshot.AcceptedPackets);

        byte[] packet =
            new byte[
                DhmpPskChaCha20Poly1305Session
                    .PathProbePacketSize];

        _securitySession.EncodePathProbe(
            response,
            packet);

        await _channel.SendPacketAsync(
            packet,
            cancellationToken)
        .ConfigureAwait(false);
    }

    private void CompleteProbe(
        DhmpPathProbeMessage response,
        DhmpAdaptiveRateController controller,
        Action<DhmpPathTelemetry>? observer)
    {
        if (!_pendingProbes.TryRemove(
                response.ProbeId,
                out var completion))
            return;

        long now =
            Stopwatch.GetTimestamp();

        long start =
            checked((long)
                response.SenderTimestamp);

        if (now < start)
        {
            completion.TrySetException(
                new DhmpSecurityException(
                    "DHMP path probe returned an invalid monotonic timestamp."));

            return;
        }

        TimeSpan rtt =
            TimeSpan.FromSeconds(
                (double)(now - start) /
                Stopwatch.Frequency);

        var telemetry =
            new DhmpPathTelemetry(
                rtt,
                response.HighestPacketCounter,
                response.WindowSpan,
                response.MissingWithinWindow,
                response.AcceptedPackets);

        controller.ApplyFeedback(
            DhmpPathRateAdvisor.Evaluate(
                telemetry));

        observer?.Invoke(telemetry);

        completion.TrySetResult(
            telemetry);
    }

    public void Dispose()
        => _channel.Dispose();
}
