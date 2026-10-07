using System.Buffers.Binary;
using System.Net;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.Security;

namespace DHMP.Connector;

/// <summary>
/// One established DHMP relationship with a remote IPv6 peer.
/// A connection is bidirectional regardless of which connector initiated it.
/// </summary>
public sealed class DhmpConnection : IAsyncDisposable
{
    private readonly DhmpConnector _owner;
    private readonly DhmpClient _client;
    private readonly IDisposable _transportSender;
    private readonly DhmpProtectedPacketSender? _protectedSender;
    private readonly DhmpPskChaCha20Poly1305Session? _securitySession;
    private readonly DhmpConnectionIdField? _connectionIdField;
    private int _disposed;

    internal DhmpConnection(
        DhmpConnector owner,
        IPAddress remoteAddress,
        DhmpClient client,
        IDisposable transportSender,
        DhmpTransportKind transport,
        DhmpProtectedPacketSender? protectedSender,
        DhmpPskChaCha20Poly1305Session? securitySession,
        ulong? connectionId,
        DhmpConnectionIdField? connectionIdField)
    {
        _owner = owner;
        RemoteAddress = remoteAddress;
        _client = client;
        _transportSender = transportSender;
        Transport = transport;
        _protectedSender = protectedSender;
        _securitySession = securitySession;
        ConnectionId = connectionId;
        _connectionIdField = connectionIdField;
    }

    public IPAddress RemoteAddress { get; }

    /// <summary>The concrete network path selected by the connection resolver.</summary>
    public DhmpTransportKind Transport { get; }

    /// <summary>
    /// Optional 64-bit application-routing identity for duplicate-source connections.
    /// Null when the Connector uses the normal source-IPv6 fast path.
    /// </summary>
    public ulong? ConnectionId { get; }

    public int RecordSize =>
        _client.WireContract.RecordSize;

    public int CurrentMaximumPayloadBytes =>
        _client.CurrentMaximumPayloadBytes;

    public bool IsProtected =>
        _securitySession is not null;

    public event DhmpRecordReceivedHandler? RecordReceived;

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> record,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_connectionIdField is null)
            return _client.SendAsync(record, cancellationToken);

        _client.WireContract.ValidateRecord(record.Length);

        byte[] stamped = record.ToArray();
        BinaryPrimitives.WriteUInt64BigEndian(
            stamped.AsSpan(_connectionIdField.Value.Offset, DhmpConnectionIdField.Size),
            ConnectionId!.Value);

        return _client.SendAsync(stamped, cancellationToken);
    }

    public ValueTask SendBatchAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_connectionIdField is null)
            return _client.SendBatchAsync(packet, cancellationToken);

        _client.WireContract.ValidatePacket(
            packet.Length,
            _client.CurrentMaximumPayloadBytes);

        byte[] stamped = packet.ToArray();
        int recordSize = RecordSize;

        for (int offset = 0; offset < stamped.Length; offset += recordSize)
        {
            BinaryPrimitives.WriteUInt64BigEndian(
                stamped.AsSpan(
                    offset + _connectionIdField.Value.Offset,
                    DhmpConnectionIdField.Size),
                ConnectionId!.Value);
        }

        return _client.SendBatchAsync(stamped, cancellationToken);
    }

    public ValueTask DisposeAsync() =>
        _owner.CloseAsync(this);

    internal void PublishBatch(
        ReadOnlySpan<byte> batch)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var handler = RecordReceived;
        if (handler is null)
            return;

        int recordSize = RecordSize;

        for (int offset = 0;
             offset < batch.Length;
             offset += recordSize)
        {
            handler(
                this,
                batch.Slice(offset, recordSize));
        }
    }

    internal async ValueTask DisposeResourcesAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_protectedSender is not null)
            await _protectedSender.DisposeAsync().ConfigureAwait(false);

        _transportSender.Dispose();
        _securitySession?.Dispose();
    }

    internal bool BelongsTo(
        DhmpConnector connector) =>
        ReferenceEquals(_owner, connector);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
}
