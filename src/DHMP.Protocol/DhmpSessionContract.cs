namespace DHMP.Protocol;

/// <summary>
/// Immutable agreement required before headerless DHMP data packets can be exchanged.
/// These values are session/control-plane state; they are not repeated in every data packet.
/// </summary>
public readonly record struct DhmpSessionContract
{
    public DhmpSessionContract(
        DhmpFixedContract fixedContract,
        DhmpProcessingMode mode = DhmpProcessingMode.Sequential,
        byte version = DhmpProtocol.CurrentVersion)
    {
        fixedContract.Validate();
        if (version != DhmpProtocol.CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(version), "Unsupported DHMP session version.");
        if (mode is not DhmpProcessingMode.Sequential and not DhmpProcessingMode.Latest)
            throw new ArgumentOutOfRangeException(nameof(mode));

        Version = version;
        FixedContract = fixedContract;
        Mode = mode;
    }

    public byte Version { get; }
    public DhmpFixedContract FixedContract { get; }
    public DhmpProcessingMode Mode { get; }

    public int PayloadSize => FixedContract.PayloadSize;
    public int Pmax => FixedContract.Pmax;
    public int MaxPacketPayloadBytes => FixedContract.MaxPacketPayloadBytes;

    /// <summary>Validate at setup, including default(struct), which bypasses the constructor.</summary>
    public void Validate()
    {
        if (Version != DhmpProtocol.CurrentVersion)
            throw new ArgumentException("A supported DHMP session version is required.");
        if (Mode is not DhmpProcessingMode.Sequential and not DhmpProcessingMode.Latest)
            throw new ArgumentException("A supported DHMP processing mode is required.");
        FixedContract.Validate();
    }
}
