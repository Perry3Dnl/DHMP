using DHMP.Protocol;

namespace DHMP.Server;

/// <summary>
/// Receiver-side protocol facade for one configured DHMP session. A direct-IP backend supplies
/// complete headerless packet payloads; this class does not bind a listener or reconstruct a byte stream.
/// </summary>
public sealed class DhmpServer
{
    private readonly DhmpSessionContract _session;
    private readonly DhmpPacketProcessor _processor;

    public DhmpServer(DhmpSessionContract session)
    {
        session.Validate();
        _session = session;
        _processor = new DhmpPacketProcessor(session.FixedContract, session.Mode);
    }

    public DhmpSessionContract Session => _session;

    public void ProcessPacket(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<byte>> publishBatch)
        => _processor.Process(packet, publishBatch);
}
