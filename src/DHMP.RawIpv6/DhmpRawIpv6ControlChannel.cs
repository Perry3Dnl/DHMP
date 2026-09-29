using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// Linux raw-IPv6 control channel using the experimental DHMP control binding.
/// </summary>
public sealed class DhmpRawIpv6ControlChannel : IDisposable
{
    private readonly Socket _socket;
    private readonly EndPoint _remoteEndPoint;
    private readonly IPAddress _remoteAddress;
    private int _disposed;

    public DhmpRawIpv6ControlChannel(
        DhmpRawIpv6Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureSupportedPlatform();

        _remoteAddress = options.RemoteAddress;
        _remoteEndPoint =
            new IPEndPoint(
                options.RemoteAddress,
                0);

        _socket = new Socket(
            AddressFamily.InterNetworkV6,
            SocketType.Raw,
            (ProtocolType)options.ControlProtocolNumber);

        try
        {
            _socket.SendBufferSize =
                options.SocketBufferBytes;

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

    public async ValueTask SendAsync(
        DhmpControlMessage message,
        CancellationToken cancellationToken = default)
    {
        byte[] packet =
            new byte[DhmpProtocol.ControlPacketSize];

        DhmpControlCodec.Encode(
            message,
            packet);

        await SendPacketAsync(
            packet,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DhmpControlMessage> ReceiveAsync(
        CancellationToken cancellationToken = default)
    {
        byte[] packet =
            new byte[DhmpProtocol.ControlPacketSize];

        int received =
            await ReceivePacketAsync(
                packet,
                cancellationToken).ConfigureAwait(false);

        if (received != packet.Length ||
            !DhmpControlCodec.TryDecode(
                packet,
                out var message))
            throw new DhmpProtocolException(
                "Received malformed or unsupported DHMP control packet.");

        return message;
    }

    public async ValueTask SendPacketAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        cancellationToken.ThrowIfCancellationRequested();

        if (packet.IsEmpty)
            throw new ArgumentException(
                "Control packet cannot be empty.",
                nameof(packet));

        int sent =
            await _socket.SendToAsync(
                packet,
                SocketFlags.None,
                _remoteEndPoint,
                cancellationToken).ConfigureAwait(false);

        if (sent != packet.Length)
            throw new IOException(
                $"Raw IPv6 control socket accepted {sent} of {packet.Length} bytes.");
    }

    public async ValueTask<int> ReceivePacketAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (destination.IsEmpty)
            throw new ArgumentException(
                "Control receive buffer cannot be empty.",
                nameof(destination));

        EndPoint remoteTemplate =
            new IPEndPoint(
                IPAddress.IPv6Any,
                0);

        while (true)
        {
            SocketReceiveMessageFromResult result =
                await _socket.ReceiveMessageFromAsync(
                    destination,
                    SocketFlags.None,
                    remoteTemplate,
                    cancellationToken).ConfigureAwait(false);

            if (result.RemoteEndPoint is not IPEndPoint peer ||
                !peer.Address.Equals(_remoteAddress))
                continue;

            if ((result.SocketFlags &
                 SocketFlags.Truncated) != 0)
                throw new DhmpProtocolException(
                    "Received truncated DHMP control packet.");

            return result.ReceivedBytes;
        }
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
                "The first DHMP raw IPv6 control backend is Linux-only.");
    }
}
