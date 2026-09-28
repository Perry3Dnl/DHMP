namespace Dhmp.Server;

public sealed class DHMPServerOptions
{
    public int Port { get; set; } = 7777;
    public int MaxPayloadBytes { get; set; } = 32;
    public long PmaxMessagesPerSecond { get; set; } = Dhmp.Protocol.DhmpSendPolicy.MeasuredLatestPmax;
    public double SafetyFactor { get; set; } = 0.90;
}
