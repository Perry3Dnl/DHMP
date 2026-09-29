using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace DHMP.RawIpv6;

/// <summary>
/// Bounded hot-path router from source IPv6 address to one DHMP session.
/// V1 intentionally supports at most one registered session per source address.
/// </summary>
public sealed class DhmpRawIpv6PeerRouter
{
    private readonly ConcurrentDictionary<
        IPAddress,
        DhmpRawIpv6PeerBinding> _peers = new();

    private readonly object _registrationGate = new();
    private readonly int _maximumPeers;
    private readonly int _maximumNetworkPayloadBytes;

    private long _acceptedPackets;
    private long _unknownPeerPackets;
    private long _rejectedPackets;
    private long _protectionRejectedPackets;

    public DhmpRawIpv6PeerRouter(
        int maximumPeers,
        int maximumNetworkPayloadBytes)
    {
        if (maximumPeers <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maximumPeers));

        if (maximumNetworkPayloadBytes <= 0 ||
            maximumNetworkPayloadBytes > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(maximumNetworkPayloadBytes));

        _maximumPeers = maximumPeers;
        _maximumNetworkPayloadBytes =
            maximumNetworkPayloadBytes;
    }

    public int PeerCount => _peers.Count;
    public int MaximumPeers => _maximumPeers;

    public long AcceptedPackets =>
        Interlocked.Read(ref _acceptedPackets);

    public long UnknownPeerPackets =>
        Interlocked.Read(ref _unknownPeerPackets);

    public long RejectedPackets =>
        Interlocked.Read(ref _rejectedPackets);

    public long ProtectionRejectedPackets =>
        Interlocked.Read(
            ref _protectionRejectedPackets);

    public void Register(
        DhmpRawIpv6PeerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (binding.MaximumNetworkPayloadBytes >
            _maximumNetworkPayloadBytes)
            throw new ArgumentException(
                "Peer receive policy plus protection overhead exceeds the listener payload ceiling.",
                nameof(binding));

        lock (_registrationGate)
        {
            if (_peers.ContainsKey(binding.RemoteAddress))
                throw new InvalidOperationException(
                    "DHMP V1 already has a session bound to this source IPv6 address.");

            if (_peers.Count >= _maximumPeers)
                throw new InvalidOperationException(
                    "DHMP raw IPv6 peer limit reached.");

            if (!_peers.TryAdd(
                    binding.RemoteAddress,
                    binding))
                throw new InvalidOperationException(
                    "Could not register the DHMP peer binding.");
        }
    }

    public bool Remove(
        IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        lock (_registrationGate)
        {
            return _peers.TryRemove(
                remoteAddress,
                out _);
        }
    }

    /// <summary>
    /// Route one already-received raw IPv6 protocol payload.
    /// plaintextScratch is reused only for protected peers.
    /// </summary>
    public bool TryRoute(
        IPAddress remoteAddress,
        ReadOnlySpan<byte> networkPayload,
        Span<byte> plaintextScratch)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (!_peers.TryGetValue(
                remoteAddress,
                out var binding))
        {
            Interlocked.Increment(
                ref _unknownPeerPackets);
            return false;
        }

        ReadOnlySpan<byte> payload =
            networkPayload;

        int plaintextBytes = 0;
        bool decoded = false;

        if (binding.Decoder is not null)
        {
            if (plaintextScratch.Length <
                binding.Server.ReceivePolicy.MaximumPayloadBytes)
                throw new ArgumentException(
                    "Plaintext scratch buffer is smaller than the peer receive policy.",
                    nameof(plaintextScratch));

            if (!binding.Decoder.TryDecode(
                    networkPayload,
                    plaintextScratch,
                    out plaintextBytes))
            {
                Interlocked.Increment(
                    ref _protectionRejectedPackets);
                return false;
            }

            payload =
                plaintextScratch[..plaintextBytes];

            decoded = true;
        }

        try
        {
            var wire =
                binding.Server.WireContract;

            if (payload.Length <= 0 ||
                payload.Length >
                    binding.Server.ReceivePolicy.MaximumPayloadBytes ||
                payload.Length %
                    wire.RecordSize != 0)
            {
                Interlocked.Increment(
                    ref _rejectedPackets);
                return false;
            }

            binding.Server.ProcessPacket(
                payload,
                binding.PublishBatch);

            Interlocked.Increment(
                ref _acceptedPackets);

            return true;
        }
        finally
        {
            if (decoded &&
                plaintextBytes > 0)
            {
                CryptographicOperations.ZeroMemory(
                    plaintextScratch[..plaintextBytes]);
            }
        }
    }
}
