using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// Open an actual Linux raw IPv6 descriptor for an explicitly selected experimental DHMP binding.
/// This low-level API does not imply that 253/254 are permanent DHMP assignments or Internet-routable.
/// </summary>
public static class DhmpLinuxRawIpv6Socket
{
    // Linux native constants, not the managed ProtocolType translation table.
    private const int AfInet6 = 10;
    private const int SockRaw = 3;
    private const int SockCloseOnExec = 0x80000;
    private const int IpProtocolIpv6 = 41;
    // Linux UAPI include/uapi/linux/in6.h; RFC 3542 section 11.2.
    private const int Ipv6DontFragment = 62;

    public static Socket Open(byte protocolNumber)
    {
        if (protocolNumber is not DhmpProtocol.ExperimentalIpv6DataNextHeader and
            not DhmpProtocol.ExperimentalIpv6ControlNextHeader)
            throw new ArgumentOutOfRangeException(nameof(protocolNumber),
                "Only the explicitly selected experimental DHMP bindings 253/254 are supported.");
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Native DHMP raw IPv6 sockets are Linux-only.");

        int descriptor = NativeSocket(AfInet6, SockRaw | SockCloseOnExec, protocolNumber);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            var nativeError = new Win32Exception(error);

            if (error is 1 or 13)
                throw new UnauthorizedAccessException(
                    "Linux denied the DHMP raw IPv6 socket. The process needs raw-socket permission, normally CAP_NET_RAW (or an equivalently privileged execution context).",
                    nativeError);

            throw new IOException(
                $"Linux could not open the DHMP raw IPv6 socket (errno {error}).",
                nativeError);
        }
        var handle = new SafeSocketHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            int enabled = 1;
            if (NativeSetSocketOption(
                    descriptor,
                    IpProtocolIpv6,
                    Ipv6DontFragment,
                    ref enabled,
                    sizeof(int)) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    $"Linux could not disable IPv6 source fragmentation for the DHMP raw socket (errno {error}).",
                    new Win32Exception(error));
            }

            return new Socket(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static extern int NativeSocket(int domain, int type, int protocol);

    [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)]
    private static extern int NativeSetSocketOption(
        int socket,
        int level,
        int optionName,
        ref int optionValue,
        uint optionLength);
}
