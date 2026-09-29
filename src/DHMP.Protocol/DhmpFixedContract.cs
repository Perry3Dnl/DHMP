namespace DHMP.Protocol;

/// <summary>Fixed-record rules for the headerless DHMP data payload carried directly by IP.</summary>
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

    /// <summary>Bytes in exactly one application record.</summary>
    public int PayloadSize { get; }

    /// <summary>Configured logical-message send budget. This is not congestion control.</summary>
    public int Pmax { get; }

    /// <summary>
    /// Maximum headerless DHMP data bytes in one IP packet for this session.
    /// The backend must choose this so the complete lower-layer packet fits the path MTU.
    /// </summary>
    public int MaxPacketPayloadBytes { get; }

    /// <summary>Validate at setup, including default(struct), which bypasses the constructor.</summary>
    public void Validate()
    {
        if (PayloadSize <= 0 || Pmax <= 0 || MaxPacketPayloadBytes < PayloadSize ||
            MaxPacketPayloadBytes > ushort.MaxValue)
            throw new ArgumentException("A valid fixed DHMP data contract is required.");
    }

    public void ValidatePayload(int length)
    {
        if (length <= 0 || length != PayloadSize)
            throw new DhmpProtocolException($"Expected one {PayloadSize}-byte record; received {length} bytes.");
    }

    /// <summary>Reject empty, oversized and incomplete packets. Never carry bytes into another packet.</summary>
    public void ValidatePacket(int length)
    {
        if (PayloadSize <= 0 || length <= 0 || length > MaxPacketPayloadBytes || length % PayloadSize != 0)
            throw new DhmpProtocolException("DHMP packet data must contain only complete records within the session packet limit.");
    }
}
