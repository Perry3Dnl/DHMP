namespace DHMP.RawIpv6;

/// <summary>
/// Indicates that the kernel/network path refused a raw IPv6 packet because
/// the submitted protocol payload was too large.
/// </summary>
public sealed class DhmpPathMtuException : IOException
{
    public DhmpPathMtuException(
        int attemptedPayloadBytes,
        int configuredPayloadCeiling,
        Exception innerException)
        : base(
            $"Raw IPv6 path rejected a {attemptedPayloadBytes}-byte DHMP network payload as too large. Current sender payload ceiling is {configuredPayloadCeiling} bytes.",
            innerException)
    {
        AttemptedPayloadBytes =
            attemptedPayloadBytes;
        PayloadCeilingBytes =
            configuredPayloadCeiling;
    }

    public int AttemptedPayloadBytes { get; }

    /// <summary>Payload ceiling that was active when the send failed.</summary>
    public int PayloadCeilingBytes { get; }

    /// <summary>
    /// Backward-compatible alias retained during pre-1.0 development.
    /// This value may now represent a live dynamic ceiling rather than the immutable configured maximum.
    /// </summary>
    public int ConfiguredPayloadCeiling =>
        PayloadCeilingBytes;
}
