using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DHMP.RawIpv6;

/// <summary>
/// Experimental connected Linux sendmmsg submission. Each entry is one
/// independent headerless DHMP IPv6 packet. Never retries or queues packets.
/// </summary>
internal static unsafe class DhmpLinuxSendMmsg
{
    internal const int MaximumBatchSize = 32;
    private const int MsgDontWait = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct Iovec
    {
        public void* Base;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msghdr
    {
        public void* Name;
        public uint NameLength;
        public Iovec* Iov;
        public nuint IovLength;
        public void* Control;
        public nuint ControlLength;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Mmsghdr
    {
        public Msghdr Header;
        public uint MessageLength;
    }

    [DllImport("libc", EntryPoint = "sendmmsg", SetLastError = true)]
    private static extern int SendMmsg(int fd, Mmsghdr* messages, uint count, int flags);

    /// <returns>Number of packets accepted by the kernel. Partial success is never retried.</returns>
    internal static int Submit(Socket socket, ReadOnlyMemory<byte>[] packets, int offset, int count)
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("Experimental sendmmsg requires 64-bit Linux.");

        // Stack memory is safe here because sendmmsg submits synchronously.
        Iovec* iov = stackalloc Iovec[count];
        Mmsghdr* messages = stackalloc Mmsghdr[count];
        MemoryHandle[] pins = ArrayPool<MemoryHandle>.Shared.Rent(count);
        int pinned = 0;
        SafeSocketHandle safeHandle = socket.SafeHandle;
        bool addedRef = false;

        try
        {
            for (int i = 0; i < count; i++)
            {
                pins[i] = packets[offset + i].Pin();
                pinned++;
                iov[i] = new Iovec { Base = pins[i].Pointer, Length = (nuint)packets[offset + i].Length };
                messages[i] = new Mmsghdr
                {
                    Header = new Msghdr { Iov = &iov[i], IovLength = 1 }
                };
            }

            // Retain descriptor ownership across a concurrent Dispose.
            safeHandle.DangerousAddRef(ref addedRef);
            int result = SendMmsg((int)safeHandle.DangerousGetHandle(), messages, (uint)count, MsgDontWait);
            if (result >= 0)
            {
                for (int i = 0; i < result; i++)
                    if (messages[i].MessageLength != packets[offset + i].Length)
                        throw new IOException("Linux sendmmsg accepted an incomplete DHMP packet.");
                return result;
            }

            int errno = Marshal.GetLastPInvokeError();
            // EAGAIN/EWOULDBLOCK: explicitly report zero accepted, no spin/retry.
            if (errno == 11) return 0;
            throw new SocketException(errno);
        }
        finally
        {
            if (addedRef) safeHandle.DangerousRelease();
            for (int i = 0; i < pinned; i++) pins[i].Dispose();
            ArrayPool<MemoryHandle>.Shared.Return(pins, clearArray: true);
        }
    }
}
