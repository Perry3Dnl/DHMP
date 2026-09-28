namespace Dhmp.Client;

public interface IDHMPClient : IAsyncDisposable
{
    bool IsConnected { get; }
    ValueTask ConnectAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> TrySendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);
}
