namespace DHMP.Connector;

/// <summary>
/// Location of the optional application-owned 64-bit ConnectionId inside each fixed record.
/// These bytes belong to the application schema, not to DHMP V1 framing.
/// </summary>
public readonly record struct DhmpConnectionIdField
{
    public const int Size = sizeof(ulong);

    public DhmpConnectionIdField(int offset)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        Offset = offset;
    }

    public int Offset { get; }

    internal void Validate(int recordSize)
    {
        if (Offset < 0 ||
            Offset > recordSize - Size)
            throw new ArgumentOutOfRangeException(
                nameof(Offset),
                $"The {Size}-byte ConnectionId field must fit completely inside the fixed application record.");
    }
}
