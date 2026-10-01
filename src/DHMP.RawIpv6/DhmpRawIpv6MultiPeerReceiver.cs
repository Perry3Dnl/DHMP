using System.Net;
using System.Net.Sockets;

namespace DHMP.RawIpv6;

/// <summary>
/// Linux raw-IPv6 receive loop that routes one data protocol binding across
/// multiple explicitly registered source IPv6 peers.
/// </summary>
public sealed class DhmpRawIpv6MultiPeerReceiver :
    IDisposable
{
    private readonly Socket _socket;
    private readonly DhmpRawIpv6PeerRouter _router;
    private readonly byte[] _networkBuffer;
    private readonly byte[] _plaintextScratch;

    private int _running;
    private int _disposed;
    private long _truncatedPackets;

    public DhmpRawIpv6MultiPeerReceiver(
        DhmpRawIpv6ListenerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureSupportedPlatform();

        _router =
            new DhmpRawIpv6PeerRouter(
                options.MaximumPeers,
                options.MaximumPayloadBytes);

        _networkBuffer =
            GC.AllocateUninitializedArray<byte>(
                options.MaximumPayloadBytes);

        _plaintextScratch =
            GC.AllocateUninitializedArray<byte>(
                options.MaximumPayloadBytes);

        _socket = DhmpLinuxRawIpv6Socket.Open(options.DataProtocolNumber);

        try
        {
            _socket.ReceiveBufferSize =
                options.SocketBufferBytes;

            _socket.Bind(
                new IPEndPoint(
                    options.LocalAddress,
                    0));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public DhmpRawIpv6PeerRouter Router =>
        _router;

    public long TruncatedPackets =>
        Interlocked.Read(
            ref _truncatedPackets);

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException(
                "This raw IPv6 multi-peer receiver is already running.");

        try
        {
            EndPoint remoteTemplate =
                new IPEndPoint(
                    IPAddress.IPv6Any,
                    0);

            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveMessageFromResult result;

                try
                {
                    result =
                        await _socket.ReceiveMessageFromAsync(
                            _networkBuffer.AsMemory(),
                            SocketFlags.None,
                            remoteTemplate,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if ((result.SocketFlags &
                     SocketFlags.Truncated) != 0)
                {
                    Interlocked.Increment(
                        ref _truncatedPackets);
                    continue;
                }

                if (result.RemoteEndPoint is not IPEndPoint peer)
                    continue;

                _router.TryRoute(
                    peer.Address,
                    _networkBuffer.AsSpan(
                        0,
                        result.ReceivedBytes),
                    _plaintextScratch);
            }
        }
        finally
        {
            Volatile.Write(
                ref _running,
                0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) == 0)
            _socket.Dispose();
    }

    private static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "The first DHMP raw IPv6 multi-peer backend is Linux-only.");
    }
}

