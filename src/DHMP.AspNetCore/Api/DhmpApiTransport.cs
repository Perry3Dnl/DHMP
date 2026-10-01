using System.Net;
using System.Security.Cryptography;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.RawIpv6;
using DHMP.Security;
using DHMP.Server;

namespace DHMP.AspNetCore;

// Exact complete-packet boundary allows privilege-free composition tests, without a network fallback.
internal interface IDhmpApiTransport : IAsyncDisposable
{
    Guid SessionId { get; }
    ValueTask SendAsync(ReadOnlyMemory<byte> record, CancellationToken token);
    Task RunAsync(Action<ReadOnlySpan<byte>> publish, CancellationToken token);
}
internal interface IDhmpApiTransportFactory
{
    Task<IDhmpApiTransport> OpenAsync(DhmpApiOptions options, CancellationToken token);
}
internal sealed class DhmpApiTransportFactory : IDhmpApiTransportFactory
{
    public async Task<IDhmpApiTransport> OpenAsync(DhmpApiOptions settings, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var options = DhmpRawIpv6Options.FromPathMtu(IPAddress.Parse(settings.LocalAddress), IPAddress.Parse(settings.RemoteAddress),
            settings.PathMtu,
            additionalIpv6HeaderBytes: settings.AdditionalIpv6HeaderBytes,
            handshakeTimeout: settings.HandshakeTimeout,
            enableExperimentalProtocolNumbers: settings.EnableExperimentalProtocolNumbers);
        byte[] bytes = Convert.FromBase64String(settings.PreSharedKeyBase64);
        DhmpPreSharedKey key;
        try { key = new DhmpPreSharedKey(settings.KeyId, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        DhmpPskChaCha20Poly1305Session session;
        using (key)
        {
            session = settings.Initiator
                ? await DhmpRawIpv6SecurityHandshake.InitiateAsync(options, key, token).ConfigureAwait(false)
                : await DhmpRawIpv6SecurityHandshake.RespondOnceAsync(options, key, token).ConfigureAwait(false);
        }
        DhmpRawIpv6PacketSender? backend = null;
        DhmpRawIpv6Receiver? receiver = null;
        try
        {
            var wire = new DhmpWireContract(DhmpApiRecord.Size);
            backend = new DhmpRawIpv6PacketSender(options);
            var sender = new DhmpProtectedPacketSender(backend, session);
            var client = new DhmpClient(sender, wire,
                new DhmpSendPolicy(settings.RecordsPerSecond, DhmpApiRecord.Size, DhmpRatePolicy.SmoothPacing));
            receiver = new DhmpRawIpv6Receiver(options,
                new DhmpServer(wire, new DhmpReceivePolicy(DhmpProcessingMode.Sequential, DhmpApiRecord.Size)), session);
            return new Transport(client, sender, receiver, backend, session);
        }
        catch { receiver?.Dispose(); backend?.Dispose(); session.Dispose(); throw; }
    }
    private sealed class Transport(DhmpClient client, DhmpProtectedPacketSender sender,
        DhmpRawIpv6Receiver receiver, DhmpRawIpv6PacketSender backend, DhmpPskChaCha20Poly1305Session session) : IDhmpApiTransport
    {
        public Guid SessionId => session.SessionId;
        public ValueTask SendAsync(ReadOnlyMemory<byte> record, CancellationToken token) => client.SendAsync(record, token);
        public Task RunAsync(Action<ReadOnlySpan<byte>> publish, CancellationToken token) => receiver.RunAsync(publish, token);
        public async ValueTask DisposeAsync()
        {
            // Runtime joins incoming loop and application workers before reaching this boundary.
            await sender.DisposeAsync().ConfigureAwait(false);
            receiver.Dispose(); backend.Dispose(); session.Dispose();
        }
    }
}
