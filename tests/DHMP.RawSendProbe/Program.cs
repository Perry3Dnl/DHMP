using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using DHMP.RawIpv6;

// Isolated Linux raw-IPv6 send research. No production backend is selected here.
var local = IPAddress.Parse("fd00::2");
var remote = IPAddress.Parse("fd00::1");
const int packetCount = 32768;
var rows = new List<object>();
foreach (int bytes in new[] { 16, 1408 })
{
    byte[] payload = GC.AllocateArray<byte>(bytes, pinned: true);
    payload.AsSpan().Fill(0x5a);
    using var receiver = DhmpLinuxRawIpv6Socket.Open(253);
    receiver.ReceiveBufferSize = 16 * 1024 * 1024;
    receiver.Bind(new IPEndPoint(remote, 0));
    receiver.ReceiveTimeout = 200;
    long received = 0, malformed = 0;
    using var stop = new CancellationTokenSource();
    Task drain = Task.Factory.StartNew(() => {
        byte[] buffer = new byte[65536];
        while (!stop.IsCancellationRequested)
        {
            try
            {
                int length = receiver.Receive(buffer.AsSpan());
                if (length != bytes || !buffer.AsSpan(0, length).SequenceEqual(payload))
                    Interlocked.Increment(ref malformed);
                Interlocked.Increment(ref received);
            }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock) { }
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    using var actual = new DhmpRawIpv6PacketSender(new DhmpRawIpv6Options(local, remote, 1408,
        socketBufferBytes: 16 * 1024 * 1024, enableExperimentalProtocolNumbers: true, allowUnprotectedPayloads: true));
    using var unconnected = DhmpLinuxRawIpv6Socket.Open(253);
    unconnected.SendBufferSize = 16 * 1024 * 1024;
    unconnected.Bind(new IPEndPoint(local, 0));
    using var connected = DhmpLinuxRawIpv6Socket.Open(253);
    connected.SendBufferSize = 16 * 1024 * 1024;
    connected.Bind(new IPEndPoint(local, 0));
    connected.Connect(new IPEndPoint(remote, 0));
    var endpoint = new IPEndPoint(remote, 0);
    var serializedEndpoint = endpoint.Serialize();
    using var native = new NativeSender(connected, payload);
    long asyncCalls = 0, immediate = 0;
    Action actualSend = () => {
        ValueTask pending = actual.SendPacketAsync(payload);
        asyncCalls++;
        if (pending.IsCompletedSuccessfully) immediate++;
        if (pending.IsCompleted) pending.GetAwaiter().GetResult();
        else pending.AsTask().GetAwaiter().GetResult();
    };
    Action sendToAsync = () => {
        ValueTask<int> pending = unconnected.SendToAsync(payload.AsMemory(), SocketFlags.None, endpoint);
        asyncCalls++;
        if (pending.IsCompletedSuccessfully) immediate++;
        if ((pending.IsCompleted ? pending.GetAwaiter().GetResult() : pending.AsTask().GetAwaiter().GetResult()) != bytes) throw new IOException("Partial packet.");
    };
    Action cachedAddressAsync = () => {
        ValueTask<int> pending = unconnected.SendToAsync(payload.AsMemory(), SocketFlags.None, serializedEndpoint);
        asyncCalls++;
        if (pending.IsCompletedSuccessfully) immediate++;
        if ((pending.IsCompleted ? pending.GetAwaiter().GetResult() : pending.AsTask().GetAwaiter().GetResult()) != bytes) throw new IOException("Partial packet.");
    };
    Action connectedAsync = () => {
        ValueTask<int> pending = connected.SendAsync(payload.AsMemory(), SocketFlags.None);
        asyncCalls++;
        if (pending.IsCompletedSuccessfully) immediate++;
        if ((pending.IsCompleted ? pending.GetAwaiter().GetResult() : pending.AsTask().GetAwaiter().GetResult()) != bytes) throw new IOException("Partial packet.");
    };
    var cases = new (string Name, int Batch, Action Send)[] {
        ("current DHMP raw sender", 1, actualSend),
        ("Socket.SendToAsync", 1, sendToAsync),
        ("cached SocketAddress SendToAsync", 1, cachedAddressAsync),
        ("connected Socket.SendAsync", 1, connectedAsync),
        ("Socket.SendTo Span synchronous", 1, () => { if (unconnected.SendTo(payload.AsSpan(), SocketFlags.None, endpoint) != bytes) throw new IOException("Partial packet."); }),
        ("connected Socket.Send Span synchronous", 1, () => { if (connected.Send(payload.AsSpan(), SocketFlags.None) != bytes) throw new IOException("Partial packet."); }),
        ("native send nonblocking", 1, native.SendOne),
        ("native sendmmsg 32", 32, native.SendBatch)
    };
    // Warm each candidate and drain before measuring. No pending native memory escapes a call.
    foreach (var test in cases)
    {
        long before = Interlocked.Read(ref received);
        for (int i = 0; i < packetCount / test.Batch; i++) test.Send();
        WaitForDrain(before + packetCount);
    }
    var samples = cases.Select(_ => new List<object>()).ToArray();
    for (int round = 0; round < 7; round++)
    for (int step = 0; step < cases.Length; step++)
    {
        int index = round % 2 == 0 ? step : cases.Length - step - 1;
        var test = cases[index];
        long before = Interlocked.Read(ref received);
        asyncCalls = immediate = 0;
        native.ResetCounters();
        var clock = Stopwatch.StartNew();
        for (int packet = 0; packet < packetCount; packet += test.Batch) test.Send();
        clock.Stop();
        long accepted = packetCount;
        bool allReceived = WaitForDrain(before + accepted);
        long delivered = Interlocked.Read(ref received) - before;
        samples[index].Add(new { nanosecondsPerAcceptedPacket = clock.Elapsed.TotalNanoseconds / accepted,
            acceptedPackets = accepted, receivedPackets = delivered, allReceived, asyncCalls,
            immediateCompletions = immediate, pendingAtObservation = asyncCalls - immediate,
            native.Syscalls, native.WouldBlock, native.PartialBatches });
    }
    for (int index = 0; index < cases.Length; index++)
        rows.Add(new { bytes, mode = cases[index].Name, batch = cases[index].Batch, samples = samples[index] });
    stop.Cancel();
    drain.GetAwaiter().GetResult();
    if (malformed != 0) throw new IOException($"Received {malformed} malformed packets.");

    bool WaitForDrain(long target)
    {
        var wait = Stopwatch.StartNew();
        while (Interlocked.Read(ref received) < target && wait.ElapsedMilliseconds < 1000) Thread.Yield();
        // If there was loss, allow the old queue to settle before the next sample.
        if (Interlocked.Read(ref received) < target) Thread.Sleep(20);
        return Interlocked.Read(ref received) == target;
    }
}
Console.WriteLine("RAW_SEND_PROBE=" + JsonSerializer.Serialize(new {
    runtime = RuntimeInformation.FrameworkDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
    scope = "Isolated raw IPv6 loopback; fixed unchanged DHMP records. Seven alternating-order samples after 32768 warmup packets per candidate. Sender elapsed measures kernel acceptance; receiver counts and full contents are verified separately. Native buffers/descriptors prepared once; native methods are research-only and not cancellation/disposal-compatible production replacements.", rows }));

unsafe sealed class NativeSender : IDisposable
{
    private readonly Socket _socket;
    private readonly byte[] _payload;
    private readonly int _fd;
    private bool _handleReference;
    private readonly Message* _messages;
    private readonly IoVector* _vectors;
    public long Syscalls { get; private set; }
    public long WouldBlock { get; private set; }
    public long PartialBatches { get; private set; }
    public NativeSender(Socket socket, byte[] payload)
    {
        if (IntPtr.Size != 8 || sizeof(Message) != 64 || sizeof(MessageHeader) != 56)
            throw new PlatformNotSupportedException("Probe uses the Linux 64-bit msghdr layout.");
        _socket = socket;
        _payload = payload;
        socket.SafeHandle.DangerousAddRef(ref _handleReference);
        _fd = checked((int)socket.SafeHandle.DangerousGetHandle());
        _messages = (Message*)NativeMemory.AllocZeroed(32, (nuint)sizeof(Message));
        _vectors = (IoVector*)NativeMemory.AllocZeroed(32, (nuint)sizeof(IoVector));
        fixed (byte* data = payload)
        {
            for (int i = 0; i < 32; i++)
            {
                _vectors[i].Base = data;
                _vectors[i].Length = (nuint)payload.Length;
                _messages[i].Header.Io = &_vectors[i];
                _messages[i].Header.IoCount = 1;
            }
        }
    }
    public void ResetCounters() { Syscalls = WouldBlock = PartialBatches = 0; }
    public void SendOne()
    {
        fixed (byte* data = _payload)
        {
            for (int attempts = 0; ; attempts++)
            {
                Syscalls++;
                nint sent = Send(_fd, data, (nuint)_payload.Length, 0x40 | 0x4000);
                if (sent == _payload.Length) return;
                if (sent >= 0) throw new IOException("Partial native packet.");
                int error = Marshal.GetLastPInvokeError();
                if (error == 4) continue; // EINTR: packet not accepted.
                if (error != 11 || attempts > 100000) throw new IOException($"send errno {error}");
                WouldBlock++;
                Thread.Yield();
            }
        }
    }
    public void SendBatch()
    {
        int offset = 0, attempts = 0;
        while (offset < 32)
        {
            Syscalls++;
            int accepted = SendMessages(_fd, _messages + offset, (uint)(32 - offset), 0x40 | 0x4000);
            if (accepted > 0)
            {
                if (accepted != 32 - offset) PartialBatches++;
                for (int i = offset; i < offset + accepted; i++)
                    if (_messages[i].Length != _payload.Length) throw new IOException("Partial native batch packet.");
                offset += accepted;
                continue;
            }
            int error = Marshal.GetLastPInvokeError();
            if (accepted < 0 && error == 4) continue;
            if (accepted == 0 || error != 11 || attempts++ > 100000) throw new IOException($"sendmmsg errno {error}");
            WouldBlock++;
            Thread.Yield();
        }
    }
    public void Dispose()
    {
        NativeMemory.Free(_messages);
        NativeMemory.Free(_vectors);
        if (_handleReference) { _socket.SafeHandle.DangerousRelease(); _handleReference = false; }
        GC.KeepAlive(_payload);
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoVector { public void* Base; public nuint Length; }
    [StructLayout(LayoutKind.Sequential)] private struct MessageHeader {
        public void* Name; public uint NameLength; public IoVector* Io; public nuint IoCount;
        public void* Control; public nuint ControlLength; public int Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public MessageHeader Header; public uint Length; }
    [DllImport("libc", EntryPoint = "send", SetLastError = true)] private static extern nint Send(int fd, byte* data, nuint length, int flags);
    [DllImport("libc", EntryPoint = "sendmmsg", SetLastError = true)] private static extern int SendMessages(int fd, Message* messages, uint count, int flags);
}
