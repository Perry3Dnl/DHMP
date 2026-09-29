using System.Net;

namespace DHMP.Client;

public sealed class DhmpClient : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpClient _client = new();
    public bool IsConnected => _client.Connected;

    public Task ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default) =>
        _client.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken).AsTask();

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (!_client.Connected) throw new InvalidOperationException("DHMP client is not connected.");
        return _client.GetStream().WriteAsync(payload, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
