using System.Threading.Channels;
using DHMP.Client;
using DHMP.Protocol;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpApplicationConfirmationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Sender : IDhmpPacketSender
    {
        internal readonly Channel<byte[]> Packets =
            Channel.CreateBounded<byte[]>(16);

        internal Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? OnSend;

        public int MaximumPayloadBytes => 256;

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
            => OnSend is { } send
                ? send(payload, cancellationToken)
                : Packets.Writer.WriteAsync(
                    payload.ToArray(),
                    cancellationToken);
    }

    private static DhmpClient Client(Sender sender)
        => new(
            sender,
            new DhmpWireContract(256),
            new DhmpSendPolicy(1000, 256));

    private static DhmpApplicationConfirmationTracker Tracker(
        Sender sender,
        int maximum = 4,
        TimeSpan? timeout = null)
        => new(
            Client(sender),
            new DhmpApplicationConfirmationOptions
            {
                MaximumInFlight = maximum,
                ConfirmationTimeout = timeout ?? TimeSpan.FromSeconds(5)
            });

    [Fact]
    public async Task TrackedSendPreservesApplicationRecordByteForByte()
    {
        var sender = new Sender();
        await using var tracker = Tracker(sender);

        byte[] record = Enumerable.Range(0, 256)
            .Select(i => (byte)i)
            .ToArray();

        Task<DhmpApplicationConfirmationReceipt> pending =
            tracker.SendTrackedAsync(42, record, Token);

        byte[] sent = await sender.Packets.Reader.ReadAsync(Token);
        Assert.Equal(record, sent);

        Assert.True(tracker.TryConfirm(42));

        DhmpApplicationConfirmationReceipt receipt = await pending;
        Assert.Equal((ulong)42, receipt.ApplicationId);
        Assert.True(receipt.RoundTripTime >= TimeSpan.Zero);
        Assert.False(tracker.TryConfirm(42));
        Assert.False(sender.Packets.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ConfirmationIdentifierIsApplicationOwnedAndUniqueOnlyWhilePending()
    {
        var sender = new Sender();
        await using var tracker = Tracker(sender);

        byte[] record = new byte[256];
        Task<DhmpApplicationConfirmationReceipt> first =
            tracker.SendTrackedAsync(7, record, Token);

        await sender.Packets.Reader.ReadAsync(Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracker.SendTrackedAsync(7, record, Token));

        Assert.False(tracker.TryConfirm(999));
        Assert.True(tracker.TryConfirm(7));
        await first;

        Task<DhmpApplicationConfirmationReceipt> reused =
            tracker.SendTrackedAsync(7, record, Token);

        await sender.Packets.Reader.ReadAsync(Token);
        Assert.True(tracker.TryConfirm(7));
        await reused;
    }

    [Fact]
    public async Task LostConfirmationTimesOutWithoutRetransmission()
    {
        var sender = new Sender();
        await using var tracker =
            Tracker(sender, 1, TimeSpan.FromSeconds(1));

        Task<DhmpApplicationConfirmationReceipt> pending =
            tracker.SendTrackedAsync(1, new byte[256], Token);

        await sender.Packets.Reader.ReadAsync(Token);

        await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.False(sender.Packets.Reader.TryRead(out _));

        Task<DhmpApplicationConfirmationReceipt> next =
            tracker.SendTrackedAsync(2, new byte[256], Token);

        await sender.Packets.Reader.ReadAsync(Token);
        Assert.True(tracker.TryConfirm(2));
        await next;
    }

    [Fact]
    public async Task UntrackedSendAlsoPreservesBytesAndCreatesNoPendingConfirmation()
    {
        var sender = new Sender();
        await using var tracker = Tracker(sender);

        byte[] record = Enumerable.Repeat((byte)0xA5, 256).ToArray();
        await tracker.SendUntrackedAsync(record, Token);

        Assert.Equal(record, await sender.Packets.Reader.ReadAsync(Token));
        Assert.False(tracker.TryConfirm(123));
    }

    [Fact]
    public async Task AdmissionAndCancellationDoNotCreateExtraPackets()
    {
        var sender = new Sender();
        await using var tracker = Tracker(sender, 1);

        using var cancel =
            CancellationTokenSource.CreateLinkedTokenSource(Token);

        Task<DhmpApplicationConfirmationReceipt> pending =
            tracker.SendTrackedAsync(1, new byte[256], cancel.Token);

        await sender.Packets.Reader.ReadAsync(Token);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracker.SendTrackedAsync(2, new byte[256], Token));

        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending);

        Assert.False(sender.Packets.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DisposalJoinsBackendStorageEvenWhenBackendIgnoresCancellation()
    {
        var entered =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        var release =
            new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

        var sender = new Sender
        {
            OnSend = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(Token);
            }
        };

        var tracker = Tracker(sender);

        Task<DhmpApplicationConfirmationReceipt> pending =
            tracker.SendTrackedAsync(1, new byte[256], Token);

        await entered.Task.WaitAsync(Token);

        Task retirement = tracker.DisposeAsync().AsTask();
        Assert.False(retirement.IsCompleted);

        release.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending);
        await retirement;

        await tracker.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => tracker.SendTrackedAsync(2, new byte[256], Token));
    }

    [Fact]
    public async Task InvalidIdentifierRecordAndOptionsAreRejected()
    {
        var sender = new Sender();
        var client = Client(sender);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DhmpApplicationConfirmationTracker(
                client,
                new() { MaximumInFlight = 0 }));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DhmpApplicationConfirmationTracker(
                client,
                new() { ConfirmationTimeout = TimeSpan.Zero }));

        await using var tracker = new DhmpApplicationConfirmationTracker(client);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => tracker.SendTrackedAsync(0, new byte[256], Token));

        await Assert.ThrowsAsync<DhmpProtocolException>(
            () => tracker.SendTrackedAsync(1, new byte[255], Token));
    }
}
