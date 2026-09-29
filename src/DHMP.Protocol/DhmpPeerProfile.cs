namespace DHMP.Protocol;

/// <summary>
/// Compatibility/capability information exchanged on the control plane.
/// It is not prepended to V1 data packets.
/// </summary>
public readonly record struct DhmpPeerProfile
{
    public DhmpPeerProfile(
        DhmpWireContract wireContract,
        int maximumReceivePayloadBytes,
        Guid schemaId)
    {
        wireContract.Validate();

        if (maximumReceivePayloadBytes < wireContract.RecordSize ||
            maximumReceivePayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maximumReceivePayloadBytes));

        WireContract = wireContract;
        MaximumReceivePayloadBytes = maximumReceivePayloadBytes;
        SchemaId = schemaId;
    }

    public DhmpWireContract WireContract { get; }
    public int MaximumReceivePayloadBytes { get; }
    public Guid SchemaId { get; }

    public void Validate()
    {
        WireContract.Validate();

        if (MaximumReceivePayloadBytes < WireContract.RecordSize ||
            MaximumReceivePayloadBytes > ushort.MaxValue)
            throw new ArgumentException("A valid DHMP peer capability profile is required.");
    }
}
