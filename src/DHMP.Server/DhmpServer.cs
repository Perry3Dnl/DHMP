using DHMP.Protocol;

namespace DHMP.Server;

/// <summary>Receiver-side protocol facade for one configured DHMP wire contract and local receive policy.</summary>
public sealed class DhmpServer
{
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpReceivePolicy _receivePolicy;
    private readonly DhmpPacketProcessor _processor;
    private readonly DhmpLatestStateWindow? _latestStateWindow;

    public DhmpServer(DhmpWireContract wireContract, DhmpReceivePolicy receivePolicy = default)
    {
        wireContract.Validate();
        if (receivePolicy == default) receivePolicy = new DhmpReceivePolicy();
        receivePolicy.Validate(wireContract);

        _wireContract = wireContract;
        _receivePolicy = receivePolicy;
        _processor = new DhmpPacketProcessor(wireContract, receivePolicy);
        if (receivePolicy.NativeSmoothing)
            _latestStateWindow = new DhmpLatestStateWindow(wireContract.RecordSize);
    }

    public DhmpWireContract WireContract => _wireContract;
    public DhmpReceivePolicy ReceivePolicy => _receivePolicy;
    public bool NativeSmoothingEnabled => _latestStateWindow is not null;
    public int NativeSmoothingRecordCount => _latestStateWindow?.Count ?? 0;

    public void ProcessPacket(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<byte>> publishBatch)
    {
        if (_latestStateWindow is not null)
        {
            _wireContract.ValidatePacket(packet.Length, _receivePolicy.MaximumPayloadBytes);
            _latestStateWindow.PublishPacket(packet);
        }
        _processor.Process(packet, publishBatch);
    }

    /// <summary>Copy retained N-2/N-1/N records in chronological order; returns zero when disabled.</summary>
    public int CopyNativeSmoothingWindow(Span<byte> destination) =>
        _latestStateWindow?.CopyNewestTo(destination) ?? 0;
}
