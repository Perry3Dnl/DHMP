namespace DHMP.AspNetCore;

public sealed class DhmpRuntimeState
{
    public bool LicenseValidated { get; internal set; }
    public bool RuntimeActivated { get; internal set; }
}
