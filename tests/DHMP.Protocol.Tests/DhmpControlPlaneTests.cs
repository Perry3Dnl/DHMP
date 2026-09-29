using Xunit;

namespace DHMP.Protocol.Tests;

public sealed class DhmpControlPlaneTests
{
    private static readonly Guid SchemaId =
        Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f001");

    [Fact]
    public void ControlCodec_Hello_RoundTripsExactly32Bytes()
    {
        var profile = new DhmpPeerProfile(
            new DhmpWireContract(32),
            maximumReceivePayloadBytes: 1408,
            SchemaId);

        var message =
            DhmpControlMessage.Hello(
                profile,
                correlationId: 0x10203040);

        byte[] packet =
            new byte[DhmpProtocol.ControlPacketSize];

        DhmpControlCodec.Encode(message, packet);

        Assert.Equal(32, packet.Length);
        Assert.Equal((byte)'D', packet[0]);
        Assert.Equal((byte)'H', packet[1]);
        Assert.Equal((byte)'M', packet[2]);
        Assert.Equal((byte)'C', packet[3]);

        Assert.True(
            DhmpControlCodec.TryDecode(
                packet,
                out var decoded));

        Assert.Equal(message, decoded);
    }

    [Fact]
    public void ControlCodec_AcceptAndReject_RoundTrip()
    {
        var profile = new DhmpPeerProfile(
            new DhmpWireContract(64),
            1280,
            SchemaId);

        foreach (var message in new[]
        {
            DhmpControlMessage.Accept(
                profile,
                7),
            DhmpControlMessage.Reject(
                profile,
                8,
                DhmpControlRejectReason.SchemaMismatch)
        })
        {
            byte[] packet =
                new byte[DhmpProtocol.ControlPacketSize];

            DhmpControlCodec.Encode(message, packet);

            Assert.True(
                DhmpControlCodec.TryDecode(
                    packet,
                    out var decoded));

            Assert.Equal(message, decoded);
        }
    }

    [Fact]
    public void ControlCodec_RejectsWrongMagicVersionLengthAndType()
    {
        var profile = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1408,
            SchemaId);

        byte[] packet =
            new byte[DhmpProtocol.ControlPacketSize];

        DhmpControlCodec.Encode(
            DhmpControlMessage.Hello(
                profile,
                42),
            packet);

        byte[] wrongMagic = packet.ToArray();
        wrongMagic[0] ^= 1;
        Assert.False(
            DhmpControlCodec.TryDecode(
                wrongMagic,
                out _));

        byte[] wrongVersion = packet.ToArray();
        wrongVersion[4]++;
        Assert.False(
            DhmpControlCodec.TryDecode(
                wrongVersion,
                out _));

        byte[] wrongType = packet.ToArray();
        wrongType[5] = 99;
        Assert.False(
            DhmpControlCodec.TryDecode(
                wrongType,
                out _));

