namespace DHMP.AfXdp;

public static class DhmpAfXdpHostProbe
{
    public static DhmpAfXdpProbeResult Probe(
        string interfaceName,
        uint queueId = 0,
        bool preferZeroCopy = true)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
            throw new ArgumentException("A Linux interface name is required.", nameof(interfaceName));

        if (!OperatingSystem.IsLinux())
        {
            return new DhmpAfXdpProbeResult(
                false,
                DhmpAfXdpMode.Unavailable,
                "AF_XDP is Linux-only.");
        }

        bool supported = DhmpAfXdpNative.TryProbe(
            interfaceName,
            queueId,
            preferZeroCopy,
            out DhmpAfXdpMode mode,
            out string detail);

        return new DhmpAfXdpProbeResult(
            supported,
            supported ? mode : DhmpAfXdpMode.Unavailable,
            detail);
    }
}
