using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.RawIpv6;
using DHMP.Security;
using DHMP.Server;

namespace DHMP.Connector;

/// <summary>
/// Application-facing DHMP networking runtime.
/// The connector owns local raw-IPv6 networking and exposes one DhmpConnection per remote peer.
/// </summary>
public sealed class DhmpConnector : IAsyncDisposable
{
    private readonly DhmpConnectorOptions _options;
    private readonly DhmpWireContract _wireContract;
    private readonly DhmpSendPolicy _sendPolicy;
    private readonly DhmpReceivePolicy _receivePolicy;
    private readonly ConcurrentDictionary<ConnectionKey, DhmpConnection> _connections = new();
    private readonly ConcurrentDictionary<IPAddress, DhmpBlindFireRegistration> _blindFireRegistrations = new();
    private readonly SemaphoreSlim _peerGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private DhmpRawIpv6MultiPeerReceiver? _receiver;
    private Task? _receiverTask;
    private int _started;
    private int _disposed;

    public DhmpConnector(
        DhmpConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _wireContract = new DhmpWireContract(options.RecordSize);

        int securityOverhead =
            options.PreSharedKey is null
                ? 0
                : DhmpPskChaCha20Poly1305Session.Overhead;

        int applicationMaximum =
            options.MaximumPayloadBytes - securityOverhead;

        applicationMaximum =
            applicationMaximum /
            options.RecordSize *
            options.RecordSize;

        if (applicationMaximum < options.RecordSize)
            throw new ArgumentException(
                "The configured payload budget cannot fit one complete record after security overhead.",
                nameof(options));

        _sendPolicy = new DhmpSendPolicy(
            options.MaximumMessagesPerSecond,
            applicationMaximum,
            options.RatePolicy);

        _receivePolicy = new DhmpReceivePolicy(
            options.ReceiveMode,
            applicationMaximum);
    }

    public IPAddress LocalAddress =>
        _options.LocalAddress;

    public int RecordSize =>
        _wireContract.RecordSize;

    public IReadOnlyCollection<DhmpConnection> Connections =>
        _connections.Values.ToArray();

    public IReadOnlyCollection<DhmpBlindFireRegistration> BlindFireRegistrations =>
        _blindFireRegistrations.Values.ToArray();

    public Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException(
                "This DHMP Connector is already started.");

