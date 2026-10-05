using System.Net;
using DHMP.Client;
using DHMP.RawIpv6;
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
    private readonly DhmpRawIpv6PacketSender _rawSender;
    private readonly DhmpProtectedPacketSender? _protectedSender;
    private readonly DhmpPskChaCha20Poly1305Session? _securitySession;
    private int _disposed;

    internal DhmpConnection(
        DhmpConnector owner,
        IPAddress remoteAddress,
        DhmpClient client,
        DhmpRawIpv6PacketSender rawSender,
        DhmpProtectedPacketSender? protectedSender,
        DhmpPskChaCha20Poly1305Session? securitySession)
    {
        _owner = owner;
        RemoteAddress = remoteAddress;
        _client = client;
        _rawSender = rawSender;
        _protectedSender = protectedSender;
        _securitySession = securitySession;
    }

    public IPAddress RemoteAddress { get; }

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
        return _client.SendAsync(record, cancellationToken);
    }

    public ValueTask SendBatchAsync(
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _client.SendBatchAsync(packet, cancellationToken);
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

        _rawSender.Dispose();
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
