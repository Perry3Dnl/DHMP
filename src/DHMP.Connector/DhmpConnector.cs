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
/// The connector resolves the best available connection path and exposes one DhmpConnection per remote peer.
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

    private DhmpRawIpv6MultiPeerReceiver? _rawReceiver;
    private Task? _rawReceiverTask;
    private DhmpUdpRuntime? _udpRuntime;
    private Task? _udpReceiverTask;
    private bool _rawLocallyAvailable;
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

        int resolvedNetworkMaximum =
            options.TransportPreference switch
            {
                DhmpTransportPreference.RawIpv6Only =>
                    options.MaximumPayloadBytes,
                DhmpTransportPreference.UdpCompatibilityOnly =>
                    options.UdpMaximumPayloadBytes,
                _ =>
                    Math.Min(
                        options.MaximumPayloadBytes,
                        options.UdpMaximumPayloadBytes)
            };

        int applicationMaximum =
            resolvedNetworkMaximum - securityOverhead;

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

    /// <summary>
    /// Reports locally usable transport candidates in resolver priority order.
    /// Reachability is confirmed only when ConnectAsync/AcceptAsync performs the DHMP handshake.
    /// </summary>
    public IReadOnlyList<DhmpTransportCandidate> GetTransportCandidates(
        IPAddress remoteAddress) =>
        DhmpTransportResolver.Probe(
            _options,
            remoteAddress);

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
            if (_options.TransportPreference !=
                    DhmpTransportPreference.UdpCompatibilityOnly &&
                _options.LocalAddress.AddressFamily ==
                    AddressFamily.InterNetworkV6 &&
                !_options.LocalAddress.IsIPv4MappedToIPv6)
            {
                DhmpRawIpv6HostProbeResult rawProbe =
                    DhmpRawIpv6HostProbe.Probe(
                        _options.EnableExperimentalProtocolNumbers);

                _rawLocallyAvailable =
                    rawProbe.IsReady;

                if (_options.TransportPreference ==
                        DhmpTransportPreference.RawIpv6Only &&
                    !_rawLocallyAvailable)
                    throw new InvalidOperationException(
                        $"Raw IPv6 was required but is not locally available: {rawProbe.Message}");

                if (_rawLocallyAvailable)
                {
                    var listenerOptions =
                        new DhmpRawIpv6ListenerOptions(
                            _options.LocalAddress,
                            _options.MaximumPayloadBytes,
                            _options.MaximumPeers,
                            _options.SocketBufferBytes,
                            _options.EnableExperimentalProtocolNumbers,
                            _options.AllowWildcardLocalAddress);

                    _rawReceiver =
                        new DhmpRawIpv6MultiPeerReceiver(
                            listenerOptions);

                    _rawReceiverTask =
                        _rawReceiver.RunAsync(
                            _lifetime.Token);
                }
            }
            else if (_options.TransportPreference ==
                     DhmpTransportPreference.RawIpv6Only)
            {
                throw new InvalidOperationException(
                    "RawIpv6Only requires a locally supported native IPv6 backend.");
            }

            if (_options.TransportPreference !=
                DhmpTransportPreference.RawIpv6Only)
            {
                _udpRuntime =
                    new DhmpUdpRuntime(
                        _options);

                _udpReceiverTask =
                    _udpRuntime.RunAsync(
                        _lifetime.Token);
            }

            if (!_rawLocallyAvailable &&
                _udpRuntime is null)
                throw new InvalidOperationException(
                    "No DHMP connection transport is locally available.");

            return Task.CompletedTask;
        }
        catch
        {
            Volatile.Write(ref _started, 0);
            _rawReceiver?.Dispose();
            _rawReceiver = null;
            _rawReceiverTask = null;
            _udpRuntime?.Dispose();
            _udpRuntime = null;
            _udpReceiverTask = null;
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

        if (_rawReceiver is null)
            throw new InvalidOperationException(
                "BlindFire requires the native Raw IPv6 backend.");

        _rawReceiver.Router.Register(binding);

        if (!_blindFireRegistrations.TryAdd(
                remoteAddress,
                registration))
        {
            _rawReceiver.Router.Remove(remoteAddress);
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
                "Remote address must be a valid IP address.",
                nameof(remoteAddress));

        return ConnectAsync(
            parsed,
            cancellationToken);
    }

    /// <summary>
    /// Run the pre-handshake DHMP Poke resolver without establishing a session.
    /// Mini Poke proves reachability; Full Echo measures an exact 1,200-byte
    /// round trip on the same candidate path.
    /// </summary>
    public Task<DhmpPokeResult> PokeAsync(
        IPAddress remoteAddress,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureStarted();
        ValidateRemoteAddress(
            remoteAddress);

        return ResolvePokeAsync(
            remoteAddress,
            cancellationToken);
    }

    public Task<DhmpPokeResult> PokeAsync(
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(
                remoteAddress,
                out IPAddress? parsed))
            throw new ArgumentException(
                "Remote address must be a valid IP address.",
                nameof(remoteAddress));

        return PokeAsync(
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
            if (_rawReceiver is not null)
            {
                await _rawReceiver.Router
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
            if (connection.Transport ==
                    DhmpTransportKind.RawIpv6 &&
                _rawReceiver is not null)
            {
                if (connection.ConnectionId is ulong connectionId)
                {
                    await _rawReceiver.Router
                        .RemoveAsync(
                            connection.RemoteAddress,
                            connectionId)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _rawReceiver.Router
                        .RemoveAsync(
                            connection.RemoteAddress)
                        .ConfigureAwait(false);
                }
            }
            else if (connection.Transport ==
                         DhmpTransportKind.UdpCompatibility &&
                     _udpRuntime is not null)
            {
                await _udpRuntime.RemoveAsync(
                    connection.RemoteAddress,
                    connection.ConnectionId)
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

        foreach (Task? task in
                 new[] { _rawReceiverTask, _udpReceiverTask })
        {
            if (task is null)
                continue;

            try
            {
                await task.ConfigureAwait(false);
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

        _rawReceiver?.Dispose();
        _udpRuntime?.Dispose();
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

        if (_options.PreSharedKey is null &&
            !_options.AllowUnprotectedPayloads)
            throw new InvalidOperationException(
                "Established plaintext DHMP connections are disabled. Configure a PreSharedKey or set AllowUnprotectedPayloads=true.");

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
                    "This DHMP Connector already has a connection for the remote address.");
            }

            NegotiationResult resolution =
                initiator
                    ? await ResolveInitiatorAsync(
                        remoteAddress,
                        cancellationToken).ConfigureAwait(false)
                    : await ResolveResponderAsync(
                        remoteAddress,
                        cancellationToken).ConfigureAwait(false);

            DhmpPskChaCha20Poly1305Session? session = null;

            if (_options.PreSharedKey is not null)
            {
                session =
                    resolution.Transport ==
                        DhmpTransportKind.RawIpv6
                        ? initiator
                            ? await DhmpRawIpv6SecurityHandshake.InitiateAsync(
                                CreateRawOptions(
                                    remoteAddress,
                                    _options.HandshakeTimeout),
                                _options.PreSharedKey,
                                cancellationToken).ConfigureAwait(false)
                            : await DhmpRawIpv6SecurityHandshake.RespondOnceAsync(
                                CreateRawOptions(
                                    remoteAddress,
                                    _options.HandshakeTimeout),
                                _options.PreSharedKey,
                                cancellationToken).ConfigureAwait(false)
                        : initiator
                            ? await DhmpUdpHandshake.InitiateSecurityAsync(
                                _udpRuntime!,
                                remoteAddress,
                                _options.PreSharedKey,
                                _options.HandshakeTimeout,
                                cancellationToken).ConfigureAwait(false)
                            : await DhmpUdpHandshake.RespondSecurityAsync(
                                _udpRuntime!,
                                remoteAddress,
                                _options.PreSharedKey,
                                _options.HandshakeTimeout,
                                cancellationToken).ConfigureAwait(false);
            }

            IDisposable? transportSender = null;
            DhmpProtectedPacketSender? protectedSender = null;

            try
            {
                IDhmpPacketSender packetSender;

                if (resolution.Transport ==
                    DhmpTransportKind.RawIpv6)
                {
                    var rawSender =
                        new DhmpRawIpv6PacketSender(
                            CreateRawOptions(
                                remoteAddress,
                                _options.HandshakeTimeout));

                    transportSender =
                        rawSender;
                    packetSender =
                        rawSender;
                }
                else
                {
                    DhmpUdpPacketSender udpSender =
                        _udpRuntime!.CreateSender(
                            remoteAddress);

                    transportSender =
                        udpSender;
                    packetSender =
                        udpSender;
                }

                if (session is not null)
                {
                    protectedSender =
                        new DhmpProtectedPacketSender(
                            packetSender,
                            session);

                    packetSender =
                        protectedSender;
                }

                DhmpSendPolicy effectivePolicy =
                    resolution.Peer.ConstrainToPayloadLimit(
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
                        transportSender,
                        resolution.Transport,
                        resolution.Poke,
                        protectedSender,
                        session,
                        connectionId,
                        connectionIdField);

                RegisterReceiveBinding(
                    connection,
                    server,
                    session,
                    connectionId,
                    connectionIdField);

                var connectionKey =
                    new ConnectionKey(
                        remoteAddress,
                        connectionId);

                if (!_connections.TryAdd(
                        connectionKey,
                        connection))
                {
                    await RemoveReceiveBindingAsync(
                        connection)
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

                transportSender?.Dispose();
                session?.Dispose();
                throw;
            }
        }
        finally
        {
            _peerGate.Release();
        }
    }

    private async Task<NegotiationResult> ResolveInitiatorAsync(
        IPAddress remoteAddress,
        CancellationToken cancellationToken)
    {
        DhmpPokeResult poke =
            await ResolvePokeAsync(
                remoteAddress,
                cancellationToken)
            .ConfigureAwait(false);

        DhmpNegotiatedPeer peer =
            poke.Transport ==
                DhmpTransportKind.RawIpv6
                ? await DhmpRawIpv6Handshake.InitiateAsync(
                    CreateRawOptions(
                        remoteAddress,
                        _options.HandshakeTimeout),
                    _wireContract,
                    _sendPolicy,
                    _receivePolicy,
                    GetNegotiatedSchemaId(),
                    cancellationToken).ConfigureAwait(false)
                : await DhmpUdpHandshake.InitiateCompatibilityAsync(
                    _udpRuntime!,
                    remoteAddress,
                    _wireContract,
                    _sendPolicy,
                    _receivePolicy,
                    GetNegotiatedSchemaId(),
                    _options.HandshakeTimeout,
                    cancellationToken).ConfigureAwait(false);

        return new NegotiationResult(
            poke.Transport,
            peer,
            poke);
    }

    private async Task<DhmpPokeResult> ResolvePokeAsync(
        IPAddress remoteAddress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DhmpTransportCandidate> candidates =
            GetTransportCandidates(
                remoteAddress);

        Exception? rawFailure = null;

        DhmpTransportCandidate? raw =
            candidates.FirstOrDefault(
                candidate =>
                    candidate.Kind ==
                    DhmpTransportKind.RawIpv6);

        if (raw?.LocallyAvailable == true)
        {
            try
            {
                TimeSpan timeout =
                    _options.TransportPreference ==
                        DhmpTransportPreference.Auto
                        ? _options.TransportAttemptTimeout
                        : _options.HandshakeTimeout;

                DhmpRawIpv6Options rawOptions =
                    CreateRawOptions(
                        remoteAddress,
                        timeout);

                TimeSpan mini =
                    await DhmpRawIpv6Poke.ProbeAsync(
                        rawOptions,
                        DhmpPokeCodec.MinimumPacketSize,
                        cancellationToken)
                    .ConfigureAwait(false);

                int fullEchoBytes =
                    DhmpPokeCodec.FullEchoPacketSize;

                TimeSpan full =
                    await DhmpRawIpv6Poke.ProbeAsync(
                        rawOptions,
                        fullEchoBytes,
                        cancellationToken)
                    .ConfigureAwait(false);

                return new DhmpPokeResult(
                    DhmpTransportKind.RawIpv6,
                    mini,
                    full,
                    fullEchoBytes);
            }
            catch (Exception error)
                when (_options.TransportPreference ==
                          DhmpTransportPreference.Auto &&
                      IsReachabilityFailure(
                          error,
                          cancellationToken))
            {
                rawFailure =
                    error;
            }
        }

        DhmpTransportCandidate? udp =
            candidates.FirstOrDefault(
                candidate =>
                    candidate.Kind ==
                    DhmpTransportKind.UdpCompatibility);

        if (udp?.LocallyAvailable == true &&
            _udpRuntime is not null)
        {
            TimeSpan mini =
                await DhmpUdpPoke.ProbeAsync(
                    _udpRuntime,
                    remoteAddress,
                    DhmpPokeCodec.MinimumPacketSize,
                    _options.HandshakeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            int fullEchoBytes =
                DhmpPokeCodec.FullEchoPacketSize;

            TimeSpan full =
                await DhmpUdpPoke.ProbeAsync(
                    _udpRuntime,
                    remoteAddress,
                    fullEchoBytes,
                    _options.HandshakeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            return new DhmpPokeResult(
                DhmpTransportKind.UdpCompatibility,
                mini,
                full,
                fullEchoBytes);
        }

        throw new InvalidOperationException(
            rawFailure is null
                ? "No locally available DHMP transport can answer Poke for this address family."
                : $"Native DHMP Poke failed and no UDP compatibility path was available: {rawFailure.Message}",
            rawFailure);
    }

    private async Task<NegotiationResult> ResolveResponderAsync(
        IPAddress remoteAddress,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DhmpTransportCandidate> candidates =
            GetTransportCandidates(
                remoteAddress);

        var attempts =
            new List<Task<NegotiationResult>>(2);

        using var linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        if (candidates.Any(
                candidate =>
                    candidate.Kind ==
                        DhmpTransportKind.RawIpv6 &&
                    candidate.LocallyAvailable))
        {
            attempts.Add(
                RespondRawAsync(
                    remoteAddress,
                    linked.Token));
        }

        if (_udpRuntime is not null &&
            candidates.Any(
                candidate =>
                    candidate.Kind ==
                        DhmpTransportKind.UdpCompatibility &&
                    candidate.LocallyAvailable))
        {
            attempts.Add(
                RespondUdpAsync(
                    remoteAddress,
                    linked.Token));
        }

        if (attempts.Count == 0)
            throw new InvalidOperationException(
                "No locally available DHMP transport can accept this peer.");

        var failures =
            new List<Exception>();

        while (attempts.Count != 0)
        {
            Task<NegotiationResult> completed =
                await Task.WhenAny(
                    attempts).ConfigureAwait(false);

            attempts.Remove(
                completed);

            try
            {
                NegotiationResult result =
                    await completed.ConfigureAwait(false);

                linked.Cancel();

                foreach (Task<NegotiationResult> pending in attempts)
                {
                    try
                    {
                        await pending.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                return result;
            }
            catch (Exception error)
                when (!cancellationToken.IsCancellationRequested)
            {
                failures.Add(
                    error);
            }
        }

        throw new AggregateException(
            "All DHMP transport accept attempts failed.",
            failures);
    }

    private async Task<NegotiationResult> RespondRawAsync(
        IPAddress remoteAddress,
        CancellationToken cancellationToken)
    {
        DhmpNegotiatedPeer peer =
            await DhmpRawIpv6Handshake.RespondOnceAsync(
                CreateRawOptions(
                    remoteAddress,
                    _options.HandshakeTimeout),
                _wireContract,
                _sendPolicy,
                _receivePolicy,
                GetNegotiatedSchemaId(),
                cancellationToken).ConfigureAwait(false);

        return new NegotiationResult(
            DhmpTransportKind.RawIpv6,
            peer,
            null);
    }

    private async Task<NegotiationResult> RespondUdpAsync(
        IPAddress remoteAddress,
        CancellationToken cancellationToken)
    {
        DhmpNegotiatedPeer peer =
            await DhmpUdpHandshake.RespondCompatibilityAsync(
                _udpRuntime!,
                remoteAddress,
                _wireContract,
                _sendPolicy,
                _receivePolicy,
                GetNegotiatedSchemaId(),
                _options.HandshakeTimeout,
                cancellationToken).ConfigureAwait(false);

        return new NegotiationResult(
            DhmpTransportKind.UdpCompatibility,
            peer,
            null);
    }

    private void RegisterReceiveBinding(
        DhmpConnection connection,
        DhmpServer server,
        DhmpPskChaCha20Poly1305Session? session,
        ulong? connectionId,
        DhmpConnectionIdField? connectionIdField)
    {
        if (connection.Transport ==
            DhmpTransportKind.RawIpv6)
        {
            if (_rawReceiver is null)
                throw new InvalidOperationException(
                    "The selected Raw IPv6 receive backend is not running.");

            var binding =
                new DhmpRawIpv6PeerBinding(
                    connection.RemoteAddress,
                    server,
                    connection.PublishBatch,
                    session,
                    _options.AllowUnprotectedPayloads,
                    connectionId,
                    connectionIdField?.Offset);

            _rawReceiver.Router.Register(
                binding);

            return;
        }

        if (_udpRuntime is null)
            throw new InvalidOperationException(
                "The selected UDP compatibility backend is not running.");

        _udpRuntime.Register(
            connection.RemoteAddress,
            server,
            connection.PublishBatch,
            session,
            _options.AllowUnprotectedPayloads,
            connectionId,
            connectionIdField?.Offset);
    }

    private async Task RemoveReceiveBindingAsync(
        DhmpConnection connection)
    {
        if (connection.Transport ==
                DhmpTransportKind.RawIpv6 &&
            _rawReceiver is not null)
        {
            if (connection.ConnectionId is ulong connectionId)
            {
                await _rawReceiver.Router
                    .RemoveAsync(
                        connection.RemoteAddress,
                        connectionId)
                    .ConfigureAwait(false);
            }
            else
            {
                await _rawReceiver.Router
                    .RemoveAsync(
                        connection.RemoteAddress)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (_udpRuntime is not null)
        {
            await _udpRuntime.RemoveAsync(
                connection.RemoteAddress,
                connection.ConnectionId)
                .ConfigureAwait(false);
        }
    }

    private static bool IsReachabilityFailure(
        Exception error,
        CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested &&
        error is TimeoutException or
            OperationCanceledException or
            SocketException or
            IOException or
            PlatformNotSupportedException or
            UnauthorizedAccessException;

    private readonly record struct NegotiationResult(
        DhmpTransportKind Transport,
        DhmpNegotiatedPeer Peer,
        DhmpPokeResult? Poke);

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
        TimeSpan? handshakeTimeout = null,
        bool? allowUnprotectedPayloads = null) =>
        new(
            _options.LocalAddress,
            remoteAddress,
            _options.MaximumPayloadBytes,
            _options.SocketBufferBytes,
            handshakeTimeout ??
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

    private void ValidateRemoteAddress(
        IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (remoteAddress.AddressFamily !=
                _options.LocalAddress.AddressFamily ||
            remoteAddress.IsIPv4MappedToIPv6 ||
            remoteAddress.Equals(IPAddress.IPv6Any) ||
            remoteAddress.Equals(IPAddress.Any))
            throw new ArgumentException(
                "DHMP connections require an explicit remote address in the configured local address family.",
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
