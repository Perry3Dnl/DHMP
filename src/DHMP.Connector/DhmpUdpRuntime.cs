using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DHMP.Protocol;
using DHMP.Server;

namespace DHMP.Connector;

/// <summary>
/// Managed UDP compatibility backend. UDP is only an outer carrier: the DHMP
/// data payload remains the same complete fixed-size record used by V1.
/// </summary>
internal sealed class DhmpUdpRuntime : IDisposable
{
    private readonly DhmpConnectorOptions _options;
    private readonly Socket _dataSocket;
    private readonly Socket _controlSocket;
    private readonly object _bindingsGate = new();
    private readonly Dictionary<IPAddress, Registration[]> _bindings = new();
    private readonly Dictionary<IPAddress, IPEndPoint> _observedDataEndpoints = new();
    private readonly byte[] _networkBuffer;
    private readonly byte[] _plaintextScratch;

    private int _peerCount;
    private int _running;
    private int _disposed;
    private long _acceptedPackets;
    private long _rejectedPackets;
    private long _unknownPeerPackets;

    public DhmpUdpRuntime(DhmpConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;

        AddressFamily family =
            options.LocalAddress.AddressFamily;

        if (family is not AddressFamily.InterNetwork and
            not AddressFamily.InterNetworkV6)
            throw new ArgumentException(
                "UDP compatibility requires an IPv4 or IPv6 local address.",
                nameof(options));

        _dataSocket =
            new Socket(
                family,
                SocketType.Dgram,
                ProtocolType.Udp);

        _controlSocket =
            new Socket(
                family,
                SocketType.Dgram,
                ProtocolType.Udp);

        try
        {
            _dataSocket.ReceiveBufferSize =
                options.SocketBufferBytes;
            _dataSocket.SendBufferSize =
                options.SocketBufferBytes;
            _controlSocket.ReceiveBufferSize =
                options.SocketBufferBytes;
            _controlSocket.SendBufferSize =
                options.SocketBufferBytes;

            _dataSocket.Bind(
                new IPEndPoint(
                    options.LocalAddress,
                    options.UdpDataPort));

            _controlSocket.Bind(
                new IPEndPoint(
                    options.LocalAddress,
                    options.UdpControlPort));
        }
        catch
        {
            _dataSocket.Dispose();
            _controlSocket.Dispose();
            throw;
        }

        _networkBuffer =
            GC.AllocateUninitializedArray<byte>(
                options.UdpMaximumPayloadBytes);

        _plaintextScratch =
            GC.AllocateUninitializedArray<byte>(
                options.UdpMaximumPayloadBytes);
    }

    public int MaximumPayloadBytes =>
        _options.UdpMaximumPayloadBytes;

    public long AcceptedPackets =>
        Interlocked.Read(ref _acceptedPackets);

    public long RejectedPackets =>
        Interlocked.Read(ref _rejectedPackets);

    public long UnknownPeerPackets =>
        Interlocked.Read(ref _unknownPeerPackets);

    public DhmpUdpPacketSender CreateSender(
        IPAddress remoteAddress)
    {
        ValidateRemoteAddress(remoteAddress);

        return new DhmpUdpPacketSender(
            this,
            remoteAddress,
            MaximumPayloadBytes);
    }

    public DhmpUdpControlChannel CreateControlChannel(
        IPAddress remoteAddress)
    {
        ValidateRemoteAddress(remoteAddress);

        return new DhmpUdpControlChannel(
            _controlSocket,
            remoteAddress,
            _options.UdpControlPort);
    }

