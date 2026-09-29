namespace DHMP.AspNetCore;

public sealed class DhmpOptions
{
    public Guid ApplicationId { get; set; }
    public string? LicenseKey { get; set; }
    public byte[] PublicVerificationKey { get; set; } = [];
}
