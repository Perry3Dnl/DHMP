using System.Net;
using System.Net.Sockets;

namespace DHMP.Server;

public sealed class DhmpServer : IAsyncDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;

    public bool IsRunning => _listener is not null;
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public event Func<ReadOnlyMemory<byte>, ValueTask>? MessageReceived;

    public Task StartAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        if (_listener is not null) throw new InvalidOperationException("DHMP server is already running.");
        cancellationToken.ThrowIfCancellationRequested();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(endpoint);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var buffer = new byte[64 * 1024];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0) return;
                var handler = MessageReceived;
                if (handler is not null) await handler(buffer.AsMemory(0, read));
            }
        }
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        if (listener is null) return;
        _listener = null;
        _lifetime?.Cancel();
        listener.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch (OperationCanceledException) { }
        }
        _lifetime?.Dispose();
        _lifetime = null;
        _acceptLoop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