    public void Register(
        IPAddress remoteAddress,
        DhmpServer server,
        Action<ReadOnlySpan<byte>> publishBatch,
        IDhmpPacketDecoder? decoder,
        bool allowUnprotectedPayloads,
        ulong? connectionId,
        int? connectionIdOffset)
    {
        ValidateRemoteAddress(remoteAddress);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(publishBatch);

        if (decoder is null &&
            !allowUnprotectedPayloads)
            throw new InvalidOperationException(
                "Unprotected UDP-carried DHMP receive is disabled.");

        int maximumNetworkPayload =
            checked(
                server.ReceivePolicy.MaximumPayloadBytes +
                (decoder?.OverheadBytes ?? 0));

        if (maximumNetworkPayload >
            MaximumPayloadBytes)
            throw new ArgumentException(
                "Receive policy plus protection overhead exceeds the UDP compatibility payload ceiling.",
                nameof(server));

        if (connectionId.HasValue !=
            connectionIdOffset.HasValue)
            throw new ArgumentException(
                "ConnectionId and offset must both be supplied or both be omitted.");

        var registration =
            new Registration(
                new Binding(
                    remoteAddress,
                    server,
                    publishBatch,
                    decoder,
                    connectionId,
                    connectionIdOffset));

        lock (_bindingsGate)
        {
            if (_peerCount >= _options.MaximumPeers)
                throw new InvalidOperationException(
                    "DHMP UDP compatibility peer limit reached.");

            _bindings.TryGetValue(
                remoteAddress,
                out Registration[]? current);

            current ??= [];

            if (current.Length != 0)
            {
                if (connectionId is null ||
                    connectionIdOffset is null ||
                    current.Any(entry =>
                        entry.Binding.ConnectionId is null ||
                        entry.Binding.ConnectionIdOffset is null))
                {
                    throw new InvalidOperationException(
                        "UDP compatibility already has a session for this remote address. " +
                        "Duplicate-source sessions require the application-owned ConnectionId routing mode.");
                }

                if (current.Any(entry =>
                        entry.Binding.ConnectionId ==
                        connectionId))
                    throw new InvalidOperationException(
                        "A UDP compatibility session with this ConnectionId is already registered.");
            }

            var next =
                new Registration[
                    current.Length + 1];

            Array.Copy(
                current,
                next,
                current.Length);

            next[^1] =
                registration;

            _bindings[remoteAddress] =
                next;

            _peerCount++;
        }
    }

    public async Task RemoveAsync(
        IPAddress remoteAddress,
        ulong? connectionId)
    {
        ValidateRemoteAddress(remoteAddress);

        Registration[] removed;

        lock (_bindingsGate)
        {
            if (!_bindings.TryGetValue(
                    remoteAddress,
                    out Registration[]? current))
                return;

            if (connectionId is null)
            {
                removed = current;
                _bindings.Remove(remoteAddress);
                _observedDataEndpoints.Remove(remoteAddress);
            }
            else
            {
                int index =
                    Array.FindIndex(
                        current,
                        entry =>
                            entry.Binding.ConnectionId ==
                            connectionId);

                if (index < 0)
                    return;

                removed =
                [
                    current[index]
                ];

                if (current.Length == 1)
                {
                    _bindings.Remove(remoteAddress);
                    _observedDataEndpoints.Remove(remoteAddress);
                }
                else
                {
                    var next =
                        new Registration[
                            current.Length - 1];

                    if (index > 0)
                        Array.Copy(
                            current,
                            0,
                            next,
                            0,
                            index);

                    if (index < current.Length - 1)
                        Array.Copy(
                            current,
                            index + 1,
                            next,
                            index,
                            current.Length - index - 1);

                    _bindings[remoteAddress] =
                        next;
                }
            }

            foreach (Registration registration in removed)
                registration.Retire();

            _peerCount -= removed.Length;
        }

        await Task.WhenAll(
            removed.Select(
                registration =>
                    registration.Drained))
            .ConfigureAwait(false);
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (Interlocked.Exchange(
                ref _running,
                1) != 0)
            throw new InvalidOperationException(
                "The UDP compatibility receive loop is already running.");

        EndPoint remoteTemplate =
            _options.LocalAddress.AddressFamily ==
                AddressFamily.InterNetworkV6
                ? new IPEndPoint(
                    IPAddress.IPv6Any,
                    0)
                : new IPEndPoint(
                    IPAddress.Any,
                    0);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                SocketReceiveFromResult result;

                try
                {
                    result =
                        await _dataSocket.ReceiveFromAsync(
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

                if (result.RemoteEndPoint is not IPEndPoint peer)
                    continue;

                Registration[] registrations;

                lock (_bindingsGate)
                {
                    _observedDataEndpoints[peer.Address] =
                        peer;

                    if (!_bindings.TryGetValue(
                            peer.Address,
                            out Registration[]? current))
                    {
                        Interlocked.Increment(
                            ref _unknownPeerPackets);
                        continue;
                    }

                    registrations =
                        current;
                }

                Route(
                    registrations,
                    _networkBuffer.AsSpan(
                        0,
                        result.ReceivedBytes));
            }
        }
        finally
        {
            Volatile.Write(
                ref _running,
                0);
        }
    }

