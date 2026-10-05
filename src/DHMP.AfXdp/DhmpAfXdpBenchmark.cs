namespace DHMP.AfXdp;

public static class DhmpAfXdpBenchmark
{
    public static DhmpAfXdpBenchmarkResult RunTransmit(
        string interfaceName,
        int payloadBytes = 1200,
        long packets = 1_000_000,
        uint queueId = 0,
        bool preferZeroCopy = true)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
            throw new ArgumentException("A Linux interface name is required.", nameof(interfaceName));

        if (payloadBytes <= 0 || payloadBytes > 1408)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));

        if (packets <= 0)
            throw new ArgumentOutOfRangeException(nameof(packets));

        if (!OperatingSystem.IsLinux())
        {
            return new DhmpAfXdpBenchmarkResult(
                false,
                DhmpAfXdpMode.Unavailable,
                0,
                0,
                0,
                0,
                0,
                "AF_XDP is Linux-only.");
        }

        bool supported = DhmpAfXdpNative.TryBenchmark(
            interfaceName,
            queueId,
            payloadBytes,
            packets,
            preferZeroCopy,
            out DhmpAfXdpNative.NativeBenchmarkResult native,
            out string detail);

        if (!supported || native.Seconds <= 0)
        {
            return new DhmpAfXdpBenchmarkResult(
                false,
                DhmpAfXdpMode.Unavailable,
                native.PacketsCompleted,
                native.PayloadBytesCompleted,
                native.Seconds,
                0,
                0,
                detail);
        }

        return new DhmpAfXdpBenchmarkResult(
            true,
            DhmpAfXdpNative.ToMode(native.Mode),
            native.PacketsCompleted,
            native.PayloadBytesCompleted,
            native.Seconds,
            native.PacketsCompleted / native.Seconds,
            native.PayloadBytesCompleted / native.Seconds / 1e9,
            detail);
    }
}
