using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>Open an actual Linux raw IPv6 descriptor for the experimental DHMP binding.</summary>
public static class DhmpLinuxRawIpv6Socket
{
    // Linux native constants, not the managed ProtocolType translation table.
    private const int AfInet6 = 10;
    private const int SockRaw = 3;
    private const int SockCloseOnExec = 0x80000;

    public static Socket Open(byte protocolNumber)
    {
        if (protocolNumber is not DhmpProtocol.ExperimentalIpv6DataNextHeader and
            not DhmpProtocol.ExperimentalIpv6ControlNextHeader)
            throw new ArgumentOutOfRangeException(nameof(protocolNumber), "Only the explicit experimental DHMP bindings are supported.");
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Native DHMP raw IPv6 sockets are Linux-only.");

        int descriptor = NativeSocket(AfInet6, SockRaw | SockCloseOnExec, protocolNumber);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException($"Linux could not open the DHMP raw IPv6 socket (errno {error}).",
                new Win32Exception(error));
        }
        var handle = new SafeSocketHandle((IntPtr)descriptor, ownsHandle: true);
        try { return new Socket(handle); }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static extern int NativeSocket(int domain, int type, int protocol);
}
