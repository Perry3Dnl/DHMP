using System.Net.Sockets;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// Result of checking whether the current host can open the native DHMP raw-IPv6
/// data and control sockets.
/// </summary>
public sealed record DhmpRawIpv6HostProbeResult(
    DhmpRawIpv6HostProbeStatus Status,
    string Message)
{
    /// <summary>
    /// True when both experimental DHMP raw-IPv6 socket bindings opened successfully.
    /// </summary>
    public bool IsReady =>
        Status == DhmpRawIpv6HostProbeStatus.Ready;
}

/// <summary>
/// Local host capability reported by <see cref="DhmpRawIpv6HostProbe"/>.
/// </summary>
public enum DhmpRawIpv6HostProbeStatus
{
    Ready = 0,
    UnsupportedOperatingSystem = 1,
    Ipv6Unavailable = 2,
    ExperimentalProtocolNumbersNotEnabled = 3,
    PermissionDenied = 4,
    SocketOpenFailed = 5
}

/// <summary>
/// Performs a side-effect-minimal local readiness check for the native Linux raw-IPv6 backend.
/// The probe opens and immediately closes the experimental data/control sockets; it does not
/// bind an application address, send traffic, or establish network-path reachability.
/// </summary>
public static class DhmpRawIpv6HostProbe
{
    public static DhmpRawIpv6HostProbeResult Probe(
        bool enableExperimentalProtocolNumbers = false)
        => ProbeCore(
            OperatingSystem.IsLinux(),
            Socket.OSSupportsIPv6,
            enableExperimentalProtocolNumbers,
            protocol => DhmpLinuxRawIpv6Socket.Open(protocol));

    internal static DhmpRawIpv6HostProbeResult ProbeCore(
        bool isLinux,
        bool ipv6Available,
        bool experimentalProtocolNumbersEnabled,
        Func<byte, IDisposable> openSocket)
    {
        ArgumentNullException.ThrowIfNull(openSocket);

        if (!isLinux)
        {
            return new DhmpRawIpv6HostProbeResult(
                DhmpRawIpv6HostProbeStatus.UnsupportedOperatingSystem,
                "The current DHMP native raw-IPv6 backend is Linux-only.");
        }

        if (!ipv6Available)
        {
            return new DhmpRawIpv6HostProbeResult(
                DhmpRawIpv6HostProbeStatus.Ipv6Unavailable,
                "The operating system does not report IPv6 socket support.");
        }

        if (!experimentalProtocolNumbersEnabled)
        {
            return new DhmpRawIpv6HostProbeResult(
                DhmpRawIpv6HostProbeStatus.ExperimentalProtocolNumbersNotEnabled,
                "DHMP raw IPv6 currently uses experimental Next Header values 253/254. " +
                "Set enableExperimentalProtocolNumbers: true only for an explicitly configured experiment.");
        }

        try
        {
            using IDisposable dataSocket =
                openSocket(
                    DhmpProtocol.ExperimentalIpv6DataNextHeader);

            using IDisposable controlSocket =
                openSocket(
                    DhmpProtocol.ExperimentalIpv6ControlNextHeader);

            return new DhmpRawIpv6HostProbeResult(
                DhmpRawIpv6HostProbeStatus.Ready,
                "The host opened DHMP experimental raw-IPv6 data/control sockets successfully. " +
                "This verifies local socket capability only; router, firewall, ISP and remote-path reachability are not tested.");
        }
        catch (UnauthorizedAccessException error)
        {
            return new DhmpRawIpv6HostProbeResult(
                DhmpRawIpv6HostProbeStatus.PermissionDenied,
                error.Message);
        }
        catch (SocketException error)
        {
            return new DhmpRawIpv6HostProbeResult(
                DhmpRawIpv6HostProbeStatus.SocketOpenFailed,
                $"The host could not open a DHMP raw-IPv6 socket: {error.Message}");
        }
        catch (IOException error)
        {
            return new DhmpRawIpv6HostProbeResult(
                DhmpRawIpv6HostProbeStatus.SocketOpenFailed,
                error.Message);
        }
    }
}
