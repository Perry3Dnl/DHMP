namespace DHMP.Protocol;

/// <summary>
/// Protocol-owned compatibility contract for headerless DHMP V1 data.
/// Only values that remote implementations must interpret identically belong here.
/// </summary>
public readonly record struct DhmpWireContract
{
    public DhmpWireContract(
        int recordSize,
        byte version = DhmpProtocol.CurrentVersion)
    {
        if (recordSize <= 0 || recordSize > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(recordSize));

        if (version != DhmpProtocol.CurrentVersion)
            throw new ArgumentOutOfRangeException(
                nameof(version),
                "Unsupported DHMP wire version.");

        RecordSize = recordSize;
        Version = version;
    }

    public int RecordSize { get; }
    public byte Version { get; }

    public void Validate()
    {
        if (RecordSize <= 0 ||
            RecordSize > ushort.MaxValue ||
            Version != DhmpProtocol.CurrentVersion)
            throw new ArgumentException("A supported DHMP wire contract is required.");
    }

    public void ValidateRecord(int length)
    {
        if (length != RecordSize)
            throw new DhmpProtocolException(
                $"Expected one {RecordSize}-byte DHMP record; received {length} bytes.");
    }

    public void ValidatePacket(int length, int maximumPayloadBytes)
    {
        if (maximumPayloadBytes < RecordSize ||
            maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        if (length <= 0 ||
            length > maximumPayloadBytes ||
            length % RecordSize != 0)
            throw new DhmpProtocolException(
                "DHMP packet data must contain only complete records within the configured local packet limit.");
    }
}
