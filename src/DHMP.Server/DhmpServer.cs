using DHMP.Protocol;

namespace DHMP.Server;

/// <summary>
/// Receiver-side protocol facade for a preconfigured session. A direct-IP backend supplies
/// complete packet payloads; this class does not bind a listener or reconstruct a byte stream.
/// </summary>
public sealed class DhmpServer
{
    private readonly DhmpPacketProcessor _processor;

    public DhmpServer(DhmpFixedContract contract, DhmpProcessingMode mode = DhmpProcessingMode.Sequential)
        => _processor = new DhmpPacketProcessor(contract, mode);

    public void ProcessPacket(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<byte>> publishBatch)
        => _processor.Process(packet, publishBatch);
}
