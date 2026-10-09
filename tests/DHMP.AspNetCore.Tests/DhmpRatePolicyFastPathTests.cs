using System.Diagnostics;
using DHMP.Client;
using DHMP.Protocol;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpRatePolicyFastPathTests
{
    private static DhmpClient Create(
        Sender sender,
        DhmpRatePolicy ratePolicy,
        long pmax = long.MaxValue) =>
        new(
            sender,
            new DhmpWireContract(16),
            new DhmpSendPolicy(
                pmax,
                64,
                ratePolicy));

    [Theory]
    [InlineData(DhmpRatePolicy.RejectWindow)]
    [InlineData(DhmpRatePolicy.SmoothPacing)]
    public async Task CompletedSend_UsesCompletedFastPath_WithoutAllocation(
        DhmpRatePolicy ratePolicy)
    {
        var sender =
            new Sender
            {
                AdvanceClockOnSend =
                    ratePolicy ==
                    DhmpRatePolicy.SmoothPacing
            };

        var client =
            Create(
                sender,
                ratePolicy);

        byte[] record =
            new byte[16];

        ValueTask first =
            client.SendAsync(
                record,
                TestContext.Current.CancellationToken);

        Assert.True(
            first.IsCompletedSuccessfully);

        await first;

        for (int i = 0; i < 20_000; i++)
        {
            await client.SendAsync(
                record,
                TestContext.Current.CancellationToken);
        }

        long before =
            GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 20_000; i++)
        {
            await client.SendAsync(
                record,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            0,
            GC.GetAllocatedBytesForCurrentThread() -
                before);

        Assert.Equal(
            40_001,
            sender.Calls);
    }

    [Theory]
    [InlineData(DhmpRatePolicy.RejectWindow)]
    [InlineData(DhmpRatePolicy.SmoothPacing)]
    public async Task InvalidRecordAndLiveBudgetFailure_AreDeferred(
        DhmpRatePolicy ratePolicy)
    {
        var sender =
            new Sender
            {
                AdvanceClockOnSend =
                    ratePolicy ==
                    DhmpRatePolicy.SmoothPacing
            };

        var client =
            Create(
                sender,
                ratePolicy);

        ValueTask invalid =
            client.SendAsync(
                new byte[15],
                TestContext.Current.CancellationToken);

        Assert.True(
            invalid.IsFaulted);

        await Assert.ThrowsAsync<DhmpProtocolException>(
            () => invalid.AsTask());

        byte[] record =
            new byte[16];

        await client.SendAsync(
            record,
            TestContext.Current.CancellationToken);

        sender.CurrentMaximumPayloadBytes =
            15;

        ValueTask blocked =
            client.SendAsync(
                record,
                TestContext.Current.CancellationToken);

        Assert.True(
            blocked.IsFaulted);

        await Assert.ThrowsAsync<DhmpProtocolException>(
            () => blocked.AsTask());

        Assert.Equal(
            1,
            sender.Calls);

        sender.CurrentMaximumPayloadBytes =
            16;

        await client.SendAsync(
            record,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            2,
            sender.Calls);
    }

    [Fact]
    public async Task RejectWindow_ExhaustionDoesNotCallBackend()
    {
        var sender =
            new Sender();

        var client =
            Create(
                sender,
                DhmpRatePolicy.RejectWindow,
                pmax: 1);

        byte[] record =
            new byte[16];

        await client.SendAsync(
            record,
            TestContext.Current.CancellationToken);

        ValueTask rejected =
            client.SendAsync(
                record,
                TestContext.Current.CancellationToken);

        Assert.True(
            rejected.IsFaulted);

        await Assert.ThrowsAsync<DhmpProtocolException>(
            () => rejected.AsTask());

        Assert.Equal(
            1,
            sender.Calls);
    }

    [Fact]
    public async Task SmoothPacing_OnlyBecomesAsyncWhenDelayIsRequired()
    {
        var sender =
            new Sender();

        var client =
            Create(
                sender,
                DhmpRatePolicy.SmoothPacing,
                pmax: 1);

        byte[] record =
            new byte[16];

        ValueTask first =
            client.SendAsync(
                record,
                TestContext.Current.CancellationToken);

        Assert.True(
            first.IsCompletedSuccessfully);

        await first;

        using var cancellation =
            new CancellationTokenSource(
                TimeSpan.FromMilliseconds(20));

        ValueTask delayed =
            client.SendAsync(
                record,
                cancellation.Token);

        Assert.False(
            delayed.IsCompletedSuccessfully);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => delayed.AsTask());

        Assert.Equal(
            1,
            sender.Calls);
    }

    [Theory]
    [InlineData(DhmpRatePolicy.RejectWindow)]
    [InlineData(DhmpRatePolicy.SmoothPacing)]
    public async Task CompletedBackend_DoesNotLeakAmbientContext(
        DhmpRatePolicy ratePolicy)
    {
        var ambient =
            new AsyncLocal<string?>
            {
                Value = "caller"
            };

        SynchronizationContext? originalContext =
            SynchronizationContext.Current;

        var sender =
            new Sender
            {
                AdvanceClockOnSend =
                    ratePolicy ==
                    DhmpRatePolicy.SmoothPacing,
                Send =
                    () =>
                    {
                        ambient.Value =
                            "backend";

                        SynchronizationContext
                            .SetSynchronizationContext(
                                new SynchronizationContext());

                        return ValueTask.CompletedTask;
                    }
            };

        ValueTask send =
            Create(
                sender,
                ratePolicy)
            .SendAsync(
                new byte[16],
                TestContext.Current.CancellationToken);

        Assert.Equal(
            "caller",
            ambient.Value);

        Assert.Same(
            originalContext,
            SynchronizationContext.Current);

        await send;
    }

    private sealed class Sender :
        IDhmpDynamicPacketSender
    {
        public int MaximumPayloadBytes =>
            64;

        public int CurrentMaximumPayloadBytes
        {
            get;
            set;
        } = 64;

        public int Calls
        {
            get;
            private set;
        }

        public bool AdvanceClockOnSend
        {
            get;
            init;
        }

        public Func<ValueTask> Send
        {
            get;
            init;
        } = static () =>
            ValueTask.CompletedTask;

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            Calls++;

            ValueTask pending =
                Send();

            if (AdvanceClockOnSend &&
                pending.IsCompletedSuccessfully)
            {
                pending.GetAwaiter()
                    .GetResult();

                long timestamp =
                    Stopwatch.GetTimestamp();

                while (Stopwatch.GetTimestamp() ==
                       timestamp)
                {
                    Thread.SpinWait(1);
                }

                return ValueTask.CompletedTask;
            }

            return pending;
        }
    }
}
