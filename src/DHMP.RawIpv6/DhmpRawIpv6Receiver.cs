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

    /// <summary>
    /// Open a Linux raw-IPv6 receiver for one configured peer. The receiver owns its socket but does
    /// not own or dispose the supplied server facade or optional packet decoder.
    /// </summary>
    public DhmpRawIpv6Receiver(
        DhmpRawIpv6Options options,
        DhmpServer server,
        IDhmpPacketDecoder? decoder = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(server);
        EnsureSupportedPlatform();
        options.EnsureExperimentalProtocolNumbersEnabled();

        if (decoder is null && !options.UnprotectedPayloadsAllowed)
            throw new InvalidOperationException(
                "Unprotected DHMP receive is disabled by default because plaintext V1 has no protocol-owned end-to-end integrity/authentication check. " +
                "Use a packet decoder such as DHMP.Security, or set allowUnprotectedPayloads: true for an explicitly accepted plaintext path.");

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

    /// <summary>Number of packets successfully decoded, validated and published.</summary>
    public long AcceptedPackets =>
        Interlocked.Read(ref _acceptedPackets);

    /// <summary>Number of malformed, truncated or protocol-invalid packets rejected.</summary>
    public long RejectedPackets =>
        Interlocked.Read(ref _rejectedPackets);

    /// <summary>Number of packets ignored because their source address was not the configured peer.</summary>
    public long ForeignPeerPackets =>
        Interlocked.Read(ref _foreignPeerPackets);

    /// <summary>Number of packets rejected by the configured protection decoder.</summary>
    public long ProtectionRejectedPackets =>
        Interlocked.Read(ref _protectionRejectedPackets);

    /// <summary>
    /// Run the single receive loop until cancellation. Published spans are borrowed and valid only
    /// for the synchronous duration of the callback.
    /// </summary>
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

                bool accepted = DhmpRawIpv6PayloadProcessor.TryProcess(_server, _decoder,
                    _buffer.AsSpan(0, result.ReceivedBytes), _plaintextBuffer,
                    publishBatch, out bool protectionRejected);
                if (!accepted)
                {
                    if (protectionRejected)
                        Interlocked.Increment(ref _protectionRejectedPackets);
                    else
                        Interlocked.Increment(ref _rejectedPackets);
                    continue;
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

    /// <summary>Close the owned raw socket; callers should cancel and join an active receive loop first.</summary>
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

