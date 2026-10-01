namespace DHMP.AspNetCore;

public sealed class DhmpRuntimeState
{
    private int _apiReady;
    public bool ApiReady => Volatile.Read(ref _apiReady) != 0;
    internal void SetApiReady(bool value) => Interlocked.Exchange(ref _apiReady, value ? 1 : 0);

    public bool LicenseValidated { get; internal set; }
    public bool RuntimeActivated { get; internal set; }
}

