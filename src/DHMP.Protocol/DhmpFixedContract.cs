namespace DHMP.Protocol;

/// <summary>Preconfigured fixed-record contract for complete direct-IP packet payloads.</summary>
public readonly record struct DhmpFixedContract
{
    public DhmpFixedContract(int payloadSize, int pmax, int maxPacketPayloadBytes = 1408)
    {
        if (payloadSize <= 0) throw new ArgumentOutOfRangeException(nameof(payloadSize));
        if (pmax <= 0) throw new ArgumentOutOfRangeException(nameof(pmax));
        if (maxPacketPayloadBytes < payloadSize || maxPacketPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxPacketPayloadBytes));
        PayloadSize = payloadSize;
        Pmax = pmax;
        MaxPacketPayloadBytes = maxPacketPayloadBytes;
    }

    public int PayloadSize { get; }
    public int Pmax { get; }
    /// <summary>Must fit the configured path MTU after all IP and security overhead.</summary>
    public int MaxPacketPayloadBytes { get; }

    /// <summary>Validate at setup, including default(struct), which bypasses the constructor.</summary>
    public void Validate()
    {
        if (PayloadSize <= 0 || Pmax <= 0 || MaxPacketPayloadBytes < PayloadSize ||
            MaxPacketPayloadBytes > ushort.MaxValue)
            throw new ArgumentException("A valid fixed packet contract is required.");
    }

    public void ValidatePayload(int length)
    {
        if (length <= 0 || length != PayloadSize)
            throw new DhmpProtocolException($"Expected one {PayloadSize}-byte message; received {length} bytes.");
    }

    /// <summary>Reject empty, oversized and incomplete packets. Never carry bytes into another packet.</summary>
    public void ValidatePacket(int length)
    {
        if (PayloadSize <= 0 || length <= 0 || length > MaxPacketPayloadBytes || length % PayloadSize != 0)
            throw new DhmpProtocolException("Packet must contain only complete messages within the configured packet limit.");
    }
}
