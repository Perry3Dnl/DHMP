using DHMP.Licensing;

namespace DHMP.AspNetCore;

public sealed class DhmpLicenseException : InvalidOperationException
{
    public DhmpLicenseException(DhmpLicenseValidationStatus status)
        : base($"DHMP startup blocked: license validation failed with status '{status}'.")
    {
        Status = status;
    }

    public DhmpLicenseValidationStatus Status { get; }
}
