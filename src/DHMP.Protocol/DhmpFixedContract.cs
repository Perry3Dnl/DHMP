namespace DHMP.Protocol;

public readonly record struct DhmpFixedContract
{
    public DhmpFixedContract(int payloadSize, int pmax)
    {
        if (payloadSize <= 0) throw new ArgumentOutOfRangeException(nameof(payloadSize));
        if (pmax <= 0) throw new ArgumentOutOfRangeException(nameof(pmax));
        PayloadSize = payloadSize;
        Pmax = pmax;
    }

    public int PayloadSize { get; }
    public int Pmax { get; }

    public void ValidatePayload(int length)
    {
        if (length != PayloadSize)
            throw new DhmpProtocolException($"DHMP fixed contract requires exactly {PayloadSize} payload bytes; received {length}.");
    }
}
