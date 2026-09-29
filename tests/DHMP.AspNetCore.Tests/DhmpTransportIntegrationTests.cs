using System.Net;
using DHMP.Client;
using DHMP.Server;
using DHMP.Protocol;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpTransportIntegrationTests
{
    [Fact]
    public async Task HappyPath_ClientCanSendBytesToServer()
    {
        await using var server = new DhmpServer();
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += message =>
        {
            received.TrySetResult(message.ToArray());
            return ValueTask.CompletedTask;
        };

        var contract = new DhmpFixedContract(4, 100);
        await server.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), contract, TestContext.Current.CancellationToken);
        await using var client = new DhmpClient();
        await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, server.Port), contract, TestContext.Current.CancellationToken);

        byte[] payload = [0x44, 0x48, 0x4d, 0x50];
        await client.SendAsync(payload, TestContext.Current.CancellationToken);

        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(payload, actual);
    }
}
