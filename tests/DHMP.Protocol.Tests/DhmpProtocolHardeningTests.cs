using System.Buffers.Binary;
using Xunit;

namespace DHMP.Protocol.Tests;

public sealed class DhmpProtocolHardeningTests
{
    private static readonly Guid SchemaId =
        Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");

    [Fact]
    public void HappyFlow_ControlPacket_UsesStableNetworkByteLayout()
    {
        var profile = new DhmpPeerProfile(
            new DhmpWireContract(0x0123),
            maximumReceivePayloadBytes: 0x0456,
            SchemaId);

        var message =
            DhmpControlMessage.Hello(
                profile,
                0x10203040);

        byte[] packet =
            new byte[DhmpProtocol.ControlPacketSize];

        DhmpControlCodec.Encode(
            message,
            packet);

        Assert.Equal("DHMC"u8.ToArray(), packet[..4]);
        Assert.Equal(DhmpProtocol.ControlVersion, packet[4]);
        Assert.Equal((byte)DhmpControlMessageType.Hello, packet[5]);
        Assert.Equal(DhmpProtocol.CurrentVersion, packet[6]);
        Assert.Equal((byte)DhmpControlRejectReason.None, packet[7]);

        Assert.Equal(
            0x0123,
            BinaryPrimitives.ReadUInt16BigEndian(
                packet.AsSpan(8, 2)));

        Assert.Equal(
            0x0456,
            BinaryPrimitives.ReadUInt16BigEndian(
                packet.AsSpan(10, 2)));

        Assert.Equal(
            SchemaId,
            new Guid(
                packet.AsSpan(12, 16),
                bigEndian: true));

        Assert.Equal(
            0x10203040u,
            BinaryPrimitives.ReadUInt32BigEndian(
                packet.AsSpan(28, 4)));
    }

    [Fact]
    public void CriticalFlow_DefaultControlMessage_FailsClosedEverywhere()
    {
        var profile =
            new DhmpPeerProfile(
                new DhmpWireContract(32),
                1408,
                SchemaId);

        byte[] destination =
            new byte[DhmpProtocol.ControlPacketSize];

        Assert.Throws<ArgumentException>(() =>
            DhmpControlCodec.Encode(
                default,
                destination));

        Assert.Throws<ArgumentException>(() =>
            DhmpControlNegotiator.EvaluateHello(
                profile,
                default));

        Assert.Throws<ArgumentException>(() =>
            DhmpControlNegotiator.CompleteResponse(
                profile,
                new DhmpSendPolicy(1000),
                expectedCorrelationId: 1,
                default));
    }

    [Fact]
    public void CriticalFlow_ControlCodec_RejectsTruncatedAndOversizedPackets()
    {
        var profile =
            new DhmpPeerProfile(
                new DhmpWireContract(32),
                1408,
                SchemaId);

        byte[] packet =
            new byte[DhmpProtocol.ControlPacketSize];

        DhmpControlCodec.Encode(
            DhmpControlMessage.Hello(
                profile,
                1),
            packet);

        Assert.False(
            DhmpControlCodec.TryDecode(
                packet.AsSpan(0, packet.Length - 1),
                out _));

        byte[] oversized =
            new byte[packet.Length + 1];

        packet.CopyTo(oversized, 0);

        Assert.False(
            DhmpControlCodec.TryDecode(
                oversized,
                out _));

        Assert.Throws<ArgumentException>(() =>
            DhmpControlCodec.Encode(
                DhmpControlMessage.Hello(
                    profile,
                    1),
                new byte[
                    DhmpProtocol.ControlPacketSize - 1]));
    }

