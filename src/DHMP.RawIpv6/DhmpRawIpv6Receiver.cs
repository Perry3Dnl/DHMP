using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;
using DHMP.Server;

namespace DHMP.RawIpv6;

/// <summary>
/// Linux raw-IPv6 receive loop for one explicitly configured DHMP peer/session.
/// Raw IPv6 delivers the protocol payload, not the base IPv6 header.
/// </summary>
public sealed class DhmpRawIpv6Receiver : IDisposable
{
    private readonly Socket _socket;
    private readonly DhmpServer _server;
    private readonly IPAddress _remoteAddress;
    private readonly byte[] _buffer;
    private int _running;
    private int _disposed;
    private long _acceptedPackets;
    private long _rejectedPackets;
    private long _foreignPeerPackets;

    public DhmpRawIpv6Receiver(
        DhmpRawIpv6Options options,
        DhmpServer server)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(server);
        EnsureSupportedPlatform();

        if (server.ReceivePolicy.MaximumPayloadBytes > options.MaximumPayloadBytes)
            throw new ArgumentException(
                "DHMP session payload limit exceeds the raw IPv6 backend limit.",
                nameof(server));

        _server = server;
        _remoteAddress = options.RemoteAddress;
        _buffer = GC.AllocateUninitializedArray<byte>(options.MaximumPayloadBytes);

        _socket = new Socket(
            AddressFamily.InterNetworkV6,
            SocketType.Raw,
            (ProtocolType)options.DataProtocolNumber);

        try
        {
            _socket.ReceiveBufferSize = options.SocketBufferBytes;
            _socket.Bind(new IPEndPoint(options.LocalAddress, 0));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public long AcceptedPackets => Interlocked.Read(ref _acceptedPackets);
    public long RejectedPackets => Interlocked.Read(ref _rejectedPackets);
    public long ForeignPeerPackets => Interlocked.Read(ref _foreignPeerPackets);

    public async Task RunAsync(
        Action<ReadOnlySpan<byte>> publishBatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publishBatch);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("This raw IPv6 receiver is already running.");

        try
        {
            EndPoint remoteTemplate = new IPEndPoint(IPAddress.IPv6Any, 0);

            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveMessageFromResult result;

                try
                {
                    result = await _socket.ReceiveMessageFromAsync(
                        _buffer.AsMemory(),
                        SocketFlags.None,
                        remoteTemplate,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if ((result.SocketFlags & SocketFlags.Truncated) != 0)
                {
                    Interlocked.Increment(ref _rejectedPackets);
                    continue;
                }

                if (result.RemoteEndPoint is not IPEndPoint peer ||
                    !peer.Address.Equals(_remoteAddress))
                {
                    Interlocked.Increment(ref _foreignPeerPackets);
                    continue;
                }

                if (!IsValidPacketLength(result.ReceivedBytes))
                {
                    Interlocked.Increment(ref _rejectedPackets);
                    continue;
                }

                _server.ProcessPacket(
                    _buffer.AsSpan(0, result.ReceivedBytes),
                    publishBatch);
                Interlocked.Increment(ref _acceptedPackets);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private bool IsValidPacketLength(int length)
    {
        var wire = _server.WireContract;
        return length > 0 &&
               length <= _server.ReceivePolicy.MaximumPayloadBytes &&
               length % wire.RecordSize == 0;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _socket.Dispose();
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "The first DHMP raw IPv6 backend is Linux-only. Other OS backends require separately validated socket semantics.");
    }
}
