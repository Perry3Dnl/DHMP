namespace DHMP.Security;

public sealed class DhmpSecurityException : InvalidOperationException
{
    public DhmpSecurityException(string message)
        : base(message)
    {
    }
}
