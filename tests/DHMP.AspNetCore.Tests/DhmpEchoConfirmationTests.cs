using System.Buffers.Binary;
using System.Threading.Channels;
using DHMP.Client;
using DHMP.Protocol;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpEchoConfirmationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Sender : IDhmpPacketSender
    {
        internal readonly Channel<byte[]> Packets = Channel.CreateBounded<byte[]>(16);
        internal Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? OnSend;
        public int MaximumPayloadBytes => 256;
        public ValueTask SendPacketAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => OnSend is { } send ? send(payload, cancellationToken) : Packets.Writer.WriteAsync(payload.ToArray(), cancellationToken);
    }
    private static DhmpClient Client(Sender sender) => new(sender, new DhmpWireContract(256), new DhmpSendPolicy(1000, 256));
    private static DhmpEchoConfirmation Profile(Sender sender, Guid session, int maximum = 4, TimeSpan? timeout = null)
        => new(Client(sender), session, new() { MaximumInFlight = maximum, ConfirmationTimeout = timeout ?? TimeSpan.FromSeconds(5) });

    [Fact]
    public async Task FullEchoConfirmsExactOwnedBytesWithoutEchoLoop()
    {
        Guid session = Guid.NewGuid();
        var a = new Sender(); var b = new Sender();
        await using var first = Profile(a, session);
        await using var second = Profile(b, session);
        byte[] content = Enumerable.Range(0, 216).Select(i => (byte)i).ToArray();
        byte[] expected = content.ToArray();
        Task<DhmpEchoReceipt> pending = first.SendAsync(content, Token);
        byte[] sent = await a.Packets.Reader.ReadAsync(Token);
        content.AsSpan().Clear(); // Caller mutation cannot change retained confirmation bytes.
        var received = await second.ReceiveAsync(sent, Token);
        Assert.Equal(expected, received!.Value.ToArray());
        byte[] echo = await b.Packets.Reader.ReadAsync(Token);
        Assert.Equal(sent.AsSpan(8).ToArray(), echo.AsSpan(8).ToArray());
        Assert.Null(await first.ReceiveAsync(echo, Token));
        Assert.True((await pending).RoundTripTime >= TimeSpan.Zero);
        Assert.Null(await first.ReceiveAsync(echo, Token)); // Late/duplicate echo is harmless.
        Assert.False(a.Packets.Reader.TryRead(out _));
        Assert.False(b.Packets.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(4)] // Version.
    [InlineData(6)] // Reserved byte.
    [InlineData(8)] // Session.
    [InlineData(24)] // Request identity.
    [InlineData(36)] // Reserved field.
    [InlineData(40)] // Wrong echoed application bytes.
    [InlineData(255)] // Nonzero padding.
    public async Task InvalidOrMismatchedEchoCannotConfirmPendingSend(int offset)
    {
        var sender = new Sender();
        await using var profile = Profile(sender, Guid.NewGuid());
        Task<DhmpEchoReceipt> pending = profile.SendAsync(new byte[] { 42 }, Token);
        byte[] record = await sender.Packets.Reader.ReadAsync(Token);
        record[5] = 2;
        byte[] invalid = record.ToArray(); invalid[offset] ^= 1;
        Assert.Null(await profile.ReceiveAsync(invalid, Token));
        Assert.False(pending.IsCompleted);
        await profile.ReceiveAsync(record, Token);
        await pending;
    }

    [Fact]
    public async Task LostEchoTimesOutWithoutRetransmissionAndReleasesAdmission()
    {
        var sender = new Sender();
        await using var profile = Profile(sender, Guid.NewGuid(), 1, TimeSpan.FromSeconds(1));
        Task<DhmpEchoReceipt> pending = profile.SendAsync(new byte[] { 1 }, Token);
        await sender.Packets.Reader.ReadAsync(Token);
        await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.False(sender.Packets.Reader.TryRead(out _));
        Task<DhmpEchoReceipt> next = profile.SendAsync(new byte[] { 2 }, Token);
        byte[] record = await sender.Packets.Reader.ReadAsync(Token); record[5] = 2;
        await profile.ReceiveAsync(record, Token); await next;
    }

    [Fact]
    public async Task SaturatedAndCancelledSendsDoNotCreateExtraPackets()
    {
        var sender = new Sender();
        await using var profile = Profile(sender, Guid.NewGuid(), 1);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<DhmpEchoReceipt> pending = profile.SendAsync(new byte[] { 1 }, cancel.Token);
        await sender.Packets.Reader.ReadAsync(Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => profile.SendAsync(new byte[] { 2 }, Token));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(sender.Packets.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DisposalJoinsBackendStorageEvenWhenBackendIgnoresCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new Sender { OnSend = async (_, _) => { entered.TrySetResult(); await release.Task.WaitAsync(Token); } };
        var profile = Profile(sender, Guid.NewGuid());
        Task<DhmpEchoReceipt> pending = profile.SendAsync(new byte[] { 1 }, Token);
        await entered.Task.WaitAsync(Token);
        Task retirement = profile.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await retirement;
        await profile.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => profile.SendAsync(new byte[] { 1 }, Token));
    }

    [Fact]
    public async Task ReceiveAdmissionIsBoundedAndDisposalJoinsActiveEcho()
    {
        Guid session = Guid.NewGuid(); var a = new Sender(); var b = new Sender();
        await using var first = Profile(a, session);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<DhmpEchoReceipt> pending = first.SendAsync(new byte[] { 1 }, cancel.Token);
        byte[] record = await a.Packets.Reader.ReadAsync(Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnSend = async (_, _) => { entered.TrySetResult(); await release.Task.WaitAsync(Token); };
        var second = new DhmpEchoConfirmation(Client(b), session, new() { MaximumConcurrentReceives = 1 });
        Task<ReadOnlyMemory<byte>?> active = second.ReceiveAsync(record, Token).AsTask();
        await entered.Task.WaitAsync(Token);
        Assert.Null(await second.ReceiveAsync(record, Token));
        Task retirement = second.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active); await retirement;
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task MalformedRequestsAreNeverEchoedAndPlainClientRemainsUnchanged()
    {
        var sender = new Sender(); Guid session = Guid.NewGuid();
        await using var profile = Profile(sender, session);
        Assert.Null(await profile.ReceiveAsync(new byte[255], Token));
        byte[] record = new byte[256];
        "DECO"u8.CopyTo(record); record[4] = 1; record[5] = 1;
        session.TryWriteBytes(record.AsSpan(8, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(24, 8), 1);
        BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(32, 4), 217);
        Assert.Null(await profile.ReceiveAsync(record, Token));
        Assert.False(sender.Packets.Reader.TryRead(out _));
        byte[] plain = Enumerable.Repeat((byte)7, 256).ToArray();
        await Client(sender).SendAsync(plain, Token);
        Assert.Equal(plain, await sender.Packets.Reader.ReadAsync(Token));
    }

    [Fact]
    public async Task BackendFailureSurfacesAndFreesConfirmationSlot()
    {
        var sender = new Sender { OnSend = (_, _) => ValueTask.FromException(new IOException("backend failed")) };
        await using var profile = Profile(sender, Guid.NewGuid(), 1);
        await Assert.ThrowsAsync<IOException>(() => profile.SendAsync(new byte[] { 1 }, Token));
        sender.OnSend = null;
        Task<DhmpEchoReceipt> pending = profile.SendAsync(new byte[] { 2 }, Token);
        byte[] record = await sender.Packets.Reader.ReadAsync(Token); record[5] = 2;
        await profile.ReceiveAsync(record, Token); await pending;
    }

    [Fact]
    public void InvalidOptionsAndEmptySessionAreRejected()
    {
        var client = Client(new Sender());
        Assert.Throws<ArgumentException>(() => new DhmpEchoConfirmation(client, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DhmpEchoConfirmation(client, Guid.NewGuid(), new() { MaximumInFlight = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DhmpEchoConfirmation(client, Guid.NewGuid(), new() { ConfirmationTimeout = TimeSpan.Zero }));
    }
}
