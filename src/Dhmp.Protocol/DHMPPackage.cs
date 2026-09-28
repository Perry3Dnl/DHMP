namespace Dhmp.Protocol;

/// <summary>
/// A complete fixed-contract DHMP package after stream extraction and before model materialization.
/// This is deliberately not a developer model.
/// </summary>
public readonly struct DHMPPackage
{
    public DHMPPackage(ReadOnlyMemory<byte> payload)
    {
        Payload = payload;
    }

    public ReadOnlyMemory<byte> Payload { get; }
    public int Length => Payload.Length;
}
