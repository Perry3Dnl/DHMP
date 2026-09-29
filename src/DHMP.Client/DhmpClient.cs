using System.Net;
using DHMP.Protocol;

namespace DHMP.Client;

public sealed class DhmpClient : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpClient _client = new();
    private DhmpFixedContract? _contract;
    private DhmpPmaxBudget? _budget;

    public bool IsConnected => _client.Connected;

    public Task ConnectAsync(IPEndPoint endpoint, DhmpFixedContract contract, CancellationToken cancellationToken = default)
    {
        _contract = contract;
        _budget = new DhmpPmaxBudget(contract.Pmax);
        return _client.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken).AsTask();
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        if (!_client.Connected) throw new InvalidOperationException("DHMP client is not connected.");
        var contract = _contract ?? throw new InvalidOperationException("DHMP fixed contract is not configured.");
        contract.ValidatePayload(payload.Length);
        if (!_budget!.TryConsume()) throw new DhmpProtocolException($"Configured Pmax of {contract.Pmax} messages/second exceeded.");
        return _client.GetStream().WriteAsync(payload, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
