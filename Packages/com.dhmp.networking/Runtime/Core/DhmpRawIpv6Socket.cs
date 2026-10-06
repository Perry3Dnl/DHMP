using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DHMP.Unity
{
    public interface IDhmpPacketSocket : IDisposable
    {
        bool Send(IPAddress peer, byte[] payload, int length);
        bool TryReceive(byte[] buffer, out IPAddress peer, out int length);
    }

    // Native creation avoids managed ProtocolType translation for experimental 253/254.
    // Nonblocking, single-owner sockets: Unity polls on its main thread; no worker touches Unity objects.
    public sealed class DhmpRawIpv6Socket : IDhmpPacketSocket
    {
        private readonly NativeHandle handle;
        private readonly bool windows;
        private readonly byte[] address = new byte[28];
        private bool disposed;

        public DhmpRawIpv6Socket(IPAddress localAddress, byte protocol, bool experimental, bool allowPlaintext)
        {
            ValidateAddress(localAddress);
            if (protocol != DhmpWire.DataProtocol && protocol != DhmpWire.ControlProtocol)
                throw new ArgumentOutOfRangeException(nameof(protocol));
            if (!experimental || !allowPlaintext)
                throw new InvalidOperationException("This preview requires explicit experimental IPv6 and unprotected demo-payload opt-ins.");
            windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            if (!windows && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                throw new PlatformNotSupportedException("This DHMP preview implements Windows and Linux desktop raw IPv6 only.");
            IntPtr socket;
            if (windows)
            {
                int startup = WsaStartup(0x202, new byte[512]);
                if (startup != 0) throw new IOException("Winsock initialization failed: " + startup);
                socket = WinSocket(23, 3, protocol);
                if (socket == new IntPtr(-1)) { int error = WsaError(); WsaCleanup(); throw NativeError(error, true); }
            }
            else
            {
                int descriptor = LinuxSocket(10, 3 | 0x800 | 0x80000, protocol); // NONBLOCK, CLOEXEC
                if (descriptor < 0) throw NativeError(Marshal.GetLastWin32Error(), false);
                socket = new IntPtr(descriptor);
            }
            handle = new NativeHandle(socket, windows);
            try
            {
                int enabled = 1;
                int optionResult = windows
                    ? WinSetOption(socket, 41, 14, ref enabled, 4) // IPV6_DONTFRAG
                    : LinuxSetOption(socket.ToInt32(), 41, 62, ref enabled, 4);
                if (optionResult != 0) throw NativeError(LastError(), windows);
                if (windows)
                {
                    uint nonblocking = 1;
                    if (WinIoctl(socket, 0x8004667e, ref nonblocking) != 0) throw NativeError(WsaError(), true);
                }
                EncodeAddress(localAddress, address, windows);
                int bound = windows ? WinBind(socket, address, 28) : LinuxBind(socket.ToInt32(), address, 28);
                if (bound != 0) throw NativeError(LastError(), windows);
            }
            catch { handle.Dispose(); throw; }
        }

        public bool Send(IPAddress peer, byte[] payload, int length)
        {
            ThrowIfDisposed(); ValidateAddress(peer);
            if (payload == null || length <= 0 || length > payload.Length || length > DhmpWire.MaximumPayload)
                throw new ArgumentException("A complete packet within the conservative IPv6 payload budget is required.");
            EncodeAddress(peer, address, windows);
            IntPtr socket = handle.DangerousGetHandle();
            long sent = windows ? WinSend(socket, payload, length, 0, address, 28)
                : LinuxSend(socket.ToInt32(), payload, new UIntPtr((uint)length), 0, address, 28).ToInt64();
            if (sent < 0)
            {
                int error = LastError();
                if (WouldBlock(error)) return false;
                throw NativeError(error, windows);
            }
            if (sent != length) throw new IOException("The raw socket did not accept the complete DHMP packet.");
            return true;
        }

        public bool TryReceive(byte[] buffer, out IPAddress peer, out int length)
        {
            ThrowIfDisposed();
            if (buffer == null || buffer.Length < 65536)
                throw new ArgumentException("Receive into a full IPv6 payload buffer, then reject oversized packets without truncation.");
            peer = null; length = 0;
            int addressLength = 28;
            IntPtr socket = handle.DangerousGetHandle();
            long received = windows ? WinReceive(socket, buffer, buffer.Length, 0, address, ref addressLength)
                : LinuxReceive(socket.ToInt32(), buffer, new UIntPtr((uint)buffer.Length), 0, address, ref addressLength).ToInt64();
            if (received < 0)
            {
                int error = LastError();
                if (WouldBlock(error) || error == (windows ? 10040 : 4)) return false;
                throw NativeError(error, windows);
            }
            if (addressLength < 28) throw new IOException("Raw socket returned an incomplete IPv6 source address.");
            var ip = new byte[16]; Array.Copy(address, 8, ip, 0, 16);
            peer = new IPAddress(ip, BitConverter.ToUInt32(address, 24));
            length = checked((int)received);
            return true;
        }

        public static void ValidateAddress(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv4MappedToIPv6 ||
                address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast)
                throw new ArgumentException("Use an explicit native unicast IPv6 address; DHMP has no transport ports.");
        }
        private static void EncodeAddress(IPAddress ip, byte[] bytes, bool win)
        {
            Array.Clear(bytes, 0, bytes.Length);
            byte[] family = BitConverter.GetBytes((ushort)(win ? 23 : 10));
            Array.Copy(family, bytes, 2); Array.Copy(ip.GetAddressBytes(), 0, bytes, 8, 16);
            Array.Copy(BitConverter.GetBytes(checked((uint)ip.ScopeId)), 0, bytes, 24, 4);
        }
        private int LastError() => windows ? WsaError() : Marshal.GetLastWin32Error();
        private bool WouldBlock(int error) => windows ? error == 10035 : error == 11;
        private static Exception NativeError(int error, bool win)
        {
            if (error == (win ? 10013 : 1) || (!win && error == 13))
                return new UnauthorizedAccessException(win
                    ? "Windows raw IPv6 requires an elevated process. This is an experimental desktop build."
                    : "Linux raw IPv6 requires CAP_NET_RAW on the dedicated executable.");
            return new IOException("DHMP raw IPv6 socket error " + error + (win ? " (Winsock)." : " (Linux errno)."));
        }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(DhmpRawIpv6Socket)); }
        public void Dispose() { if (disposed) return; disposed = true; handle.Dispose(); }

        private sealed class NativeHandle : SafeHandle
        {
            private readonly bool windows;
            public NativeHandle(IntPtr value, bool win) : base(new IntPtr(-1), true) { windows = win; SetHandle(value); }
            public override bool IsInvalid => handle == new IntPtr(-1);
            protected override bool ReleaseHandle()
            {
                if (windows) { int closed = WinClose(handle); WsaCleanup(); return closed == 0; }
                return LinuxClose(handle.ToInt32()) == 0;
            }
        }

        [DllImport("libc", EntryPoint = "socket", SetLastError = true)] private static extern int LinuxSocket(int family, int type, int protocol);
        [DllImport("libc", EntryPoint = "bind", SetLastError = true)] private static extern int LinuxBind(int socket, byte[] address, uint length);
        [DllImport("libc", EntryPoint = "setsockopt", SetLastError = true)] private static extern int LinuxSetOption(int socket, int level, int option, ref int value, uint length);
        [DllImport("libc", EntryPoint = "sendto", SetLastError = true)] private static extern IntPtr LinuxSend(int socket, byte[] buffer, UIntPtr size, int flags, byte[] address, uint length);
        [DllImport("libc", EntryPoint = "recvfrom", SetLastError = true)] private static extern IntPtr LinuxReceive(int socket, [Out] byte[] buffer, UIntPtr size, int flags, [Out] byte[] address, ref int length);
        [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int LinuxClose(int socket);
        [DllImport("ws2_32.dll", EntryPoint = "WSAStartup")] private static extern int WsaStartup(ushort version, [Out] byte[] data);
        [DllImport("ws2_32.dll", EntryPoint = "WSACleanup")] private static extern int WsaCleanup();
        [DllImport("ws2_32.dll", EntryPoint = "WSAGetLastError")] private static extern int WsaError();
        [DllImport("ws2_32.dll", EntryPoint = "socket")] private static extern IntPtr WinSocket(int family, int type, int protocol);
        [DllImport("ws2_32.dll", EntryPoint = "bind")] private static extern int WinBind(IntPtr socket, byte[] address, int length);
        [DllImport("ws2_32.dll", EntryPoint = "setsockopt")] private static extern int WinSetOption(IntPtr socket, int level, int option, ref int value, int length);
        [DllImport("ws2_32.dll", EntryPoint = "ioctlsocket")] private static extern int WinIoctl(IntPtr socket, uint command, ref uint value);
        [DllImport("ws2_32.dll", EntryPoint = "sendto")] private static extern int WinSend(IntPtr socket, byte[] buffer, int size, int flags, byte[] address, int length);
        [DllImport("ws2_32.dll", EntryPoint = "recvfrom")] private static extern int WinReceive(IntPtr socket, [Out] byte[] buffer, int size, int flags, [Out] byte[] address, ref int length);
        [DllImport("ws2_32.dll", EntryPoint = "closesocket")] private static extern int WinClose(IntPtr socket);
    }
}