        try
        {
            var listenerOptions =
                new DhmpRawIpv6ListenerOptions(
                    _options.LocalAddress,
                    _options.MaximumPayloadBytes,
                    _options.MaximumPeers,
                    _options.SocketBufferBytes,
                    _options.EnableExperimentalProtocolNumbers,
                    _options.AllowWildcardLocalAddress);

            _receiver =
                new DhmpRawIpv6MultiPeerReceiver(
                    listenerOptions);

            _receiverTask =
                _receiver.RunAsync(
                    _lifetime.Token);

            return Task.CompletedTask;
        }
        catch
        {
            Volatile.Write(ref _started, 0);
            _receiver?.Dispose();
            _receiver = null;
            _receiverTask = null;
            throw;
        }
    }

    /// <summary>
    /// Register one plaintext IoT/telemetry source for one-packet BlindFire delivery.
    /// No DHMP handshake or security session is created for BlindFire packets.
    /// </summary>
    public DhmpBlindFireRegistration RegisterIoTDevice(
        IPAddress remoteAddress)
    {
        ThrowIfDisposed();
        EnsureStarted();
        ValidateRemoteAddress(remoteAddress);

        if (!_options.AllowUnprotectedBlindFire)
            throw new InvalidOperationException(
                "BlindFire is disabled. Set AllowUnprotectedBlindFire=true only when plaintext source-address/schema validation is acceptable.");

        if (_connections.Keys.Any(
                key => key.RemoteAddress.Equals(remoteAddress)))
            throw new InvalidOperationException(
                "The IPv6 address is already used by an established DHMP connection.");

        if (_blindFireRegistrations.ContainsKey(remoteAddress))
            throw new InvalidOperationException(
                "This BlindFire IPv6 source is already registered.");

        var server =
            new DhmpServer(
                _wireContract,
                _receivePolicy);

        var registration =
            new DhmpBlindFireRegistration(
                this,
                remoteAddress);

        var binding =
            new DhmpRawIpv6PeerBinding(
                remoteAddress,
                server,
                span => registration.PublishBatch(
                    span,
                    _wireContract.RecordSize),
                allowUnprotectedPayloads: true);

        _receiver!.Router.Register(binding);

        if (!_blindFireRegistrations.TryAdd(
                remoteAddress,
                registration))
        {
            _receiver.Router.Remove(remoteAddress);
            throw new InvalidOperationException(
                "Could not publish the BlindFire registration.");
        }

        return registration;
    }

    public DhmpBlindFireRegistration RegisterIoTDevice(
        string remoteAddress)
    {
        if (!IPAddress.TryParse(
                remoteAddress,
                out IPAddress? parsed))
            throw new ArgumentException(
                "Remote address must be a valid IPv6 address.",
                nameof(remoteAddress));

        return RegisterIoTDevice(parsed);
    }

    /// <summary>
    /// Sends exactly one plaintext fixed DHMP record without performing a handshake.
    /// The remote server must already have registered this source IPv6/schema out of band.
    /// </summary>
    public async ValueTask BlindFireAsync(
        IPAddress remoteAddress,
        ReadOnlyMemory<byte> record,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateRemoteAddress(remoteAddress);

        if (!_options.AllowUnprotectedBlindFire)
            throw new InvalidOperationException(
                "BlindFire is disabled. Set AllowUnprotectedBlindFire=true only when plaintext one-packet delivery is intentionally accepted.");

        _wireContract.ValidateRecord(record.Length);

        using var sender =
            new DhmpRawIpv6PacketSender(
                CreateRawOptions(
                    remoteAddress,
                    allowUnprotectedPayloads: true));

        await sender.SendPacketAsync(
            record,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask BlindFireAsync(
        string remoteAddress,
        ReadOnlyMemory<byte> record,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(
                remoteAddress,
                out IPAddress? parsed))
            throw new ArgumentException(
                "Remote address must be a valid IPv6 address.",
                nameof(remoteAddress));

        return BlindFireAsync(
            parsed,
            record,
            cancellationToken);
    }

    /// <summary>
    /// Initiate compatibility/security setup with an explicitly addressed DHMP Connector.
    /// The remote connector must call AcceptAsync for this address with matching settings.
    /// </summary>
    public Task<DhmpConnection> ConnectAsync(
        IPAddress remoteAddress,
        CancellationToken cancellationToken = default) =>
        EstablishAsync(
            remoteAddress,
            initiator: true,
            cancellationToken);

    public Task<DhmpConnection> ConnectAsync(
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(
                remoteAddress,
                out IPAddress? parsed))
            throw new ArgumentException(
                "Remote address must be a valid IPv6 address.",
                nameof(remoteAddress));

        return ConnectAsync(
            parsed,
            cancellationToken);
    }

    /// <summary>
    /// Accept one explicitly configured remote DHMP Connector.
    /// Automatic discovery of previously unknown peers is not part of the current control plane.
    /// </summary>
    public Task<DhmpConnection> AcceptAsync(
        IPAddress remoteAddress,
        CancellationToken cancellationToken = default) =>
        EstablishAsync(
            remoteAddress,
            initiator: false,
            cancellationToken);

    public Task<DhmpConnection> AcceptAsync(
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(
                remoteAddress,
                out IPAddress? parsed))
            throw new ArgumentException(
                "Remote address must be a valid IPv6 address.",
                nameof(remoteAddress));

        return AcceptAsync(
            parsed,
            cancellationToken);
    }

    internal async ValueTask CloseBlindFireAsync(
        DhmpBlindFireRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);

        if (!registration.BelongsTo(this))
            throw new ArgumentException(
                "The BlindFire registration belongs to a different DHMP Connector.",
                nameof(registration));

        if (_blindFireRegistrations.TryRemove(
                registration.RemoteAddress,
                out DhmpBlindFireRegistration? current) &&
            ReferenceEquals(current, registration))
        {
            if (_receiver is not null)
            {
                await _receiver.Router
                    .RemoveAsync(registration.RemoteAddress)
                    .ConfigureAwait(false);
            }
        }

        registration.MarkDisposed();
    }

    internal async ValueTask CloseAsync(
        DhmpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!connection.BelongsTo(this))
            throw new ArgumentException(
                "The connection belongs to a different DHMP Connector.",
                nameof(connection));

        var key =
            new ConnectionKey(
                connection.RemoteAddress,
                connection.ConnectionId);

        if (_connections.TryRemove(
                key,
                out DhmpConnection? current) &&
            ReferenceEquals(current, connection))
        {
            if (_receiver is not null)
            {
                if (connection.ConnectionId is ulong connectionId)
                {
                    await _receiver.Router
                        .RemoveAsync(
                            connection.RemoteAddress,
                            connectionId)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _receiver.Router
                        .RemoveAsync(connection.RemoteAddress)
                        .ConfigureAwait(false);
                }
            }
        }

        await connection
            .DisposeResourcesAsync()
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();

        if (_receiverTask is not null)
        {
            try
            {
                await _receiverTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (DhmpConnection connection in
                 _connections.Values.ToArray())
        {
            await CloseAsync(connection)
                .ConfigureAwait(false);
        }

        foreach (DhmpBlindFireRegistration registration in
                 _blindFireRegistrations.Values.ToArray())
        {
            await CloseBlindFireAsync(registration)
                .ConfigureAwait(false);
        }

        _receiver?.Dispose();
        _peerGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task<DhmpConnection> EstablishAsync(
        IPAddress remoteAddress,
        bool initiator,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureStarted();
        ValidateRemoteAddress(remoteAddress);

        await _peerGate
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (_options.DuplicatePeerHandling ==
                    DhmpDuplicatePeerHandling.Reject &&
                _connections.Keys.Any(
                    key =>
                        key.RemoteAddress.Equals(
                            remoteAddress)))
            {
                throw new InvalidOperationException(
                    "This DHMP Connector already has a connection for the remote IPv6 address. " +
                    "Use DuplicatePeerHandling.ResolveWithConnectionId with an application-owned ConnectionId field when multiple logical peers intentionally share one source IPv6 address.");
            }

            DhmpRawIpv6Options rawOptions =
                CreateRawOptions(remoteAddress);

            DhmpNegotiatedPeer negotiated =
                initiator
                    ? await DhmpRawIpv6Handshake.InitiateAsync(
                        rawOptions,
                        _wireContract,
                        _sendPolicy,
                        _receivePolicy,
                        GetNegotiatedSchemaId(),
                        cancellationToken).ConfigureAwait(false)
                    : await DhmpRawIpv6Handshake.RespondOnceAsync(
                        rawOptions,
                        _wireContract,
                        _sendPolicy,
                        _receivePolicy,
                        GetNegotiatedSchemaId(),
                        cancellationToken).ConfigureAwait(false);

            DhmpPskChaCha20Poly1305Session? session = null;

            if (_options.PreSharedKey is not null)
            {
                session =
                    initiator
                        ? await DhmpRawIpv6SecurityHandshake.InitiateAsync(
                            rawOptions,
                            _options.PreSharedKey,
                            cancellationToken).ConfigureAwait(false)
                        : await DhmpRawIpv6SecurityHandshake.RespondOnceAsync(
                            rawOptions,
                            _options.PreSharedKey,
                            cancellationToken).ConfigureAwait(false);
            }

            DhmpRawIpv6PacketSender? rawSender = null;
            DhmpProtectedPacketSender? protectedSender = null;

            try
            {
                rawSender =
                    new DhmpRawIpv6PacketSender(
                        rawOptions);

                IDhmpPacketSender packetSender =
                    rawSender;

                if (session is not null)
                {
                    protectedSender =
                        new DhmpProtectedPacketSender(
                            rawSender,
                            session);

                    packetSender = protectedSender;
                }

                DhmpSendPolicy effectivePolicy =
                    negotiated.ConstrainToPayloadLimit(
                        packetSender.MaximumPayloadBytes);

                var client =
                    new DhmpClient(
                        packetSender,
                        _wireContract,
                        effectivePolicy);

                var server =
                    new DhmpServer(
                        _wireContract,
                        _receivePolicy);

                ulong? connectionId =
                    GetConnectionId(session);

                DhmpConnectionIdField? connectionIdField =
                    _options.DuplicatePeerHandling ==
                        DhmpDuplicatePeerHandling.ResolveWithConnectionId
                        ? _options.ConnectionIdField
                        : null;

                var connection =
                    new DhmpConnection(
                        this,
                        remoteAddress,
                        client,
                        rawSender,
                        protectedSender,
                        session,
                        connectionId,
                        connectionIdField);

                var binding =
                    new DhmpRawIpv6PeerBinding(
                        remoteAddress,
                        server,
                        connection.PublishBatch,
                        session,
                        _options.AllowUnprotectedPayloads,
                        connectionId,
                        connectionIdField?.Offset);

                _receiver!.Router.Register(binding);

                var connectionKey =
                    new ConnectionKey(
                        remoteAddress,
                        connectionId);

                if (!_connections.TryAdd(
                        connectionKey,
                        connection))
                {
                    if (connectionId is ulong duplicateId)
                    {
                        await _receiver.Router
                            .RemoveAsync(
                                remoteAddress,
                                duplicateId)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _receiver.Router
                            .RemoveAsync(remoteAddress)
                            .ConfigureAwait(false);
                    }

                    await connection
                        .DisposeResourcesAsync()
                        .ConfigureAwait(false);

                    throw new InvalidOperationException(
                        "Could not publish the established DHMP connection.");
                }

                return connection;
            }
            catch
            {
                if (protectedSender is not null)
                    await protectedSender.DisposeAsync().ConfigureAwait(false);

                rawSender?.Dispose();
                session?.Dispose();
                throw;
            }
        }
        finally
        {
            _peerGate.Release();
        }
    }

    private Guid GetNegotiatedSchemaId()
    {
        if (_options.DuplicatePeerHandling ==
            DhmpDuplicatePeerHandling.Reject)
            return _options.SchemaId;

        Span<byte> material =
            stackalloc byte[25];

        _options.SchemaId.TryWriteBytes(
            material[..16],
            bigEndian: true,
            out _);

        "CID1"u8.CopyTo(
            material.Slice(16, 4));

        material[20] =
            (byte)DhmpConnectionIdField.Size;

        BinaryPrimitives.WriteInt32BigEndian(
            material.Slice(21, 4),
            _options.ConnectionIdField!.Value.Offset);

        Span<byte> hash =
            stackalloc byte[32];

        SHA256.HashData(
            material,
            hash);

        return new Guid(
            hash[..16],
            bigEndian: true);
    }

    private ulong? GetConnectionId(
        DhmpPskChaCha20Poly1305Session? session)
    {
        if (_options.DuplicatePeerHandling ==
            DhmpDuplicatePeerHandling.Reject)
            return null;

        if (session is null)
            throw new InvalidOperationException(
                "ResolveWithConnectionId requires an authenticated DHMP session.");

        Span<byte> sessionBytes =
            stackalloc byte[16];

        session.SessionId.TryWriteBytes(
            sessionBytes,
            bigEndian: true,
            out _);

        ulong connectionId =
            BinaryPrimitives.ReadUInt64BigEndian(
                sessionBytes[..8]);

        if (connectionId == 0)
        {
            connectionId =
                BinaryPrimitives.ReadUInt64BigEndian(
                    sessionBytes[8..]);
        }

        if (connectionId == 0)
            throw new InvalidOperationException(
                "Authenticated session produced an invalid zero ConnectionId.");

        return connectionId;
    }

    private DhmpRawIpv6Options CreateRawOptions(
        IPAddress remoteAddress,
        bool? allowUnprotectedPayloads = null) =>
        new(
            _options.LocalAddress,
            remoteAddress,
            _options.MaximumPayloadBytes,
            _options.SocketBufferBytes,
            _options.HandshakeTimeout,
            _options.EnableExperimentalProtocolNumbers,
            _options.AllowWildcardLocalAddress,
            allowUnprotectedPayloads ??
                _options.AllowUnprotectedPayloads);

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _started) == 0)
            throw new InvalidOperationException(
                "StartAsync must be called before establishing DHMP connections.");
    }

    private static void ValidateRemoteAddress(
        IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (remoteAddress.AddressFamily !=
                AddressFamily.InterNetworkV6 ||
            remoteAddress.IsIPv4MappedToIPv6 ||
            remoteAddress.Equals(IPAddress.IPv6Any))
            throw new ArgumentException(
                "DHMP connections require an explicit native IPv6 remote address.",
                nameof(remoteAddress));
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

    private readonly record struct ConnectionKey(
        IPAddress RemoteAddress,
        ulong? ConnectionId);
}
