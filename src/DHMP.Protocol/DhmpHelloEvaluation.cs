namespace DHMP.Protocol;

public readonly record struct DhmpHelloEvaluation(
    DhmpControlMessage Response,
    DhmpPeerProfile? RemoteProfile)
{
    public bool Accepted =>
        Response.Type == DhmpControlMessageType.Accept &&
        RemoteProfile.HasValue;
}
