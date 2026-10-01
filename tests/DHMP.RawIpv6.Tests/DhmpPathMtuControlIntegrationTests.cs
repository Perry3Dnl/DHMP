using System.Net.Sockets;
using System.Threading.Channels;
using DHMP.Protocol;
using DHMP.Security;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpPathMtuControlIntegrationTests
{
    [Fact]
    public async Task AuthenticatedControlLoop_DiscoversAppliesAndFallsBackLivePathBudget()
    {
        const int additionalIpv6HeaderBytes = 16;

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
            MaximumOutboundPathMtu = 1420,
            AdditionalIpv6HeaderBytes = additionalIpv6HeaderBytes
        };

        var responderWire = new MemoryControlChannel
        {
            MaximumOutboundPathMtu = 1500,
            AdditionalIpv6HeaderBytes = additionalIpv6HeaderBytes
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
                additionalIpv6HeaderBytes:
                    additionalIpv6HeaderBytes,
                probeTimeout: TimeSpan.FromSeconds(1),
                maximumProbeAttempts: 2,
                minimumSearchGainBytes: 1);

        var target =
            new MemoryPathBudgetTarget(
                maximumPayloadBytes:
                    1500 -
                    DhmpIpv6PathBudget.Ipv6BaseHeaderBytes -
                    additionalIpv6HeaderBytes,
                additionalIpv6HeaderBytes);

        DhmpPathMtuDiscoveryResult result =
            await initiator.DiscoverAndApplyPathMtuCoreAsync(
                target,
                options,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            DhmpPathMtuDiscoveryState.SearchComplete,
            result.State);

        Assert.True(result.BaseConfirmed);
        Assert.False(result.ReachedConfiguredMaximum);
        Assert.Equal(1420, result.ConfirmedPathMtu);

        int basePayload =
            1280 -
            DhmpIpv6PathBudget.Ipv6BaseHeaderBytes -
            additionalIpv6HeaderBytes;

        int discoveredPayload =
            1420 -
            DhmpIpv6PathBudget.Ipv6BaseHeaderBytes -
            additionalIpv6HeaderBytes;

        Assert.Equal(
            discoveredPayload,
            target.CurrentMaximumPayloadBytes);

        Assert.Equal(
            new[]
            {
                basePayload,
                discoveredPayload
            },
            target.AppliedPayloadLimits);

        Assert.Contains(
            basePayload,
            initiatorWire.AttemptedPayloadLengths);

        Assert.Contains(
            1500 -
                DhmpIpv6PathBudget.Ipv6BaseHeaderBytes -
                additionalIpv6HeaderBytes,
            initiatorWire.AttemptedPayloadLengths);

        Assert.All(
            responderWire.AttemptedPayloadLengths,
            length =>
                Assert.Equal(
                    DhmpPskChaCha20Poly1305Session
                        .PathMtuProbeMinimumPacketSize,
                    length));

        // Simulate a route change / black hole after discovery.
        initiatorWire.MaximumOutboundPathMtu = 1280;

        Assert.False(
            await initiator.ConfirmAndApplyPathMtuCoreAsync(
                target,
                1420,
                options,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            basePayload,
            target.CurrentMaximumPayloadBytes);

        Assert.True(
            await initiator.ConfirmAndApplyPathMtuCoreAsync(
                target,
                1280,
                options,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            basePayload,
            target.CurrentMaximumPayloadBytes);

        cancellation.Cancel();

        await Task.WhenAll(
            initiatorLoop,
            responderLoop);
    }

    private sealed class MemoryPathBudgetTarget :
        IDhmpPathBudgetTarget
    {
        private readonly int _additionalIpv6HeaderBytes;
        private int _currentMaximumPayloadBytes;

        public MemoryPathBudgetTarget(
            int maximumPayloadBytes,
            int additionalIpv6HeaderBytes)
        {
            MaximumPayloadBytes =
                maximumPayloadBytes;

            _currentMaximumPayloadBytes =
                maximumPayloadBytes;

            _additionalIpv6HeaderBytes =
                additionalIpv6HeaderBytes;
        }

        public int MaximumPayloadBytes { get; }

        public int CurrentMaximumPayloadBytes =>
            Volatile.Read(
                ref _currentMaximumPayloadBytes);

        public bool DynamicPathBudgetEnabled =>
            true;

        public int DynamicAdditionalIpv6HeaderBytes =>
            _additionalIpv6HeaderBytes;

        public List<int> AppliedPayloadLimits { get; } =
            new();

        public void ApplyConfirmedPathBudget(
            DhmpIpv6PathBudget pathBudget)
        {
            Assert.Equal(
                _additionalIpv6HeaderBytes,
                pathBudget.AdditionalIpv6HeaderBytes);

            Assert.InRange(
                pathBudget.MaximumProtocolPayloadBytes,
                1,
                MaximumPayloadBytes);

            Volatile.Write(
                ref _currentMaximumPayloadBytes,
                pathBudget.MaximumProtocolPayloadBytes);

            AppliedPayloadLimits.Add(
                pathBudget.MaximumProtocolPayloadBytes);
        }

        public void FallBackToMinimumPathBudget()
        {
            int payload =
                new DhmpIpv6PathBudget(
                    DhmpIpv6PathBudget.MinimumIpv6Mtu,
                    _additionalIpv6HeaderBytes)
                .MaximumProtocolPayloadBytes;

            Volatile.Write(
                ref _currentMaximumPayloadBytes,
                payload);

            AppliedPayloadLimits.Add(
                payload);
        }
    }

    private sealed class MemoryControlChannel :
        IDhmpControlPacketChannel
    {
        private readonly Channel<byte[]> _incoming =
            Channel.CreateUnbounded<byte[]>();

        public MemoryControlChannel? Peer { get; set; }

        public int MaximumOutboundPathMtu { get; set; } =
            DhmpIpv6PathBudget.MaximumNonJumboIpv6PacketBytes;

        public int AdditionalIpv6HeaderBytes { get; set; }

        public List<int> AttemptedPayloadLengths { get; } =
            new();

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> packet,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AttemptedPayloadLengths.Add(packet.Length);

            if (packet.Length +
                    DhmpIpv6PathBudget.Ipv6BaseHeaderBytes +
                    AdditionalIpv6HeaderBytes >
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

            packet.AsMemory().CopyTo(destination);
            return packet.Length;
        }

        public void Dispose()
            => _incoming.Writer.TryComplete();
    }
}
