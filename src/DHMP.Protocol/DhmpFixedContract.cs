namespace DHMP.Protocol;

public readonly record struct DhmpFixedContract(int PayloadSize, int Pmax)
{
    public DhmpFixedContract
    {
        if (PayloadSize <= 0) throw new ArgumentOutOfRangeException(nameof(PayloadSize));
        if (Pmax <= 0) throw new ArgumentOutOfRangeException(nameof(Pmax));
    }

    public void ValidatePayload(int length)
    {
        if (length != PayloadSize)
            throw new DhmpProtocolException($"DHMP fixed contract requires exactly {PayloadSize} payload bytes; received {length}.");
    }
}
