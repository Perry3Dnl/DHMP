using System.Net;
using DHMP.Protocol;
using DHMP.RawIpv6;
using DHMP.Security;
using DHMP.Server;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpPeerLifecycleTests
{
    private static readonly IPAddress Peer = IPAddress.Parse("2001:db8::42");
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RemoveAsync_WaitsForDecodeAndReturnsCallerOwnedBinding()
    {
        var router = new DhmpRawIpv6PeerRouter(1, 128);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var decoder = new TestDecoder(entered, release);
        var binding = Binding(_ => { }, decoder);
        router.Register(binding);
        byte[] scratch = new byte[64];
        Task<bool> route = Task.Run(() => router.TryRoute(Peer, new byte[4], scratch), TestContext.Current.CancellationToken);
        Task<DhmpRawIpv6PeerBinding?>? removed = null;
        try
        {
            await entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
            removed = router.RemoveAsync(Peer);
            Assert.Equal(0, router.PeerCount);
            Assert.False(removed.IsCompleted);
            Assert.False(decoder.Disposed);
            Assert.False(router.TryRoute(Peer, new byte[4], new byte[64]));
        }
        finally
        {
            release.Set();
            await route.WaitAsync(Guard, TestContext.Current.CancellationToken);
        }
        Assert.NotNull(removed);
        Assert.Same(binding, await removed.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.False(decoder.Disposed); // Router does not own decoder disposal.
        decoder.Dispose();
        Assert.All(scratch, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task ReplaceAsync_ActivatesNewBindingWhileOldCallbackDrains()
    {
        var router = new DhmpRawIpv6PeerRouter(1, 128);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int oldCalls = 0;
        int newCalls = 0;
        var old = Binding(_ =>
        {
            Interlocked.Increment(ref oldCalls);
            entered.SetResult();
            release.Wait(TestContext.Current.CancellationToken);
        });
        var replacement = Binding(_ => Interlocked.Increment(ref newCalls));
        router.Register(old);
        Task<bool> route = Task.Run(() => router.TryRoute(Peer, new byte[4], new byte[64]), TestContext.Current.CancellationToken);
        Task<DhmpRawIpv6PeerBinding>? retired = null;
        try
        {
            await entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
            retired = router.ReplaceAsync(replacement);
            Assert.False(retired.IsCompleted);
            Assert.Equal(1, router.PeerCount);
            Assert.True(router.TryRoute(Peer, new byte[4], new byte[64]));
            Assert.Equal(1, newCalls);
        }
        finally
        {
            release.Set();
            await route.WaitAsync(Guard, TestContext.Current.CancellationToken);
        }
        Assert.NotNull(retired);
        Assert.Same(old, await retired.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.True(router.TryRoute(Peer, new byte[4], new byte[64]));
        Assert.Equal(1, oldCalls);
        Assert.Equal(2, newCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetirementStartedFromCallback_CompletesAfterCallbackReturns(bool replace)
    {
        var router = new DhmpRawIpv6PeerRouter(1, 128);
        Task? retired = null;
        var old = Binding(_ =>
        {
            retired = replace ? router.ReplaceAsync(Binding(_ => { })) : router.RemoveAsync(Peer);
            Assert.False(retired.IsCompleted);
        });
        router.Register(old);
        Assert.True(router.TryRoute(Peer, new byte[4], new byte[64]));
        Assert.NotNull(retired);
        await retired.WaitAsync(Guard, TestContext.Current.CancellationToken);
        Assert.Equal(replace ? 1 : 0, router.PeerCount);
    }

    [Fact]
    public async Task UnknownRemoval_IsIdempotentAndUnknownReplacementDoesNotRegister()
    {
        var router = new DhmpRawIpv6PeerRouter(1, 128);
        Assert.Null(await router.RemoveAsync(Peer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => router.ReplaceAsync(Binding(_ => { })));
        Assert.Equal(0, router.PeerCount);
        var binding = Binding(_ => { });
        router.Register(binding);
        Assert.Same(binding, await router.RemoveAsync(Peer));
        Assert.Null(await router.RemoveAsync(Peer));
    }

    [Fact]
    public async Task InvalidReplacement_LeavesCurrentBindingUsable()
    {
        var router = new DhmpRawIpv6PeerRouter(1, 64);
        int calls = 0;
        var current = Binding(_ => calls++);
        router.Register(current);
        var oversized = new DhmpRawIpv6PeerBinding(Peer,
            new DhmpServer(new DhmpWireContract(4), new DhmpReceivePolicy(DhmpProcessingMode.Sequential, 128)), _ => { });
        await Assert.ThrowsAsync<ArgumentException>(() => router.ReplaceAsync(oversized));
        await Assert.ThrowsAsync<ArgumentException>(() => router.ReplaceAsync(current));
        Assert.True(router.TryRoute(Peer, new byte[4], new byte[64]));
        Assert.Equal(1, calls);
        Assert.Same(current, await router.RemoveAsync(Peer));
    }

    [Fact]
    public async Task DecoderException_ClearsScratchAndReleasesRetirementLease()
    {
        var router = new DhmpRawIpv6PeerRouter(1, 128);
        using var decoder = new TestDecoder { Fail = true };
        var binding = Binding(_ => throw new InvalidOperationException("must not publish"), decoder);
        router.Register(binding);
        byte[] scratch = Enumerable.Repeat((byte)0xcc, 64).ToArray();
        Assert.Throws<IOException>(() => router.TryRoute(Peer, new byte[4], scratch));
        Assert.All(scratch, value => Assert.Equal((byte)0, value));
        Assert.Same(binding, await router.RemoveAsync(Peer).WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal(0, router.AcceptedPackets);
    }

    [Fact]
    public async Task CallbackException_StillAllowsRetirementAndClearsEntireScratch()
    {
        var router = new DhmpRawIpv6PeerRouter(1, 128);
        using var decoder = new TestDecoder();
        var binding = Binding(_ => throw new IOException("application failure"), decoder);
        router.Register(binding);
        byte[] scratch = Enumerable.Repeat((byte)0xcc, 64).ToArray();
        Assert.Throws<IOException>(() => router.TryRoute(Peer, new byte[4], scratch));
        Assert.All(scratch, value => Assert.Equal((byte)0, value));
        Assert.Same(binding, await router.RemoveAsync(Peer).WaitAsync(Guard, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OversizedProtectedPacket_IsRejectedBeforeDecoderInvocation()
    {
        var router = new DhmpRawIpv6PeerRouter(1, 128);
        using var decoder = new TestDecoder();
        router.Register(Binding(_ => { }, decoder));
        Assert.False(router.TryRoute(Peer, new byte[65], new byte[64]));
        Assert.Equal(0, decoder.Calls);
        Assert.Equal(1, router.RejectedPackets);
    }

    [Fact]
    public async Task FreshProtectedReplacement_RejectsOldSessionPackets()
    {
        var router = new DhmpRawIpv6PeerRouter(1, 128);
        using var key = new DhmpPreSharedKey(7, new byte[32]);
        Guid oldId = Guid.NewGuid();
        Guid newId = Guid.NewGuid();
        using var oldSender = new DhmpPskChaCha20Poly1305Session(key, oldId, DhmpSecurityRole.Initiator);
        using var oldReceiver = new DhmpPskChaCha20Poly1305Session(key, oldId, DhmpSecurityRole.Responder);
        using var newSender = new DhmpPskChaCha20Poly1305Session(key, newId, DhmpSecurityRole.Initiator);
        using var newReceiver = new DhmpPskChaCha20Poly1305Session(key, newId, DhmpSecurityRole.Responder);
        int calls = 0;
        var old = Binding(_ => calls++, oldReceiver);
        router.Register(old);
        Assert.Same(old, await router.ReplaceAsync(Binding(_ => calls++, newReceiver)));
        oldReceiver.Dispose();
        byte[] packet = new byte[4 + DhmpPskChaCha20Poly1305Session.Overhead];
        oldSender.Protect(new byte[4], packet);
        Assert.False(router.TryRoute(Peer, packet, new byte[64]));
        Assert.Equal(0, calls);
        newSender.Protect(new byte[4], packet);
        Assert.True(router.TryRoute(Peer, packet, new byte[64]));
        Assert.Equal(1, calls);
        Assert.Equal(1, router.ProtectionRejectedPackets);
    }

    private static DhmpRawIpv6PeerBinding Binding(Action<ReadOnlySpan<byte>> publish, IDhmpPacketDecoder? decoder = null)
        => new(Peer, new DhmpServer(new DhmpWireContract(4),
            new DhmpReceivePolicy(DhmpProcessingMode.Sequential, 64)), publish, decoder);

    [Fact]
    public async Task ConcurrentProtectedRoutesAndReplacement_StayBoundedAndNeverUseDisposedDecoder()
    {
        var router = new DhmpRawIpv6PeerRouter(1, 128);
        router.Register(Binding(_ => { }, new TestDecoder()));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task routes = Task.Run(async () =>
        {
            await start.Task;
            for (int i = 0; i < 1000; i++)
                Assert.True(router.TryRoute(Peer, new byte[4], new byte[64]));
        }, TestContext.Current.CancellationToken);
        Task replacements = Task.Run(async () =>
        {
            await start.Task;
            for (int i = 0; i < 100; i++)
            {
                var retired = await router.ReplaceAsync(Binding(_ => { }, new TestDecoder()));
                ((TestDecoder)retired.Decoder!).Dispose();
                Assert.Equal(1, router.PeerCount);
            }
        }, TestContext.Current.CancellationToken);
        start.SetResult();
        try
        {
            await Task.WhenAll(routes, replacements).WaitAsync(Guard, TestContext.Current.CancellationToken);
            Assert.Equal(1000, router.AcceptedPackets);
            Assert.Equal(0, router.UnknownPeerPackets);
        }
        finally
        {
            var final = await router.RemoveAsync(Peer);
            (final?.Decoder as IDisposable)?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedReceiveBoundary_ClearsScratchOnDecoderFailure(bool throws)
    {
        using var decoder = new TestDecoder { Fail = throws, Reject = !throws };
        var binding = Binding(_ => throw new InvalidOperationException("must not publish"), decoder);
        byte[] scratch = Enumerable.Repeat((byte)0xcc, 64).ToArray();
        if (throws)
            Assert.Throws<IOException>(() => DhmpRawIpv6PayloadProcessor.TryProcess(binding.Server,
                decoder, new byte[4], scratch, binding.PublishBatch, out _));
        else
        {
            Assert.False(DhmpRawIpv6PayloadProcessor.TryProcess(binding.Server,
                decoder, new byte[4], scratch, binding.PublishBatch, out bool rejected));
            Assert.True(rejected);
        }
        Assert.All(scratch, value => Assert.Equal((byte)0, value));
    }

    private sealed class TestDecoder(TaskCompletionSource? entered = null, ManualResetEventSlim? release = null)
        : IDhmpPacketDecoder, IDisposable
    {
        public bool Fail { get; init; }
        public bool Reject { get; init; }
        public bool Disposed { get; private set; }
        public int Calls { get; private set; }
        public int OverheadBytes => 0;
        public bool TryDecode(ReadOnlySpan<byte> packet, Span<byte> destination, out int bytes)
        {
            Calls++;
            destination.Fill(0xa5);
            entered?.TrySetResult();
            release?.Wait(TestContext.Current.CancellationToken);
            Assert.False(Disposed);
            if (Fail) throw new IOException("injected decoder failure");
            bytes = 4;
            return !Reject;
        }
        public void Dispose() => Disposed = true;
    }
}