    [Fact]
    public void CriticalFlow_ControlMessage_InvalidSemanticCombinationsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpControlMessage(
                (DhmpControlMessageType)99,
                1,
                32,
                1408,
                SchemaId,
                1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpControlMessage(
                DhmpControlMessageType.Hello,
                0,
                32,
                1408,
                SchemaId,
                1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpControlMessage(
                DhmpControlMessageType.Hello,
                1,
                32,
                31,
                SchemaId,
                1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DhmpControlMessage(
                DhmpControlMessageType.Hello,
                1,
                32,
                1408,
                SchemaId,
                0));

        Assert.Throws<ArgumentException>(() =>
            new DhmpControlMessage(
                DhmpControlMessageType.Reject,
                1,
                32,
                1408,
                SchemaId,
                1));

        Assert.Throws<ArgumentException>(() =>
            new DhmpControlMessage(
                DhmpControlMessageType.Accept,
                1,
                32,
                1408,
                SchemaId,
                1,
                DhmpControlRejectReason.SchemaMismatch));
    }

    [Fact]
    public void HappyFlow_NegotiatedLimit_NeverIncreasesAndAlignsToWholeRecords()
    {
        var remote =
            new DhmpPeerProfile(
                new DhmpWireContract(24),
                1408,
                SchemaId);

        var negotiated =
            new DhmpNegotiatedPeer(
                remote,
                new DhmpSendPolicy(
                    pmax: 5000,
                    maximumPayloadBytes: 1200,
                    ratePolicy:
                        DhmpRatePolicy.SmoothPacing));

        var sameOrLower =
            negotiated.ConstrainToPayloadLimit(
                1187);

        Assert.Equal(
            1176,
            sameOrLower.MaximumPayloadBytes);

        Assert.Equal(
            5000,
            sameOrLower.Pmax);

        Assert.Equal(
            DhmpRatePolicy.SmoothPacing,
            sameOrLower.RatePolicy);

        var cannotIncrease =
            negotiated.ConstrainToPayloadLimit(
                5000);

        Assert.Equal(
            1200,
            cannotIncrease.MaximumPayloadBytes);
    }

    [Fact]
    public void CriticalFlow_NegotiatedLimit_MustFitAtLeastOneRecord()
    {
        var negotiated =
            new DhmpNegotiatedPeer(
                new DhmpPeerProfile(
                    new DhmpWireContract(64),
                    1408,
                    SchemaId),
                new DhmpSendPolicy(
                    1000,
                    1408));

        Assert.Throws<ArgumentException>(() =>
            negotiated.ConstrainToPayloadLimit(
                63));
    }

    [Fact]
    public void CriticalFlow_DefaultPolicyAndProfileStructsAreRejected()
    {
        var wire =
            new DhmpWireContract(32);

        Assert.Throws<ArgumentException>(() =>
            default(DhmpReceivePolicy)
                .Validate(wire));

        Assert.Throws<ArgumentException>(() =>
            default(DhmpPeerProfile)
                .Validate());

        Assert.Throws<ArgumentException>(() =>
            new DhmpPacketProcessor(
                wire,
                default));
    }

    [Fact]
    public void CriticalFlow_DefaultCongestionFeedback_DoesNotMutateRateController()
    {
        var controller =
            new DhmpAdaptiveRateController(
                maximumMessagesPerSecond: 10_000,
                minimumMessagesPerSecond: 1000);

        Assert.Throws<ArgumentException>(() =>
            controller.ApplyFeedback(default));

        Assert.Equal(
            10_000,
            controller.CurrentMessagesPerSecond);

        Assert.Equal(
            0,
            controller.FeedbackCount);

        Assert.Equal(
            0,
            controller.DecreaseCount);

        Assert.Equal(
            0,
            controller.RecoveryCount);
    }

    [Fact]
    public void HappyFlow_PacingRateUpdate_ChangesFutureSchedule()
    {
        var schedule =
            new DhmpPacingSchedule(1000);

        long timestamp =
            20 *
            System.Diagnostics.Stopwatch.Frequency;

        schedule.Commit(
            messages: 100,
            timestamp);

        TimeSpan first =
            schedule.GetDelay(
                1,
                timestamp);

        schedule.Reset();
        schedule.UpdateRate(500);
        schedule.Commit(
            messages: 100,
            timestamp);

        TimeSpan second =
            schedule.GetDelay(
                1,
                timestamp);

        Assert.InRange(
            first.TotalMilliseconds,
            99,
            101);

        Assert.InRange(
            second.TotalMilliseconds,
            199,
            201);
    }

    [Fact]
    public void CriticalFlow_AdaptiveController_ConcurrentNoPressureFeedbackIsBounded()
    {
        var controller =
            new DhmpAdaptiveRateController(
                maximumMessagesPerSecond: 50_000,
                minimumMessagesPerSecond: 1000);

        var noPressure =
            new DhmpCongestionFeedback(
                DhmpCongestionPressure.None,
                1000,
                0,
                1,
                0);

        Parallel.For(
            0,
            1000,
            _ => controller.ApplyFeedback(
                noPressure));

        Assert.Equal(
            50_000,
            controller.CurrentMessagesPerSecond);

        Assert.Equal(
            1000,
            controller.FeedbackCount);
    }

    [Theory]
    [InlineData(50, 1, DhmpCongestionPressure.Soft, 750)]
    [InlineData(10, 1, DhmpCongestionPressure.Hard, 500)]
    [InlineData(64, 0, DhmpCongestionPressure.None, 1000)]
    public void BoundaryFlow_PathLossThresholds_AreExact(
        int windowSpan,
        int missing,
        DhmpCongestionPressure expectedPressure,
        int expectedScale)
    {
        var telemetry =
            new DhmpPathTelemetry(
                TimeSpan.FromMilliseconds(25),
                highestPacketCounter:
                    checked((ulong)windowSpan),
                windowSpan,
                missing,
                acceptedPackets:
                    windowSpan - missing);

        var feedback =
            DhmpPathRateAdvisor.Evaluate(
                telemetry);

        Assert.Equal(
            expectedPressure,
            feedback.Pressure);

        Assert.Equal(
            expectedScale,
            feedback.RateScalePermille);
    }

    [Fact]
    public void BoundaryFlow_PathTelemetry_EmptyAndFullLossWindowsAreWellDefined()
    {
        var empty =
            new DhmpPathTelemetry(
                TimeSpan.Zero,
                0,
                0,
                0,
                0);

        var fullLoss =
            new DhmpPathTelemetry(
                TimeSpan.Zero,
                64,
                64,
                64,
                0);

        Assert.Equal(0, empty.LossPermille);
        Assert.Equal(1000, fullLoss.LossPermille);
    }
}
