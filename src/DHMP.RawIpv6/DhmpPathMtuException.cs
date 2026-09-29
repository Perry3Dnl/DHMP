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
            $"Raw IPv6 path rejected a {attemptedPayloadBytes}-byte DHMP network payload as too large. Configured backend ceiling is {configuredPayloadCeiling} bytes.",
            innerException)
    {
        AttemptedPayloadBytes =
            attemptedPayloadBytes;
        ConfiguredPayloadCeiling =
            configuredPayloadCeiling;
    }

    public int AttemptedPayloadBytes { get; }
    public int ConfiguredPayloadCeiling { get; }
}
