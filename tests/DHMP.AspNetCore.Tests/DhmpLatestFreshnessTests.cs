using System.Buffers.Binary;
using DHMP.Protocol;
using DHMP.Server;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpLatestFreshnessTests
{
    [Fact]
    public void NewerGeneration_IsPublished_StaleAndDuplicateAreDropped()
    {
        var server = new DhmpServer(
            new DhmpWireContract(16),
            new DhmpReceivePolicy(
                DhmpProcessingMode.Latest,
                64));

        var filter = new DhmpLatestGenerationFilter(
            recordSize: 16,
            generationOffset: 0,
            DhmpGenerationByteOrder.BigEndian);

        var published = new List<ulong>();

        Process(10);
        Process(12);
        Process(11);
        Process(12);
        Process(13);

        Assert.Equal(
            new ulong[] { 10, 12, 13 },
            published);
        Assert.Equal(13UL, filter.LatestGeneration);
        Assert.Equal(3, filter.AcceptedRecords);
        Assert.Equal(2, filter.StaleRecords);

        void Process(ulong generation)
        {
            byte[] packet = CreateRecord(generation);

            server.ProcessPacket(
                packet,
                record =>
                    filter.TryPublish(
                        record,
                        accepted =>
                            published.Add(
                                BinaryPrimitives.ReadUInt64BigEndian(
                                    accepted[..8]))));
        }
    }

    [Fact]
    public void LatestPacketRecord_IsFreshnessCheckedAcrossPackets()
    {
        var server = new DhmpServer(
            new DhmpWireContract(16),
            new DhmpReceivePolicy(
                DhmpProcessingMode.Latest,
                64));

        var filter = new DhmpLatestGenerationFilter(16);
        ulong? published = null;

        byte[] packet = new byte[32];
        CreateRecord(100).CopyTo(packet, 0);
        CreateRecord(101).CopyTo(packet, 16);

        server.ProcessPacket(
            packet,
            record =>
                filter.TryPublish(
                    record,
                    accepted =>
                        published =
                            BinaryPrimitives.ReadUInt64BigEndian(
                                accepted[..8])));

        Assert.Equal(101UL, published);

        byte[] delayed = CreateRecord(99);

        bool acceptedDelayed = false;

        server.ProcessPacket(
            delayed,
            record =>
                acceptedDelayed =
                    filter.TryPublish(
                        record,
                        _ => { }));

        Assert.False(acceptedDelayed);
        Assert.Equal(101UL, filter.LatestGeneration);
    }

    [Fact]
    public void GenerationWraparound_IsHandled()
    {
        var filter = new DhmpLatestGenerationFilter(16);
        var accepted = new List<ulong>();

        Publish(ulong.MaxValue - 1);
        Publish(ulong.MaxValue);
        Publish(0);
        Publish(1);

        Assert.Equal(
            new ulong[]
            {
                ulong.MaxValue - 1,
                ulong.MaxValue,
                0,
                1
            },
            accepted);

        void Publish(ulong generation)
        {
            byte[] record = CreateRecord(generation);

            filter.TryPublish(
                record,
                span =>
                    accepted.Add(
                        BinaryPrimitives.ReadUInt64BigEndian(
                            span[..8])));
        }
    }

    [Fact]
    public void HalfRangeAmbiguity_IsConservativelyRejected()
    {
        Assert.False(
            DhmpLatestGenerationFilter.IsNewer(
                1UL << 63,
                0));

        Assert.True(
            DhmpLatestGenerationFilter.IsNewer(
                1,
                ulong.MaxValue));
    }

    [Fact]
    public void LittleEndianApplicationGeneration_IsSupported()
    {
        var filter = new DhmpLatestGenerationFilter(
            16,
            generationOffset: 4,
            DhmpGenerationByteOrder.LittleEndian);

        byte[] record = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(
            record.AsSpan(4, 8),
            42);

        Assert.True(
            filter.TryPublish(
                record,
                _ => { }));

        Assert.Equal(42UL, filter.LatestGeneration);
    }

    private static byte[] CreateRecord(ulong generation)
    {
        byte[] record = new byte[16];

        BinaryPrimitives.WriteUInt64BigEndian(
            record.AsSpan(0, 8),
            generation);

        return record;
    }
}
