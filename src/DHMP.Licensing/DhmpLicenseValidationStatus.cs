namespace DHMP.Licensing;

public enum DhmpLicenseValidationStatus
{
    Valid,
    MissingKey,
    MalformedKey,
    UnsupportedVersion,
    InvalidSignature,
    ApplicationMismatch
}
