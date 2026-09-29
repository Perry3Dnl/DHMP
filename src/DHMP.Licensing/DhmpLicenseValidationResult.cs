namespace DHMP.Licensing;

public readonly record struct DhmpLicenseValidationResult(
    DhmpLicenseValidationStatus Status,
    Guid? ApplicationId = null,
    Guid? KeyId = null)
{
    public bool IsValid => Status == DhmpLicenseValidationStatus.Valid;
}