    internal async ValueTask SendDataAsync(
        IPAddress remoteAddress,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (payload.IsEmpty ||
            payload.Length >
            MaximumPayloadBytes)
            throw new DhmpProtocolException(
                "UDP compatibility sender received an empty or oversized DHMP payload.");

        EndPoint endpoint;

        lock (_bindingsGate)
        {
            endpoint =
                _observedDataEndpoints.TryGetValue(
                    remoteAddress,
                    out IPEndPoint? observed)
                    ? observed
                    : new IPEndPoint(
                        remoteAddress,
                        _options.UdpDataPort);
        }

        int sent =
            await _dataSocket.SendToAsync(
                payload,
                SocketFlags.None,
                endpoint,
                cancellationToken)
            .ConfigureAwait(false);

        if (sent != payload.Length)
            throw new IOException(
                $"UDP compatibility socket accepted {sent} of {payload.Length} DHMP payload bytes.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
            return;

        _dataSocket.Dispose();
        _controlSocket.Dispose();
    }

    private void Route(
        Registration[] registrations,
        ReadOnlySpan<byte> networkPayload)
    {
        bool sawRejection = false;

        foreach (Registration registration in registrations)
        {
            if (!registration.TryAcquire())
                continue;

            try
            {
                if (TryRoute(
                        registration.Binding,
                        networkPayload))
                {
                    Interlocked.Increment(
                        ref _acceptedPackets);
                    return;
                }

                sawRejection = true;
            }
            finally
            {
                registration.Release();
            }
        }

        if (sawRejection)
            Interlocked.Increment(
                ref _rejectedPackets);
    }

    private bool TryRoute(
        Binding binding,
        ReadOnlySpan<byte> networkPayload)
    {
        int recordSize =
            binding.Server.WireContract.RecordSize;

        if (binding.Decoder is null)
        {
            if (networkPayload.Length !=
                recordSize)
                return false;

            if (!MatchesConnectionId(
                    binding,
                    networkPayload))
                return false;

            binding.Server.ProcessNegotiatedRecord(
                networkPayload,
                binding.PublishBatch);

            return true;
        }

        int maximumPlaintext =
            binding.Server.ReceivePolicy.MaximumPayloadBytes;

        if (networkPayload.IsEmpty ||
            networkPayload.Length >
                checked(
                    maximumPlaintext +
                    binding.Decoder.OverheadBytes))
            return false;

        try
        {
            if (!binding.Decoder.TryDecode(
                    networkPayload,
                    _plaintextScratch,
                    out int plaintextBytes) ||
                plaintextBytes != recordSize)
                return false;

            ReadOnlySpan<byte> plaintext =
                _plaintextScratch[
                    ..plaintextBytes];

            if (!MatchesConnectionId(
                    binding,
                    plaintext))
                return false;

            binding.Server.ProcessNegotiatedRecord(
                plaintext,
                binding.PublishBatch);

            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                _plaintextScratch);
        }
    }

    private static bool MatchesConnectionId(
        Binding binding,
        ReadOnlySpan<byte> plaintext)
    {
        if (binding.ConnectionId is null)
            return true;

        int offset =
            binding.ConnectionIdOffset ??
            throw new InvalidOperationException(
                "ConnectionId routing requires a field offset.");

        return
            BinaryPrimitives.ReadUInt64BigEndian(
                plaintext.Slice(
                    offset,
                    sizeof(ulong))) ==
            binding.ConnectionId.Value;
    }

