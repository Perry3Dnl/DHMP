namespace DHMP.AspNetCore;

/// <summary>
/// Host-level DHMP licensing options registered by <see cref="DhmpServiceCollectionExtensions"/>.
/// These values configure local startup validation; they are not peer-authentication credentials.
/// </summary>
public sealed class DhmpOptions
{
    /// <summary>Application identifier encoded in and validated against the offline license.</summary>
    public Guid ApplicationId { get; set; }

    /// <summary>Offline DHMP license key supplied by the application.</summary>
    public string? LicenseKey { get; set; }

    /// <summary>Trusted public verification key used only for offline license signature validation.</summary>
    public byte[] PublicVerificationKey { get; set; } = [];
}
