using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// Linux raw-IPv6 implementation of the DHMP packet-sender boundary.
/// The IPv6 kernel API owns the IPv6 header; DHMP supplies payload bytes only.
/// The native socket is configured not to insert IPv6 Fragment headers.
/// </summary>
public sealed class DhmpRawIpv6PacketSender : IDhmpPacketSender, IDisposable
{
    private readonly Socket _socket;
    private readonly EndPoint _remoteEndPoint;
    private int _disposed;

    public DhmpRawIpv6PacketSender(DhmpRawIpv6Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureSupportedPlatform();
        options.EnsureExperimentalProtocolNumbersEnabled();

        MaximumPayloadBytes = options.MaximumPayloadBytes;
        _remoteEndPoint = new IPEndPoint(options.RemoteAddress, 0);

        _socket = DhmpLinuxRawIpv6Socket.Open(options.DataProtocolNumber);

        try
        {
            _socket.SendBufferSize = options.SocketBufferBytes;
            _socket.Bind(new IPEndPoint(options.LocalAddress, 0));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }
    }

    public int MaximumPayloadBytes { get; }

    public async ValueTask SendPacketAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (payload.IsEmpty || payload.Length > MaximumPayloadBytes)
            throw new DhmpProtocolException(
                "Raw IPv6 sender received an empty or oversized DHMP packet payload.");

        int sent;

        try
        {
            sent = await _socket.SendToAsync(
                payload,
                SocketFlags.None,
                _remoteEndPoint,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException error)
            when (error.SocketErrorCode == SocketError.MessageSize)
        {
            throw new DhmpPathMtuException(
                payload.Length,
                MaximumPayloadBytes,
                error);
        }

        if (sent != payload.Length)
            throw new IOException(
                $"Raw IPv6 socket accepted {sent} of {payload.Length} DHMP payload bytes.");
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

