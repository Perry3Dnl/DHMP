using System.Net.Sockets;
using System.Threading.Channels;
using DHMP.Protocol;
using DHMP.Security;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpPathMtuControlIntegrationTests
{
    [Fact]
    public async Task AuthenticatedControlLoop_DiscoversLargestSupportedPathMtu()
    {
        using var key =
            new DhmpPreSharedKey(
                7,
                new byte[DhmpPreSharedKey.KeySizeBytes]);

        Guid sessionId =
            Guid.Parse("71223344-5566-7788-99aa-bbccddeeff00");

        using var initiatorSession =
            new DhmpPskChaCha20Poly1305Session(
                key,
                sessionId,
                DhmpSecurityRole.Initiator);

        using var responderSession =
            new DhmpPskChaCha20Poly1305Session(
                key,
                sessionId,
                DhmpSecurityRole.Responder);

        var initiatorWire = new MemoryControlChannel
        {
            MaximumOutboundPathMtu = 1420
        };

        var responderWire = new MemoryControlChannel
        {
            MaximumOutboundPathMtu = 1500
        };

        initiatorWire.Peer = responderWire;
        responderWire.Peer = initiatorWire;

        using var initiator =
            new DhmpRawIpv6CongestionChannel(
                initiatorWire,
                initiatorSession);

        using var responder =
            new DhmpRawIpv6CongestionChannel(
                responderWire,
                responderSession);

        var initiatorRate =
            new DhmpAdaptiveRateController(1000);

        var responderRate =
            new DhmpAdaptiveRateController(1000);

        using var cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);

        Task initiatorLoop =
            initiator.RunAdaptiveReceiveLoopAsync(
                initiatorRate,
                cancellationToken: cancellation.Token);

        Task responderLoop =
            responder.RunAdaptiveReceiveLoopAsync(
                responderRate,
                cancellationToken: cancellation.Token);

        var options =
            new DhmpPathMtuDiscoveryOptions(
                maximumPathMtu: 1500,
                probeTimeout: TimeSpan.FromSeconds(1),
                maximumProbeAttempts: 2,
                minimumSearchGainBytes: 1);

        DhmpPathMtuDiscoveryResult result =
            await initiator.DiscoverPathMtuAsync(
                options,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            DhmpPathMtuDiscoveryState.SearchComplete,
            result.State);

        Assert.True(result.BaseConfirmed);
        Assert.False(result.ReachedConfiguredMaximum);
        Assert.Equal(1420, result.ConfirmedPathMtu);

        Assert.Contains(
            1280 - DhmpIpv6PathBudget.Ipv6BaseHeaderBytes,
            initiatorWire.AttemptedPayloadLengths);

        Assert.Contains(
            1500 - DhmpIpv6PathBudget.Ipv6BaseHeaderBytes,
            initiatorWire.AttemptedPayloadLengths);

        Assert.All(
            responderWire.AttemptedPayloadLengths,
            length =>
                Assert.Equal(
                    DhmpPskChaCha20Poly1305Session
                        .PathMtuProbeMinimumPacketSize,
                    length));

        cancellation.Cancel();

        await Task.WhenAll(
            initiatorLoop,
            responderLoop);
    }

    private sealed class MemoryControlChannel :
        IDhmpControlPacketChannel
    {
        private readonly Channel<byte[]> _incoming =
            Channel.CreateUnbounded<byte[]>();

        public MemoryControlChannel? Peer { get; set; }

        public int MaximumOutboundPathMtu { get; init; } =
            DhmpIpv6PathBudget.MaximumNonJumboIpv6PacketBytes;

        public List<int> AttemptedPayloadLengths { get; } =
            new();

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> packet,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AttemptedPayloadLengths.Add(packet.Length);

            if (packet.Length +
                    DhmpIpv6PathBudget.Ipv6BaseHeaderBytes >
                MaximumOutboundPathMtu)
            {
                throw new SocketException(
                    (int)SocketError.MessageSize);
            }

            MemoryControlChannel peer =
                Peer ??
                throw new InvalidOperationException(
                    "Memory control peer is not connected.");

            if (!peer._incoming.Writer.TryWrite(
                    packet.ToArray()))
                throw new IOException(
                    "Memory control peer is closed.");

            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceivePacketAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            byte[] packet =
                await _incoming.Reader.ReadAsync(
                    cancellationToken);

            if (destination.Length < packet.Length)
                throw new ArgumentException(
                    "Destination is too small.");

            packet.CopyTo(destination);
            return packet.Length;
        }

        public void Dispose()
            => _incoming.Writer.TryComplete();
    }
}
