namespace DHMP.Protocol;

/// <summary>
/// Optional lower-layer packet transform used before DHMP V1 plaintext validation.
/// Implementations may decrypt/authenticate a protected packet into caller-owned storage.
/// </summary>
public interface IDhmpPacketDecoder
{
    /// <summary>Additional protected bytes carried around one plaintext DHMP payload.</summary>
    int OverheadBytes { get; }

    /// <summary>
    /// Decode one protected packet into caller-owned plaintext storage.
    /// Return false for authentication, replay or format rejection.
    /// </summary>
    bool TryDecode(
        ReadOnlySpan<byte> packet,
        Span<byte> plaintextDestination,
        out int plaintextBytes);
}
