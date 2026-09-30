using System.Collections.Concurrent;
using System.Net;

namespace DHMP.RawIpv6;

/// <summary>
/// Bounded hot-path router from source IPv6 address to one DHMP session.
/// V1 intentionally supports at most one registered session per source address.
/// </summary>
public sealed class DhmpRawIpv6PeerRouter
{
    private readonly ConcurrentDictionary<
        IPAddress,
        PeerRegistration> _peers = new();

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

        ValidateBinding(binding);

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
                    new PeerRegistration(binding)))
                throw new InvalidOperationException(
                    "Could not register the DHMP peer binding.");
        }
    }

    /// <summary>Stops new routes immediately; does not wait for callbacks already executing.</summary>
    public bool Remove(IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        lock (_registrationGate)
        {
            if (!_peers.TryRemove(remoteAddress, out var registration))
                return false;
            registration.Retire();
            return true;
        }
    }

    /// <summary>
    /// Remove a peer and return its caller-owned binding only after all its active routes exit.
    /// Await before disposing receive resources. Does not dispose callbacks, decoders or send/control paths.
    /// </summary>
    public async Task<DhmpRawIpv6PeerBinding?> RemoveAsync(IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);
        PeerRegistration registration;
        lock (_registrationGate)
        {
            if (!_peers.TryRemove(remoteAddress, out registration!))
                return null;
            registration.Retire();
        }
        await registration.Drained.ConfigureAwait(false);
        return registration.Binding;
    }

    /// <summary>
    /// Atomically publish a new binding for an existing address, then drain and return the old one.
    /// Packets already leased to the old binding finish there; subsequent routes use the replacement.
    /// </summary>
    public async Task<DhmpRawIpv6PeerBinding> ReplaceAsync(DhmpRawIpv6PeerBinding replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateBinding(replacement);
        PeerRegistration previous;
        lock (_registrationGate)
        {
            if (!_peers.TryGetValue(replacement.RemoteAddress, out previous!))
                throw new InvalidOperationException("No DHMP peer is registered for replacement.");
            if (ReferenceEquals(previous.Binding, replacement))
                throw new ArgumentException("Replacement must be a distinct peer binding.", nameof(replacement));
            _peers[replacement.RemoteAddress] = new PeerRegistration(replacement);
            previous.Retire();
        }
        await previous.Drained.ConfigureAwait(false);
        return previous.Binding;
    }

    private void ValidateBinding(DhmpRawIpv6PeerBinding binding)
    {
        if (binding.MaximumNetworkPayloadBytes > _maximumNetworkPayloadBytes)
            throw new ArgumentException(
                "Peer receive policy plus protection overhead exceeds the listener payload ceiling.", nameof(binding));
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

        PeerRegistration registration;
        while (true)
        {
            if (!_peers.TryGetValue(remoteAddress, out registration!))
            {
                Interlocked.Increment(ref _unknownPeerPackets);
                return false;
            }
            if (registration.TryAcquire())
                break;
            // A replacement retired this snapshot. Resolve the current binding instead.
        }

        try
        {
            return RouteLeased(registration.Binding, networkPayload, plaintextScratch);
        }
        finally
        {
            registration.Release();
        }
    }

    private bool RouteLeased(DhmpRawIpv6PeerBinding binding,
        ReadOnlySpan<byte> networkPayload, Span<byte> plaintextScratch)
    {
        if (networkPayload.IsEmpty || networkPayload.Length > binding.MaximumNetworkPayloadBytes ||
            networkPayload.Length > _maximumNetworkPayloadBytes)
        {
            Interlocked.Increment(ref _rejectedPackets);
            return false;
        }
        bool accepted = DhmpRawIpv6PayloadProcessor.TryProcess(binding.Server, binding.Decoder,
            networkPayload, plaintextScratch, binding.PublishBatch, out bool protectionRejected);
        if (accepted)
            Interlocked.Increment(ref _acceptedPackets);
        else if (protectionRejected)
            Interlocked.Increment(ref _protectionRejectedPackets);
        else
            Interlocked.Increment(ref _rejectedPackets);
        return accepted;
    }

    // A lease spans decoding, publication and plaintext cleanup. No packet allocation is introduced.
    private sealed class PeerRegistration(DhmpRawIpv6PeerBinding binding)
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeRoutes;
        private bool _retired;
        public DhmpRawIpv6PeerBinding Binding { get; } = binding;
        public Task Drained => _drained.Task;

        public bool TryAcquire()
        {
            lock (_gate)
            {
                if (_retired) return false;
                _activeRoutes++;
                return true;
            }
        }
        public void Release()
        {
            lock (_gate)
            {
                _activeRoutes--;
                if (_retired && _activeRoutes == 0)
                    _drained.TrySetResult();
            }
        }
        public void Retire()
        {
            lock (_gate)
            {
                _retired = true;
                if (_activeRoutes == 0)
                    _drained.TrySetResult();
            }
        }
    }
}
