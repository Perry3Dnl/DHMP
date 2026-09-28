namespace Dhmp.Client;

public sealed class DHMPClientOptions
{
    public required Uri Endpoint { get; set; }
    public int MaxPayloadBytes { get; set; } = 32;
    public long PmaxMessagesPerSecond { get; set; } = Dhmp.Protocol.DhmpSendPolicy.MeasuredLatestPmax;
    public double SafetyFactor { get; set; } = 0.90;
}
