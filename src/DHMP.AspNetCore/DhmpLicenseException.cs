using DHMP.Licensing;

namespace DHMP.AspNetCore;

/// <summary>Thrown when DHMP host startup is blocked by offline license validation.</summary>
public sealed class DhmpLicenseException : InvalidOperationException
{
    /// <summary>Create a startup exception for the supplied validation status.</summary>
    public DhmpLicenseException(DhmpLicenseValidationStatus status)
        : base($"DHMP startup blocked: license validation failed with status '{status}'.")
    {
        Status = status;
    }

    /// <summary>The validation result that blocked startup.</summary>
    public DhmpLicenseValidationStatus Status { get; }
}