    private void ValidateRemoteAddress(
        IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (remoteAddress.AddressFamily !=
            _options.LocalAddress.AddressFamily ||
            remoteAddress.IsIPv4MappedToIPv6)
            throw new ArgumentException(
                "UDP compatibility requires a remote address in the configured local address family.",
                nameof(remoteAddress));
    }

    private sealed record Binding(
        IPAddress RemoteAddress,
        DhmpServer Server,
        Action<ReadOnlySpan<byte>> PublishBatch,
        IDhmpPacketDecoder? Decoder,
        ulong? ConnectionId,
        int? ConnectionIdOffset);

    private sealed class Registration(
        Binding binding)
    {
        private readonly TaskCompletionSource _drained =
            new(
                TaskCreationOptions.RunContinuationsAsynchronously);

        private int _activeRoutes;
        private int _retired;

        public Binding Binding { get; } =
            binding;

        public Task Drained =>
            _drained.Task;

        public bool TryAcquire()
        {
            if (Volatile.Read(
                    ref _retired) != 0)
                return false;

            Interlocked.Increment(
                ref _activeRoutes);

            if (Volatile.Read(
                    ref _retired) == 0)
                return true;

            Release();
            return false;
        }

        public void Release()
        {
            int remaining =
                Interlocked.Decrement(
                    ref _activeRoutes);

            if (remaining == 0 &&
                Volatile.Read(
                    ref _retired) != 0)
                _drained.TrySetResult();
        }

        public void Retire()
        {
            Interlocked.Exchange(
                ref _retired,
                1);

            if (Volatile.Read(
                    ref _activeRoutes) == 0)
                _drained.TrySetResult();
        }
    }
}

internal sealed class DhmpUdpPacketSender :
    IDhmpPacketSender,
    IDisposable
{
    private readonly DhmpUdpRuntime _runtime;
    private readonly IPAddress _remoteAddress;

    public DhmpUdpPacketSender(
        DhmpUdpRuntime runtime,
        IPAddress remoteAddress,
        int maximumPayloadBytes)
    {
        _runtime = runtime;
        _remoteAddress = remoteAddress;
        MaximumPayloadBytes =
            maximumPayloadBytes;
    }

    public int MaximumPayloadBytes { get; }

    public ValueTask SendPacketAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default) =>
        _runtime.SendDataAsync(
            _remoteAddress,
            payload,
            cancellationToken);

    public void Dispose()
    {
        // The shared UDP sockets are owned by DhmpUdpRuntime.
    }
}

internal sealed class DhmpUdpControlChannel :
    IDisposable
{
    private readonly Socket _socket;
    private readonly IPAddress _remoteAddress;
    private EndPoint _remoteEndPoint;
    private int _disposed;

    public DhmpUdpControlChannel(
        Socket socket,
        IPAddress remoteAddress,
        int remotePort)
    {
        _socket = socket;
        _remoteAddress = remoteAddress;
        _remoteEndPoint =
            new IPEndPoint(
                remoteAddress,
                remotePort);
    }

    public async ValueTask SendPacketAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        int sent =
            await _socket.SendToAsync(
                packet,
                SocketFlags.None,
                _remoteEndPoint,
                cancellationToken)
            .ConfigureAwait(false);

        if (sent != packet.Length)
            throw new IOException(
                $"UDP control socket accepted {sent} of {packet.Length} bytes.");
    }

    public async ValueTask<int> ReceivePacketAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        EndPoint template =
            _remoteAddress.AddressFamily ==
                AddressFamily.InterNetworkV6
                ? new IPEndPoint(
                    IPAddress.IPv6Any,
                    0)
                : new IPEndPoint(
                    IPAddress.Any,
                    0);

        while (true)
        {
            SocketReceiveFromResult result =
                await _socket.ReceiveFromAsync(
                    destination,
                    SocketFlags.None,
                    template,
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.RemoteEndPoint is not IPEndPoint peer ||
                !peer.Address.Equals(
                    _remoteAddress))
                continue;

            _remoteEndPoint =
                peer;

            return result.ReceivedBytes;
        }
    }

    public void Dispose() =>
        Interlocked.Exchange(
            ref _disposed,
            1);
}
