using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<IPAddress, DhmpConnection> _connections = new();
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

    internal async ValueTask CloseAsync(
        DhmpConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!connection.BelongsTo(this))
            throw new ArgumentException(
                "The connection belongs to a different DHMP Connector.",
                nameof(connection));

        if (_connections.TryRemove(
                connection.RemoteAddress,
                out DhmpConnection? current) &&
            ReferenceEquals(current, connection))
        {
            if (_receiver is not null)
            {
                await _receiver.Router
                    .RemoveAsync(connection.RemoteAddress)
                    .ConfigureAwait(false);
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
            if (_connections.ContainsKey(remoteAddress))
                throw new InvalidOperationException(
                    "This DHMP Connector already has a connection for the remote IPv6 address.");

            DhmpRawIpv6Options rawOptions =
                CreateRawOptions(remoteAddress);

            DhmpNegotiatedPeer negotiated =
                initiator
                    ? await DhmpRawIpv6Handshake.InitiateAsync(
                        rawOptions,
                        _wireContract,
                        _sendPolicy,
                        _receivePolicy,
                        _options.SchemaId,
                        cancellationToken).ConfigureAwait(false)
                    : await DhmpRawIpv6Handshake.RespondOnceAsync(
                        rawOptions,
                        _wireContract,
                        _sendPolicy,
                        _receivePolicy,
                        _options.SchemaId,
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

                var connection =
                    new DhmpConnection(
                        this,
                        remoteAddress,
                        client,
                        rawSender,
                        protectedSender,
                        session);

                var binding =
                    new DhmpRawIpv6PeerBinding(
                        remoteAddress,
                        server,
                        connection.PublishBatch,
                        session,
                        _options.AllowUnprotectedPayloads);

                _receiver!.Router.Register(binding);

                if (!_connections.TryAdd(
                        remoteAddress,
                        connection))
                {
                    await _receiver.Router
                        .RemoveAsync(remoteAddress)
                        .ConfigureAwait(false);

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

    private DhmpRawIpv6Options CreateRawOptions(
        IPAddress remoteAddress) =>
        new(
            _options.LocalAddress,
            remoteAddress,
            _options.MaximumPayloadBytes,
            _options.SocketBufferBytes,
            _options.HandshakeTimeout,
            _options.EnableExperimentalProtocolNumbers,
            _options.AllowWildcardLocalAddress,
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
}
