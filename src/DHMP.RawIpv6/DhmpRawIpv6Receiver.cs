using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;
using DHMP.Server;

namespace DHMP.RawIpv6;

/// <summary>
/// Linux raw-IPv6 receive loop for one explicitly configured DHMP peer.
/// Optional packet protection is decoded before headerless V1 validation.
/// </summary>
public sealed class DhmpRawIpv6Receiver : IDisposable
{
    private readonly Socket _socket;
    private readonly DhmpServer _server;
    private readonly IPAddress _remoteAddress;
    private readonly IDhmpPacketDecoder? _decoder;
    private readonly byte[] _buffer;
    private readonly byte[]? _plaintextBuffer;

    private int _running;
    private int _disposed;
    private long _acceptedPackets;
    private long _rejectedPackets;
    private long _foreignPeerPackets;
    private long _protectionRejectedPackets;

    public DhmpRawIpv6Receiver(
        DhmpRawIpv6Options options,
        DhmpServer server,
        IDhmpPacketDecoder? decoder = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(server);
        EnsureSupportedPlatform();

        if (decoder is not null &&
            decoder.OverheadBytes < 0)
            throw new ArgumentException(
                "Packet decoder overhead cannot be negative.",
                nameof(decoder));

        int requiredNetworkPayload =
            decoder is null
                ? server.ReceivePolicy.MaximumPayloadBytes
                : checked(
                    server.ReceivePolicy.MaximumPayloadBytes +
                    decoder.OverheadBytes);

        if (requiredNetworkPayload >
            options.MaximumPayloadBytes)
            throw new ArgumentException(
                "DHMP receive policy plus packet-protection overhead exceeds the raw IPv6 backend limit.",
                nameof(server));

        _server = server;
        _decoder = decoder;
        _remoteAddress = options.RemoteAddress;
        _buffer =
            GC.AllocateUninitializedArray<byte>(
                options.MaximumPayloadBytes);

        if (decoder is not null)
        {
            _plaintextBuffer =
                GC.AllocateUninitializedArray<byte>(
                    server.ReceivePolicy.MaximumPayloadBytes);
        }

        _socket = new Socket(
            AddressFamily.InterNetworkV6,
            SocketType.Raw,
            (ProtocolType)options.DataProtocolNumber);

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

    public long AcceptedPackets =>
        Interlocked.Read(ref _acceptedPackets);

    public long RejectedPackets =>
        Interlocked.Read(ref _rejectedPackets);

    public long ForeignPeerPackets =>
        Interlocked.Read(ref _foreignPeerPackets);

    public long ProtectionRejectedPackets =>
        Interlocked.Read(ref _protectionRejectedPackets);

    public async Task RunAsync(
        Action<ReadOnlySpan<byte>> publishBatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(publishBatch);

        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException(
                "This raw IPv6 receiver is already running.");

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
                            _buffer.AsMemory(),
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
                        ref _rejectedPackets);
                    continue;
                }

                if (result.RemoteEndPoint is not IPEndPoint peer ||
                    !peer.Address.Equals(_remoteAddress))
                {
                    Interlocked.Increment(
                        ref _foreignPeerPackets);
                    continue;
                }

                ReadOnlySpan<byte> payload =
                    _buffer.AsSpan(
                        0,
                        result.ReceivedBytes);

                if (_decoder is not null)
                {
                    if (!_decoder.TryDecode(
                            payload,
                            _plaintextBuffer!,
                            out int plaintextBytes))
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                            _plaintextBuffer!);

                        Interlocked.Increment(
                            ref _protectionRejectedPackets);

                        continue;
                    }

                    if (plaintextBytes <= 0 ||
                        plaintextBytes >
                            _server.ReceivePolicy.MaximumPayloadBytes ||
                        plaintextBytes >
                            _plaintextBuffer!.Length)
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                            _plaintextBuffer);

                        Interlocked.Increment(
                            ref _protectionRejectedPackets);
                        continue;
                    }

                    payload =
                        _plaintextBuffer.AsSpan(
                            0,
                            plaintextBytes);
                }

                if (!IsValidPacketLength(payload.Length))
                {
                    Interlocked.Increment(
                        ref _rejectedPackets);
                    continue;
                }

                if (_decoder is null)
                {
                    _server.ProcessPacket(
                        payload,
                        publishBatch);
                }
                else
                {
                    try
                    {
                        _server.ProcessPacket(
                            payload,
                            publishBatch);
                    }
                    finally
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(
                            _plaintextBuffer!.AsSpan(
                                0,
                                payload.Length));
                    }
                }

                Interlocked.Increment(
                    ref _acceptedPackets);
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
               length <=
                   _server.ReceivePolicy.MaximumPayloadBytes &&
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
