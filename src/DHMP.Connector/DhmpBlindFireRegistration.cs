using System.Net;

namespace DHMP.Connector;

/// <summary>
/// Server-side registration for one plaintext BlindFire source IPv6 address.
/// This is not an authenticated DHMP connection.
/// </summary>
public sealed class DhmpBlindFireRegistration : IAsyncDisposable
{
    private readonly DhmpConnector _owner;
    private int _disposed;

    internal DhmpBlindFireRegistration(
        DhmpConnector owner,
        IPAddress remoteAddress)
    {
        _owner = owner;
        RemoteAddress = remoteAddress;
    }

    public IPAddress RemoteAddress { get; }

    public event DhmpBlindFireReceivedHandler? RecordReceived;

    public ValueTask CloseAsync() =>
        _owner.CloseBlindFireAsync(this);

    public ValueTask DisposeAsync() =>
        CloseAsync();

    internal void PublishBatch(
        ReadOnlySpan<byte> batch,
        int recordSize)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var handler = RecordReceived;
        if (handler is null)
            return;

        for (int offset = 0;
             offset < batch.Length;
             offset += recordSize)
        {
            handler(
                this,
                batch.Slice(offset, recordSize));
        }
    }

    internal bool BelongsTo(
        DhmpConnector connector) =>
        ReferenceEquals(_owner, connector);

    internal void MarkDisposed() =>
        Interlocked.Exchange(ref _disposed, 1);
}
