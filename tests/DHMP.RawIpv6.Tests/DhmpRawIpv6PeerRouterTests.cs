using System.Net;
using DHMP.Protocol;
using DHMP.Server;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpRawIpv6PeerRouterTests
{
    [Fact]
    public void DifferentSourceAddresses_RouteToDifferentSessions()
    {
        var router =
            new DhmpRawIpv6PeerRouter(
                maximumPeers: 4,
                maximumNetworkPayloadBytes: 128);

        var peerA =
            IPAddress.Parse("2001:db8::10");
        var peerB =
            IPAddress.Parse("2001:db8::20");

        byte[]? receivedA = null;
        byte[]? receivedB = null;

        router.Register(
            new DhmpRawIpv6PeerBinding(
                peerA,
                new DhmpServer(
                    new DhmpWireContract(4),
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        32)),
                span => receivedA = span.ToArray()));

        router.Register(
            new DhmpRawIpv6PeerBinding(
                peerB,
                new DhmpServer(
                    new DhmpWireContract(8),
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Latest,
                        32)),
                span => receivedB = span.ToArray()));

        Assert.True(
            router.TryRoute(
                peerA,
                new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                new byte[128]));

        Assert.True(
            router.TryRoute(
                peerB,
                Enumerable.Range(1, 16)
                    .Select(i => (byte)i)
                    .ToArray(),
                new byte[128]));

        Assert.Equal(
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            receivedA);

        Assert.Equal(
            new byte[] { 9, 10, 11, 12, 13, 14, 15, 16 },
            receivedB);

        Assert.Equal(2, router.AcceptedPackets);
        Assert.Equal(2, router.PeerCount);
    }

    [Fact]
    public void DuplicateSourceAddress_IsRejected()
    {
        var router =
            new DhmpRawIpv6PeerRouter(4, 128);

        var address =
            IPAddress.Parse("2001:db8::1");

        var binding =
            Binding(address);

        router.Register(binding);

        Assert.Throws<InvalidOperationException>(() =>
            router.Register(
                Binding(address)));
    }

    [Fact]
    public void PeerLimit_IsBounded()
    {
        var router =
            new DhmpRawIpv6PeerRouter(1, 128);

        router.Register(
            Binding(
                IPAddress.Parse("2001:db8::1")));

        Assert.Throws<InvalidOperationException>(() =>
            router.Register(
                Binding(
                    IPAddress.Parse("2001:db8::2"))));
    }

    [Fact]
    public void UnknownPeer_IsDroppedWithoutPublication()
    {
        var router =
            new DhmpRawIpv6PeerRouter(4, 128);

        int callbacks = 0;

        router.Register(
            new DhmpRawIpv6PeerBinding(
                IPAddress.Parse("2001:db8::1"),
                new DhmpServer(
                    new DhmpWireContract(4)),
                _ => callbacks++));

        Assert.False(
            router.TryRoute(
                IPAddress.Parse("2001:db8::2"),
                new byte[4],
                new byte[128]));

        Assert.Equal(0, callbacks);
        Assert.Equal(1, router.UnknownPeerPackets);
    }

    [Fact]
    public void MalformedPacket_IsRejectedForMatchedPeer()
    {
        var router =
            new DhmpRawIpv6PeerRouter(4, 128);

        var peer =
            IPAddress.Parse("2001:db8::1");

        int callbacks = 0;

        router.Register(
            new DhmpRawIpv6PeerBinding(
                peer,
                new DhmpServer(
                    new DhmpWireContract(4)),
                _ => callbacks++));

        Assert.False(
            router.TryRoute(
                peer,
                new byte[3],
                new byte[128]));

        Assert.Equal(0, callbacks);
        Assert.Equal(1, router.RejectedPackets);
    }

    [Fact]
    public void DecoderFailure_IsTrackedSeparately()
    {
        var router =
            new DhmpRawIpv6PeerRouter(4, 128);

        var peer =
            IPAddress.Parse("2001:db8::1");

        router.Register(
            new DhmpRawIpv6PeerBinding(
                peer,
                new DhmpServer(
                    new DhmpWireContract(4)),
                _ => throw new InvalidOperationException(
                    "must not publish"),
                new PrefixDecoder()));

        Assert.False(
            router.TryRoute(
                peer,
                new byte[] { 0x00, 1, 2, 3, 4 },
                new byte[128]));

        Assert.Equal(
            1,
            router.ProtectionRejectedPackets);
        Assert.Equal(
            0,
            router.RejectedPackets);
    }

    [Fact]
    public void ProtectedPeer_DecodesRoutesAndClearsScratch()
    {
        var router =
            new DhmpRawIpv6PeerRouter(4, 128);

        var peer =
            IPAddress.Parse("2001:db8::1");

        byte[]? received = null;
        byte[] scratch =
            Enumerable.Repeat((byte)0xcc, 128)
                .ToArray();

        router.Register(
            new DhmpRawIpv6PeerBinding(
                peer,
                new DhmpServer(
                    new DhmpWireContract(4)),
                span => received = span.ToArray(),
                new PrefixDecoder()));

        Assert.True(
            router.TryRoute(
                peer,
                new byte[] { 0xa5, 1, 2, 3, 4 },
                scratch));

        Assert.Equal(
            new byte[] { 1, 2, 3, 4 },
            received);

        Assert.All(
            scratch.AsSpan(0, 4).ToArray(),
            value => Assert.Equal((byte)0, value));

        Assert.Equal(1, router.AcceptedPackets);
    }

    [Fact]
    public void BindingMustFitListenerNetworkCeiling()
    {
        var router =
            new DhmpRawIpv6PeerRouter(
                maximumPeers: 4,
                maximumNetworkPayloadBytes: 64);

        var binding =
            new DhmpRawIpv6PeerBinding(
                IPAddress.Parse("2001:db8::1"),
                new DhmpServer(
                    new DhmpWireContract(4),
                    new DhmpReceivePolicy(
                        DhmpProcessingMode.Sequential,
                        64)),
                _ => { },
                new PrefixDecoder());

        Assert.Throws<ArgumentException>(() =>
            router.Register(binding));
    }

    [Fact]
    public void Remove_FreesSourceAddressForReplacementSession()
    {
        var router =
            new DhmpRawIpv6PeerRouter(1, 128);

        var peer =
            IPAddress.Parse("2001:db8::1");

        router.Register(Binding(peer));

        Assert.True(
            router.Remove(peer));
        Assert.Equal(0, router.PeerCount);

        router.Register(Binding(peer));

        Assert.Equal(1, router.PeerCount);
    }

    [Fact]
    public void ListenerOptions_AreBoundedAndIpv6Only()
    {
        var options =
            new DhmpRawIpv6ListenerOptions(
                IPAddress.IPv6Any,
                maximumPayloadBytes: 1408,
                maximumPeers: 256);

        Assert.Equal(256, options.MaximumPeers);
        Assert.Equal(1408, options.MaximumPayloadBytes);
        Assert.Equal(
            DhmpProtocol.ExperimentalIpv6DataNextHeader,
            options.DataProtocolNumber);

        Assert.Throws<ArgumentException>(() =>
            new DhmpRawIpv6ListenerOptions(
                IPAddress.Any,
                1408));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpRawIpv6ListenerOptions(
                IPAddress.IPv6Any,
                1408,
                maximumPeers: 0));
    }

    private static DhmpRawIpv6PeerBinding Binding(
        IPAddress address)
        => new(
            address,
            new DhmpServer(
                new DhmpWireContract(4)),
            _ => { });

    private sealed class PrefixDecoder :
        IDhmpPacketDecoder
    {
        public int OverheadBytes => 1;

        public bool TryDecode(
            ReadOnlySpan<byte> packet,
            Span<byte> plaintextDestination,
            out int plaintextBytes)
        {
            plaintextBytes = 0;

            if (packet.Length <= 1 ||
                packet[0] != 0xa5)
                return false;

            int length = packet.Length - 1;

            packet[1..].CopyTo(
                plaintextDestination[..length]);

            plaintextBytes = length;
            return true;
        }
    }
}
