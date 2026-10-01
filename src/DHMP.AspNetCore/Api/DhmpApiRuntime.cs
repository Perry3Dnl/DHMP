using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DHMP.AspNetCore;

internal sealed class DhmpApiRuntime(IOptions<DhmpApiOptions> configured, DhmpRuntimeState state,
    DhmpApiPipeline pipeline, IDhmpApiTransportFactory factory, ILogger<DhmpApiRuntime> logger) : BackgroundService, IDhmpApiExchange
{
    private readonly TaskCompletionSource<DhmpApiExchange> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (!state.LicenseValidated) throw new InvalidOperationException("DHMP API cannot start before license validation.");
        configured.Value.Validate();
        return base.StartAsync(cancellationToken);
    }
    public async Task<byte[]> RequestAsync(byte[] message, CancellationToken cancellationToken)
    {
        var exchange = await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await exchange.RequestAsync(message, cancellationToken).ConfigureAwait(false);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ioStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        DhmpApiExchange? exchange = null;
        IDhmpApiTransport? transport = null;
        Task? receiving = null;
        try
        {
            var settings = configured.Value;
            transport = await factory.OpenAsync(settings, stoppingToken).ConfigureAwait(false);
            exchange = new DhmpApiExchange(settings, transport.SendAsync, pipeline.DispatchAsync);
            receiving = transport.RunAsync(exchange.Receive, ioStop.Token);
            state.SetApiSession(transport.SessionId);
            state.SetApiReady(true);
            _ready.TrySetResult(exchange);
            logger.LogInformation("DHMP API application profile DAPI/1 ready for configured peer {Peer}", settings.RemoteAddress);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (!receiving.IsCompleted && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) exchange.Expire();
            await receiving.ConfigureAwait(false);
            if (!stoppingToken.IsCancellationRequested) throw new IOException("DHMP API receive loop ended unexpectedly.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { _ready.TrySetCanceled(stoppingToken); }
        catch (Exception error) { _ready.TrySetException(error); throw; }
        finally
        {
            state.SetApiReady(false);
            state.SetApiSession(null);
            ioStop.Cancel();
            if (receiving is not null)
            {
                try { await receiving.ConfigureAwait(false); }
                catch (OperationCanceledException) when (ioStop.IsCancellationRequested) { }
                catch (Exception error) { logger.LogError(error, "DHMP API receive loop failed during shutdown"); }
            }
            if (exchange is not null) await exchange.DisposeAsync().ConfigureAwait(false);
            if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
