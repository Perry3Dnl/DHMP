using System.Runtime.InteropServices;
using System.Text;

namespace DHMP.AfXdp;

internal static class DhmpAfXdpNative
{
    private const string Library = "dhmp_afxdp";
    private const int ErrorCapacity = 512;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeBenchmarkResult
    {
        public int Mode;
        public long PacketsCompleted;
        public long PayloadBytesCompleted;
        public double Seconds;
    }

    [DllImport(Library, EntryPoint = "dhmp_afxdp_probe", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ProbeNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string interfaceName,
        uint queueId,
        int preferZeroCopy,
        [Out] byte[] error,
        nuint errorCapacity,
        out int mode);

    [DllImport(Library, EntryPoint = "dhmp_afxdp_tx_benchmark", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BenchmarkNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string interfaceName,
        uint queueId,
        int payloadBytes,
        long packets,
        int preferZeroCopy,
        [Out] byte[] error,
        nuint errorCapacity,
        out NativeBenchmarkResult result);

    internal static bool TryProbe(
        string interfaceName,
        uint queueId,
        bool preferZeroCopy,
        out DhmpAfXdpMode mode,
        out string detail)
    {
        var error = new byte[ErrorCapacity];

        try
        {
            int status = ProbeNative(
                interfaceName,
                queueId,
                preferZeroCopy ? 1 : 0,
                error,
                (nuint)error.Length,
                out int nativeMode);

            mode = ToMode(nativeMode);
            detail = Decode(error);

            if (status == 0)
            {
                if (string.IsNullOrWhiteSpace(detail))
                    detail = $"AF_XDP {mode} mode initialized.";
                return true;
            }

            if (string.IsNullOrWhiteSpace(detail))
                detail = $"AF_XDP initialization failed with native status {status}.";
            return false;
        }
        catch (DllNotFoundException)
        {
            mode = DhmpAfXdpMode.Unavailable;
            detail = "Native AF_XDP helper libdhmp_afxdp.so was not found.";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            mode = DhmpAfXdpMode.Unavailable;
            detail = "Native AF_XDP helper is present but does not expose the expected ABI.";
            return false;
        }
    }

    internal static bool TryBenchmark(
        string interfaceName,
        uint queueId,
        int payloadBytes,
        long packets,
        bool preferZeroCopy,
        out NativeBenchmarkResult result,
        out string detail)
    {
        var error = new byte[ErrorCapacity];

        try
        {
            int status = BenchmarkNative(
                interfaceName,
                queueId,
                payloadBytes,
                packets,
                preferZeroCopy ? 1 : 0,
                error,
                (nuint)error.Length,
                out result);

            detail = Decode(error);

            if (status == 0)
            {
                if (string.IsNullOrWhiteSpace(detail))
                    detail = $"AF_XDP {ToMode(result.Mode)} transmit benchmark completed.";
                return true;
            }

            if (string.IsNullOrWhiteSpace(detail))
                detail = $"AF_XDP benchmark failed with native status {status}.";
            return false;
        }
        catch (DllNotFoundException)
        {
            result = default;
            detail = "Native AF_XDP helper libdhmp_afxdp.so was not found.";
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            result = default;
            detail = "Native AF_XDP helper is present but does not expose the expected ABI.";
            return false;
        }
    }

    internal static DhmpAfXdpMode ToMode(int mode) =>
        mode switch
        {
            1 => DhmpAfXdpMode.Copy,
            2 => DhmpAfXdpMode.ZeroCopy,
            _ => DhmpAfXdpMode.Unavailable
        };

    private static string Decode(byte[] bytes)
    {
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
            length = bytes.Length;

        return Encoding.UTF8.GetString(bytes, 0, length).Trim();
    }
}
