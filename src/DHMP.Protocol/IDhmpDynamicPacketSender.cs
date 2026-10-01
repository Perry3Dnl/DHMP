namespace DHMP.Protocol;

/// <summary>
/// Optional sender capability exposing a live payload ceiling that may shrink or grow
/// within the sender's immutable configured maximum.
/// </summary>
public interface IDhmpDynamicPacketSender : IDhmpPacketSender
{
    /// <summary>
    /// Current maximum payload accepted by the sender. This value never exceeds
    /// <see cref="IDhmpPacketSender.MaximumPayloadBytes"/>.
    /// </summary>
    int CurrentMaximumPayloadBytes { get; }
}