        Assert.False(
            DhmpControlCodec.TryDecode(
                packet.AsSpan(0, 31),
                out _));
    }

    [Fact]
    public void MatchingHello_IsAccepted_AndRemoteProfileIsReturned()
    {
        var local = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1408,
            SchemaId);

        var remote = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1024,
            SchemaId);

        var evaluation =
            DhmpControlNegotiator.EvaluateHello(
                local,
                DhmpControlMessage.Hello(
                    remote,
                    123));

        Assert.True(evaluation.Accepted);
        Assert.Equal(
            DhmpControlMessageType.Accept,
            evaluation.Response.Type);
        Assert.Equal(
            123u,
            evaluation.Response.CorrelationId);
        Assert.Equal(
            remote,
            evaluation.RemoteProfile);
        Assert.Equal(
            1408,
            evaluation.Response.MaximumReceivePayloadBytes);
    }

    [Theory]
    [InlineData(2, 32, DhmpControlRejectReason.UnsupportedWireVersion)]
    [InlineData(1, 64, DhmpControlRejectReason.RecordSizeMismatch)]
    public void IncompatibleWireHello_IsRejected(
        int version,
        int recordSize,
        DhmpControlRejectReason expected)
    {
        var local = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1408,
            SchemaId);

        var hello = new DhmpControlMessage(
            DhmpControlMessageType.Hello,
            checked((byte)version),
            recordSize,
            1408,
            SchemaId,
            55);

        var evaluation =
            DhmpControlNegotiator.EvaluateHello(
                local,
                hello);

        Assert.False(evaluation.Accepted);
        Assert.Null(evaluation.RemoteProfile);
        Assert.Equal(
            DhmpControlMessageType.Reject,
            evaluation.Response.Type);
        Assert.Equal(
            expected,
            evaluation.Response.RejectReason);
    }

    [Fact]
    public void SchemaMismatch_IsRejected()
    {
        var local = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1408,
            SchemaId);

        var otherSchema = Guid.NewGuid();

        var hello = new DhmpControlMessage(
            DhmpControlMessageType.Hello,
            DhmpProtocol.CurrentVersion,
            32,
            1408,
            otherSchema,
            77);

        var evaluation =
            DhmpControlNegotiator.EvaluateHello(
                local,
                hello);

        Assert.Equal(
            DhmpControlRejectReason.SchemaMismatch,
            evaluation.Response.RejectReason);
        Assert.False(evaluation.Accepted);
    }

    [Fact]
    public void AcceptedResponse_ClampsLocalSendCeiling_ToRemoteCapabilityAndRecordBoundary()
    {
        var local = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1408,
            SchemaId);

        var remote = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1000,
            SchemaId);

        var response =
            DhmpControlMessage.Accept(
                remote,
                99);

        var negotiated =
            DhmpControlNegotiator.CompleteResponse(
                local,
                new DhmpSendPolicy(
                    pmax: 5000,
                    maximumPayloadBytes: 1408),
                expectedCorrelationId: 99,
                response);

        Assert.Equal(
            remote,
            negotiated.RemoteProfile);
        Assert.Equal(
            5000,
            negotiated.EffectiveSendPolicy.Pmax);
        Assert.Equal(
            992,
            negotiated.EffectiveSendPolicy.MaximumPayloadBytes);
    }

    [Fact]
    public void RejectResponse_BecomesNegotiationException()
    {
        var local = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1408,
            SchemaId);

        var response =
            DhmpControlMessage.Reject(
                local,
                100,
                DhmpControlRejectReason.SchemaMismatch);

        var error =
            Assert.Throws<DhmpNegotiationException>(() =>
                DhmpControlNegotiator.CompleteResponse(
                    local,
                    new DhmpSendPolicy(1000),
                    100,
                    response));

        Assert.Equal(
            DhmpControlRejectReason.SchemaMismatch,
            error.Reason);
    }

    [Fact]
    public void ResponseWithWrongCorrelation_IsRejected()
    {
        var local = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1408,
            SchemaId);

        var response =
            DhmpControlMessage.Accept(
                local,
                200);

        Assert.Throws<DhmpProtocolException>(() =>
            DhmpControlNegotiator.CompleteResponse(
                local,
                new DhmpSendPolicy(1000),
                expectedCorrelationId: 201,
                response));
    }

    [Fact]
    public void Negotiation_PreservesSmoothPacingPolicy()
    {
        var local = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1408,
            SchemaId);

        var remote = new DhmpPeerProfile(
            new DhmpWireContract(32),
            1024,
            SchemaId);

        var negotiated =
            DhmpControlNegotiator.CompleteResponse(
                local,
                new DhmpSendPolicy(
                    5000,
                    1408,
                    DhmpRatePolicy.SmoothPacing),
                321,
                DhmpControlMessage.Accept(
                    remote,
                    321));

        Assert.Equal(
            DhmpRatePolicy.SmoothPacing,
            negotiated.EffectiveSendPolicy.RatePolicy);
        Assert.Equal(
            1024,
            negotiated.EffectiveSendPolicy.MaximumPayloadBytes);
    }

    [Fact]
    public void DataAndControlBindings_AreDistinctExperimentalValues()
    {
        Assert.Equal(
            253,
            DhmpProtocol.ExperimentalIpv6DataNextHeader);
        Assert.Equal(
            254,
            DhmpProtocol.ExperimentalIpv6ControlNextHeader);
        Assert.NotEqual(
            DhmpProtocol.ExperimentalIpv6DataNextHeader,
            DhmpProtocol.ExperimentalIpv6ControlNextHeader);
    }
}
