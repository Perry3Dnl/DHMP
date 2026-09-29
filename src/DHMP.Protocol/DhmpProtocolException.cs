namespace DHMP.Protocol;

public sealed class DhmpProtocolException : InvalidOperationException
{
    public DhmpProtocolException(string message) : base(message) { }
}
