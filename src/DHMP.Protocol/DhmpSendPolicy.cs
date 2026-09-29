namespace DHMP.Protocol;

/// <summary>
/// Local outbound policy. These values are not part of DHMP V1 wire identity.
/// </summary>
public readonly record struct DhmpSendPolicy
{
    public DhmpSendPolicy(
        int pmax,
        int maximumPayloadBytes = 1408)
    {
        if (pmax <= 0)
            throw new ArgumentOutOfRangeException(nameof(pmax));
        if (maximumPayloadBytes <= 0 || maximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));

        Pmax = pmax;
        MaximumPayloadBytes = maximumPayloadBytes;
    }

    public int Pmax { get; }
    public int MaximumPayloadBytes { get; }

    public void Validate(DhmpWireContract wireContract)
    {
        wireContract.Validate();

        if (Pmax <= 0 ||
            MaximumPayloadBytes < wireContract.RecordSize ||
            MaximumPayloadBytes > ushort.MaxValue)
            throw new ArgumentException(
                "A valid local DHMP send policy is required.");
    }
}
