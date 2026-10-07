using System.Net;
using System.Threading.Channels;
using DHMP.Protocol;
using DHMP.RawIpv6;
using Xunit;

namespace DHMP.RawIpv6.Tests;

public sealed class DhmpPokeLifecycleTests
{
    [Theory]
    [InlineData(DhmpPokeCodec.MinimumPacketSize)]
    [InlineData(DhmpPokeCodec.FullEchoPacketSize)]
    public async Task Poke_client_requires_exact_echo(
        int packetBytes)
    {
        var channel =
            new EchoChannel();

        TimeSpan elapsed =
            await DhmpRawIpv6Poke.ProbeCoreAsync(
                TimeSpan.FromSeconds(1),
                packetBytes,
                () => channel,
                TimeProvider.System,
                TestContext.Current.CancellationToken);

        Assert.True(
            elapsed >= TimeSpan.Zero);

        Assert.Single(
            channel.Sent);

        Assert.Equal(
            packetBytes,
            channel.Sent[0].Length);
    }

    [Fact]
    public async Task Compatibility_responder_echoes_Poke_then_continues_to_Hello()
    {
        const int recordSize = 16;
        Guid schema =
            Guid.NewGuid();

        var wire =
            new DhmpWireContract(
                recordSize);

        var send =
            new DhmpSendPolicy(
                long.MaxValue,
                1408,
                DhmpRatePolicy.Unlimited);

        var receive =
            new DhmpReceivePolicy(
                DhmpProcessingMode.Sequential,
                1408);

        byte[] poke =
            new byte[
                DhmpPokeCodec.MinimumPacketSize];

        DhmpPokeCodec.Encode(
            0x0102030405060708UL,
            poke);

        byte[] hello =
            new byte[
                DhmpProtocol.ControlPacketSize];

        DhmpControlCodec.Encode(
            DhmpControlMessage.Hello(
                new DhmpPeerProfile(
                    wire,
                    1408,
                    schema),
                correlationId: 7),
            hello);

        var channel =
            new ScriptedChannel();

        channel.Enqueue(
            poke);

        channel.Enqueue(
            hello);

        DhmpNegotiatedPeer peer =
            await DhmpRawIpv6Handshake.RespondCoreAsync(
                Options(),
                wire,
                send,
                receive,
                schema,
                () => channel,
                TimeProvider.System,
                TestContext.Current.CancellationToken);

        Assert.Equal(
            recordSize,
            peer.RemoteProfile.WireContract.RecordSize);

        Assert.Equal(
            2,
            channel.Sent.Count);

        Assert.Equal(
            poke,
            channel.Sent[0]);

        Assert.Equal(
            DhmpProtocol.ControlPacketSize,
            channel.Sent[1].Length);
    }

    private static DhmpRawIpv6Options Options() =>
        new(
            IPAddress.IPv6Loopback,
            IPAddress.Parse("::2"),
            maximumPayloadBytes: 1408,
            handshakeTimeout: TimeSpan.FromSeconds(1),
            enableExperimentalProtocolNumbers: true,
            allowUnprotectedPayloads: true);

    private sealed class EchoChannel :
        IDhmpControlPacketChannel
    {
        private byte[]? _echo;

        public List<byte[]> Sent { get; } =
            new();

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> packet,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _echo =
                packet.ToArray();

            Sent.Add(
                _echo);

            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReceivePacketAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] echo =
                _echo ??
                throw new InvalidOperationException(
                    "No Poke was sent.");

            echo.AsMemory()
                .CopyTo(
                    destination);

            return ValueTask.FromResult(
                echo.Length);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ScriptedChannel :
        IDhmpControlPacketChannel
    {
        private readonly Channel<byte[]> _incoming =
            Channel.CreateUnbounded<byte[]>();

        public List<byte[]> Sent { get; } =
            new();

        public void Enqueue(
            byte[] packet) =>
            Assert.True(
                _incoming.Writer.TryWrite(
                    packet));

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> packet,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Sent.Add(
                packet.ToArray());

            return ValueTask.CompletedTask;
        }

        public async ValueTask<int> ReceivePacketAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            byte[] packet =
                await _incoming.Reader.ReadAsync(
                    cancellationToken);

            packet.AsMemory()
                .CopyTo(
                    destination);

            return packet.Length;
        }

        public void Dispose()
        {
        }
    }
}
