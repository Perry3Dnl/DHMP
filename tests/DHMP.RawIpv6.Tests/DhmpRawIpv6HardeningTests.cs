using System.Net;
using DHMP.Protocol;
using DHMP.Server;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpRawIpv6HardeningTests
{
    [Fact]
    public void HappyFlow_PeerBinding_ReportsPlainAndProtectedNetworkCeilings()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(4),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Sequential,
                    64));

        var plain =
            new DhmpRawIpv6PeerBinding(
                IPAddress.Parse("2001:db8::1"),
                server,
                _ => { },
                allowUnprotectedPayloads: true);

        var protectedBinding =
            new DhmpRawIpv6PeerBinding(
                IPAddress.Parse("2001:db8::2"),
                server,
                _ => { },
                new FixedDecoder(
                    overheadBytes: 24,
                    plaintextBytes: 4));

        Assert.Equal(
            64,
            plain.MaximumNetworkPayloadBytes);

        Assert.Equal(
            88,
            protectedBinding.MaximumNetworkPayloadBytes);
    }

    [Fact]
    public void PlainPeerBindingRequiresExplicitIntegrityAcceptance()
    {
        var server = new DhmpServer(
            new DhmpWireContract(4),
            new DhmpReceivePolicy(
                DhmpProcessingMode.Sequential,
                64));

        Assert.Throws<InvalidOperationException>(() =>
            new DhmpRawIpv6PeerBinding(
                IPAddress.Parse("2001:db8::1"),
                server,
                _ => { }));

        var explicitPlain = new DhmpRawIpv6PeerBinding(
            IPAddress.Parse("2001:db8::1"),
            server,
            _ => { },
            allowUnprotectedPayloads: true);

        Assert.True(explicitPlain.UnprotectedPayloadsAllowed);
    }

    [Fact]
    public void CriticalFlow_PeerBinding_RejectsIpv4MappedAndImpossibleDecoderOverhead()
    {
        var server =
            new DhmpServer(
                new DhmpWireContract(4),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Sequential,
                    64));

        Assert.Throws<ArgumentException>(() =>
            new DhmpRawIpv6PeerBinding(
                IPAddress.Loopback,
                server,
                _ => { }));

        Assert.Throws<ArgumentException>(() =>
            new DhmpRawIpv6PeerBinding(
                IPAddress.Parse(
                    "::ffff:127.0.0.1"),
                server,
                _ => { }));

        Assert.Throws<ArgumentException>(() =>
            new DhmpRawIpv6PeerBinding(
                IPAddress.IPv6Loopback,
                server,
                _ => { },
                new FixedDecoder(
                    overheadBytes: -1,
                    plaintextBytes: 4)));

        Assert.Throws<ArgumentException>(() =>
            new DhmpRawIpv6PeerBinding(
                IPAddress.IPv6Loopback,
                server,
                _ => { },
                new FixedDecoder(
                    overheadBytes: ushort.MaxValue,
                    plaintextBytes: 4)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    [InlineData(4096)]
    public void CriticalFlow_Router_RejectsInvalidSuccessfulDecoderLengths(
        int plaintextBytes)
    {
        var peer =
            IPAddress.Parse("2001:db8::10");

        var router =
            new DhmpRawIpv6PeerRouter(
                maximumPeers: 2,
                maximumNetworkPayloadBytes: 128);

        int callbacks = 0;

        router.Register(
            new DhmpRawIpv6PeerBinding(
                peer,
                new DhmpServer(
                    new DhmpWireContract(4),
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        64)),
                _ => callbacks++,
                new FixedDecoder(
                    overheadBytes: 1,
                    plaintextBytes)));

        byte[] scratch =
            Enumerable.Repeat(
                    (byte)0xcc,
                    64)
                .ToArray();

        Assert.False(
            router.TryRoute(
                peer,
                new byte[] { 0xa5, 1, 2, 3, 4 },
                scratch));

        Assert.Equal(0, callbacks);
        Assert.Equal(
            1,
            router.ProtectionRejectedPackets);

        Assert.All(
            scratch,
            value => Assert.Equal(
                (byte)0,
                value));
    }

    [Fact]
    public void CriticalFlow_Router_ClearsScratchWhenDecoderWritesThenRejects()
    {
        var peer =
            IPAddress.Parse("2001:db8::13");

        var router =
            new DhmpRawIpv6PeerRouter(
                maximumPeers: 1,
                maximumNetworkPayloadBytes: 128);

        router.Register(
            new DhmpRawIpv6PeerBinding(
                peer,
                new DhmpServer(
                    new DhmpWireContract(4),
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        64)),
                _ => throw new InvalidOperationException(
                    "must not publish"),
                new FixedDecoder(
                    overheadBytes: 1,
                    plaintextBytes: 4,
                    succeeds: false)));

        byte[] scratch =
            Enumerable.Repeat(
                    (byte)0xcc,
                    64)
                .ToArray();

        Assert.False(
            router.TryRoute(
                peer,
                new byte[] { 1, 2, 3, 4, 5 },
                scratch));

        Assert.Equal(
            1,
            router.ProtectionRejectedPackets);

        Assert.All(
            scratch,
            value => Assert.Equal(
                (byte)0,
                value));
    }

    [Fact]
    public void HappyFlow_Router_AcceptsExactlyOneNegotiatedSlotAndDropsWrongSize()
    {
        var peer =
            IPAddress.Parse("2001:db8::44");

        var router =
            new DhmpRawIpv6PeerRouter(
                maximumPeers: 1,
                maximumNetworkPayloadBytes: 128);

        byte[]? published = null;

        router.Register(
            new DhmpRawIpv6PeerBinding(
                peer,
                new DhmpServer(
                    new DhmpWireContract(4),
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Latest,
                        64)),
                span => published = span.ToArray(),
                allowUnprotectedPayloads: true));

        Assert.True(
            router.TryRoute(
                peer,
                new byte[] { 1, 2, 3, 4 },
                Span<byte>.Empty));

        Assert.Equal(
            new byte[] { 1, 2, 3, 4 },
            published);

        Assert.False(
            router.TryRoute(
                peer,
                new byte[] { 5, 6, 7 },
                Span<byte>.Empty));

        Assert.False(
            router.TryRoute(
                peer,
                new byte[] { 5, 6, 7, 8, 9 },
                Span<byte>.Empty));
    }

    [Fact]
    public void CriticalFlow_Router_RejectsDecodedPartialRecordAndClearsPlaintext()
    {
        var peer =
            IPAddress.Parse("2001:db8::11");

        var router =
            new DhmpRawIpv6PeerRouter(
                maximumPeers: 2,
                maximumNetworkPayloadBytes: 128);

        router.Register(
            new DhmpRawIpv6PeerBinding(
                peer,
                new DhmpServer(
                    new DhmpWireContract(4),
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        64)),
                _ => throw new InvalidOperationException(
                    "must not publish"),
                new FixedDecoder(
                    overheadBytes: 1,
                    plaintextBytes: 3)));

        byte[] scratch =
            Enumerable.Repeat(
                    (byte)0xcc,
                    64)
                .ToArray();

        Assert.False(
            router.TryRoute(
                peer,
                new byte[] { 1, 2, 3, 4 },
                scratch));

        Assert.Equal(1, router.RejectedPackets);
        Assert.Equal(0, router.AcceptedPackets);

        Assert.Equal(
            new byte[] { 0, 0, 0 },
            scratch[..3].ToArray());
    }

    [Fact]
    public void CriticalFlow_Router_CallbackFailureStillClearsDecodedPlaintext()
    {
        var peer =
            IPAddress.Parse("2001:db8::12");

        var router =
            new DhmpRawIpv6PeerRouter(
                maximumPeers: 2,
                maximumNetworkPayloadBytes: 128);

        router.Register(
            new DhmpRawIpv6PeerBinding(
                peer,
                new DhmpServer(
                    new DhmpWireContract(4),
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        64)),
                _ => throw new InvalidOperationException(
                    "application failed"),
                new FixedDecoder(
                    overheadBytes: 1,
                    plaintextBytes: 4)));

        byte[] scratch =
            Enumerable.Repeat(
                    (byte)0xcc,
                    64)
                .ToArray();

        var error =
            Assert.Throws<InvalidOperationException>(() =>
                router.TryRoute(
                    peer,
                    new byte[] { 1, 2, 3, 4, 5 },
                    scratch));

        Assert.Equal(
            "application failed",
            error.Message);

        Assert.Equal(0, router.AcceptedPackets);

        Assert.Equal(
            new byte[] { 0, 0, 0, 0 },
            scratch[..4].ToArray());
    }

    [Fact]
    public void CriticalFlow_NonLinuxConcreteRawBackendsFailFastBeforeSocketUse()
    {
        if (OperatingSystem.IsLinux())
            return;

        var options =
            new DhmpRawIpv6Options(
                IPAddress.IPv6Loopback,
                IPAddress.Parse("2001:db8::1"),
                128);

        var listenerOptions =
            new DhmpRawIpv6ListenerOptions(
                IPAddress.IPv6Loopback,
                128);

        var server =
            new DhmpServer(
                new DhmpWireContract(4),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Sequential,
                    64));

        Assert.Throws<PlatformNotSupportedException>(() =>
            new DhmpRawIpv6PacketSender(
                options));

        Assert.Throws<PlatformNotSupportedException>(() =>
            new DhmpRawIpv6Receiver(
                options,
                server));

        Assert.Throws<PlatformNotSupportedException>(() =>
            new DhmpRawIpv6MultiPeerReceiver(
                listenerOptions));

        Assert.Throws<PlatformNotSupportedException>(() =>
            new DhmpRawIpv6ControlChannel(
                options));
    }

    [Fact]
    public void CriticalFlow_Router_RemoveUnknownPeerIsIdempotent()
    {
        var router =
            new DhmpRawIpv6PeerRouter(
                maximumPeers: 2,
                maximumNetworkPayloadBytes: 128);

        var peer =
            IPAddress.Parse("2001:db8::20");

        Assert.False(
            router.Remove(peer));

        router.Register(
            Binding(peer));

        Assert.True(
            router.Remove(peer));

        Assert.False(
            router.Remove(peer));
    }

    [Fact]
    public void CriticalFlow_Router_ConcurrentRegistrationNeverExceedsPeerLimit()
    {
        const int limit = 16;

        var router =
            new DhmpRawIpv6PeerRouter(
                maximumPeers: limit,
                maximumNetworkPayloadBytes: 128);

        int accepted = 0;
        int rejected = 0;

        Parallel.For(
            1,
            101,
            i =>
            {
                try
                {
                    router.Register(
                        Binding(
                            IPAddress.Parse(
                                $"2001:db8::{i:x}")));

                    Interlocked.Increment(
                        ref accepted);
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(
                        ref rejected);
                }
            });

        Assert.Equal(limit, accepted);
        Assert.Equal(100 - limit, rejected);
        Assert.Equal(limit, router.PeerCount);
    }

    [Fact]
    public void CriticalFlow_Router_EmptyUnprotectedPacketIsRejected()
    {
        var peer =
            IPAddress.Parse("2001:db8::30");

        var router =
            new DhmpRawIpv6PeerRouter(
                maximumPeers: 1,
                maximumNetworkPayloadBytes: 128);

        router.Register(
            Binding(peer));

        Assert.False(
            router.TryRoute(
                peer,
                ReadOnlySpan<byte>.Empty,
                Span<byte>.Empty));

        Assert.Equal(1, router.RejectedPackets);
    }

    private static DhmpRawIpv6PeerBinding Binding(
        IPAddress peer)
        => new(
            peer,
            new DhmpServer(
                new DhmpWireContract(4),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.Sequential,
                    64)),
            _ => { },
            allowUnprotectedPayloads: true);

    private sealed class FixedDecoder :
        IDhmpPacketDecoder
    {
        private readonly int _plaintextBytes;
        private readonly bool _succeeds;

        public FixedDecoder(
            int overheadBytes,
            int plaintextBytes,
            bool succeeds = true)
        {
            OverheadBytes = overheadBytes;
            _plaintextBytes = plaintextBytes;
            _succeeds = succeeds;
        }

        public int OverheadBytes { get; }

        public bool TryDecode(
            ReadOnlySpan<byte> packet,
            Span<byte> plaintextDestination,
            out int plaintextBytes)
        {
            int writable =
                Math.Min(
                    Math.Max(_plaintextBytes, 0),
                    plaintextDestination.Length);

            if (writable > 0)
            {
                plaintextDestination[
                    ..writable]
                    .Fill(0x5a);
            }

            plaintextBytes =
                _plaintextBytes;

            return _succeeds;
        }
    }
}
