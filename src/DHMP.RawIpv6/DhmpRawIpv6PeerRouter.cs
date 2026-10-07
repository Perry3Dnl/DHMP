using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace DHMP.RawIpv6;

/// <summary>
/// Bounded hot-path router from source IPv6 address to one or more DHMP sessions.
/// The normal V1 fast path uses one source address -> one binding. Multiple bindings
/// for the same source are accepted only when every binding declares a nonzero
/// application-owned ConnectionId field.
/// </summary>
public sealed class DhmpRawIpv6PeerRouter
{
    private readonly ConcurrentDictionary<
        IPAddress,
        PeerRegistration[]> _peers = new();

    private readonly object _registrationGate = new();
    private readonly int _maximumPeers;
    private readonly int _maximumNetworkPayloadBytes;
    private int _peerCount;

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

    public int PeerCount =>
        Volatile.Read(ref _peerCount);

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
            if (_peerCount >= _maximumPeers)
                throw new InvalidOperationException(
                    "DHMP raw IPv6 peer limit reached.");

            _peers.TryGetValue(
                binding.RemoteAddress,
                out PeerRegistration[]? current);

            current ??= [];

            if (current.Length != 0)
            {
                if (binding.ConnectionId is null ||
                    binding.ConnectionIdOffset is null ||
                    current.Any(entry =>
                        entry.Binding.ConnectionId is null ||
                        entry.Binding.ConnectionIdOffset is null))
                {
                    throw new InvalidOperationException(
                        "DHMP V1 already has a session bound to this source IPv6 address. " +
                        "Multiple sessions require application-owned ConnectionId routing.");
                }

                if (current.Any(entry =>
                        entry.Binding.ConnectionId ==
                        binding.ConnectionId))
                {
                    throw new InvalidOperationException(
                        "A DHMP session with this source IPv6 address and ConnectionId is already registered.");
                }

                if (current.Any(entry =>
                        entry.Binding.ConnectionIdOffset !=
                        binding.ConnectionIdOffset ||
                        entry.Binding.Server.WireContract.RecordSize !=
                        binding.Server.WireContract.RecordSize))
                {
                    throw new InvalidOperationException(
                        "Duplicate-source DHMP sessions must use the same record size and ConnectionId field offset.");
                }
            }

            var next =
                new PeerRegistration[
                    current.Length + 1];

            Array.Copy(
                current,
                next,
                current.Length);

            next[^1] =
                new PeerRegistration(binding);

            _peers[binding.RemoteAddress] =
                next;

            _peerCount++;
        }
    }

    /// <summary>
    /// Stops all sessions for one source IPv6 address immediately.
    /// Does not wait for callbacks already executing.
    /// </summary>
    public bool Remove(IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        lock (_registrationGate)
        {
            if (!_peers.TryRemove(
                    remoteAddress,
                    out PeerRegistration[]? registrations))
                return false;

            foreach (PeerRegistration registration in registrations)
                registration.Retire();

            _peerCount -= registrations.Length;
            return true;
        }
    }

    /// <summary>
    /// Remove all sessions for one source IPv6 address and wait until active routes exit.
    /// Returns the first binding for backward-compatible single-peer callers.
    /// </summary>
    public async Task<DhmpRawIpv6PeerBinding?> RemoveAsync(
        IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        PeerRegistration[] registrations;

        lock (_registrationGate)
        {
            if (!_peers.TryRemove(
                    remoteAddress,
                    out registrations!))
                return null;

            foreach (PeerRegistration registration in registrations)
                registration.Retire();

            _peerCount -= registrations.Length;
        }

        await Task.WhenAll(
            registrations.Select(
                entry => entry.Drained))
            .ConfigureAwait(false);

        return registrations[0].Binding;
    }

    /// <summary>
    /// Remove one duplicate-source session by its application-owned ConnectionId.
    /// </summary>
    public async Task<DhmpRawIpv6PeerBinding?> RemoveAsync(
        IPAddress remoteAddress,
        ulong connectionId)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        PeerRegistration? removed = null;

        lock (_registrationGate)
        {
            if (!_peers.TryGetValue(
                    remoteAddress,
                    out PeerRegistration[]? current))
                return null;

            int index =
                Array.FindIndex(
                    current,
                    entry =>
                        entry.Binding.ConnectionId ==
                        connectionId);

            if (index < 0)
                return null;

            removed = current[index];

            if (current.Length == 1)
            {
                _peers.TryRemove(
                    remoteAddress,
                    out _);
            }
            else
            {
                var next =
                    new PeerRegistration[
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

                _peers[remoteAddress] =
                    next;
            }

            removed.Retire();
            _peerCount--;
        }

        await removed.Drained.ConfigureAwait(false);
        return removed.Binding;
    }

    /// <summary>
    /// Atomically replace a single registered binding for an address.
    /// Duplicate-source sessions should be removed/re-registered by ConnectionId.
    /// </summary>
    public async Task<DhmpRawIpv6PeerBinding> ReplaceAsync(
        DhmpRawIpv6PeerBinding replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ValidateBinding(replacement);

        PeerRegistration previous;

        lock (_registrationGate)
        {
            if (!_peers.TryGetValue(
                    replacement.RemoteAddress,
                    out PeerRegistration[]? current) ||
                current.Length != 1)
            {
                throw new InvalidOperationException(
                    "ReplaceAsync requires exactly one DHMP session for the source IPv6 address.");
            }

            previous = current[0];

            if (ReferenceEquals(
                    previous.Binding,
                    replacement))
                throw new ArgumentException(
                    "Replacement must be a distinct peer binding.",
                    nameof(replacement));

            _peers[replacement.RemoteAddress] =
            [
                new PeerRegistration(replacement)
            ];

            previous.Retire();
        }

        await previous.Drained.ConfigureAwait(false);
        return previous.Binding;
    }

    private void ValidateBinding(
        DhmpRawIpv6PeerBinding binding)
    {
        if (binding.MaximumNetworkPayloadBytes >
            _maximumNetworkPayloadBytes)
            throw new ArgumentException(
                "Peer receive policy plus protection overhead exceeds the listener payload ceiling.",
                nameof(binding));
    }

    /// <summary>
    /// Route one already-received raw IPv6 protocol payload.
    /// The one-binding fast path is unchanged. Duplicate-source routing only
    /// inspects the configured application-owned ConnectionId field.
    /// </summary>
    public bool TryRoute(
        IPAddress remoteAddress,
        ReadOnlySpan<byte> networkPayload,
        Span<byte> plaintextScratch)
    {
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (!_peers.TryGetValue(
                remoteAddress,
                out PeerRegistration[]? registrations))
        {
            Interlocked.Increment(
                ref _unknownPeerPackets);
            return false;
        }

        if (registrations.Length == 1)
        {
            PeerRegistration registration =
                registrations[0];

            if (!registration.TryAcquire())
                return TryRoute(
                    remoteAddress,
                    networkPayload,
                    plaintextScratch);

            try
            {
                return RouteSingle(
                    registration.Binding,
                    networkPayload,
                    plaintextScratch);
            }
            finally
            {
                registration.Release();
            }
        }

        return RouteDuplicateSource(
            registrations,
            networkPayload,
            plaintextScratch);
    }

    private bool RouteSingle(
        DhmpRawIpv6PeerBinding binding,
        ReadOnlySpan<byte> networkPayload,
        Span<byte> plaintextScratch)
    {
        bool accepted =
            DhmpRawIpv6PayloadProcessor.TryProcess(
                binding.Server,
                binding.Decoder,
                networkPayload,
                plaintextScratch,
                binding.PublishBatch,
                out bool protectionRejected,
                out bool slotSizeIgnored);

        if (accepted)
            Interlocked.Increment(
                ref _acceptedPackets);
        else if (slotSizeIgnored)
            return false;
        else if (protectionRejected)
            Interlocked.Increment(
                ref _protectionRejectedPackets);
        else
            Interlocked.Increment(
                ref _rejectedPackets);

        return accepted;
    }

    private bool RouteDuplicateSource(
        PeerRegistration[] registrations,
        ReadOnlySpan<byte> networkPayload,
        Span<byte> plaintextScratch)
    {
        bool sawProtectionRejection = false;

        foreach (PeerRegistration registration in registrations)
        {
            if (!registration.TryAcquire())
                continue;

            try
            {
                DhmpRawIpv6PeerBinding binding =
                    registration.Binding;

                if (TryRouteDuplicateCandidate(
                        binding,
                        networkPayload,
                        plaintextScratch,
                        out bool protectionRejected))
                {
                    Interlocked.Increment(
                        ref _acceptedPackets);
                    return true;
                }

                sawProtectionRejection |=
                    protectionRejected;
            }
            finally
            {
                registration.Release();
            }
        }

        if (sawProtectionRejection)
            Interlocked.Increment(
                ref _protectionRejectedPackets);
        else
            Interlocked.Increment(
                ref _rejectedPackets);

        return false;
    }

    private static bool TryRouteDuplicateCandidate(
        DhmpRawIpv6PeerBinding binding,
        ReadOnlySpan<byte> networkPayload,
        Span<byte> plaintextScratch,
        out bool protectionRejected)
    {
        protectionRejected = false;

        ulong expectedId =
            binding.ConnectionId ??
            throw new InvalidOperationException(
                "Duplicate-source routing requires ConnectionId bindings.");

        int connectionIdOffset =
            binding.ConnectionIdOffset ??
            throw new InvalidOperationException(
                "Duplicate-source routing requires a ConnectionId field offset.");

        int maximumPlaintext =
            binding.Server.ReceivePolicy.MaximumPayloadBytes;

        if (networkPayload.IsEmpty ||
            networkPayload.Length >
                checked(
                    maximumPlaintext +
                    (binding.Decoder?.OverheadBytes ?? 0)))
            return false;

        ReadOnlySpan<byte> payload =
            networkPayload;

        try
        {
            if (binding.Decoder is not null)
            {
                if (plaintextScratch.Length <
                    maximumPlaintext)
                    throw new ArgumentException(
                        "Plaintext scratch buffer is smaller than the receive policy.",
                        nameof(plaintextScratch));

                if (!binding.Decoder.TryDecode(
                        networkPayload,
                        plaintextScratch,
                        out int plaintextBytes))
                {
                    protectionRejected = true;
                    return false;
                }

                if (plaintextBytes <= 0 ||
                    plaintextBytes > maximumPlaintext ||
                    plaintextBytes > plaintextScratch.Length)
                    return false;

                payload =
                    plaintextScratch[
                        ..plaintextBytes];
            }

            int recordSize =
                binding.Server.WireContract.RecordSize;

            if (payload.Length < recordSize ||
                payload.Length > maximumPlaintext)
                return false;

            int completeBytes =
                payload.Length /
                recordSize *
                recordSize;

            for (int offset = 0;
                 offset < completeBytes;
                 offset += recordSize)
            {
                ulong actualId =
                    BinaryPrimitives.ReadUInt64BigEndian(
                        payload.Slice(
                            offset + connectionIdOffset,
                            sizeof(ulong)));

                if (actualId != expectedId)
                    return false;
            }

            binding.Server.ProcessPacket(
                payload,
                binding.PublishBatch);

            return true;
        }
        finally
        {
            if (binding.Decoder is not null)
                CryptographicOperations.ZeroMemory(
                    plaintextScratch);
        }
    }

    private sealed class PeerRegistration(
        DhmpRawIpv6PeerBinding binding)
    {
        private readonly TaskCompletionSource _drained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _activeRoutes;
        private int _retired;

        public DhmpRawIpv6PeerBinding Binding { get; } =
            binding;

        public Task Drained =>
            _drained.Task;

        public bool TryAcquire()
        {
            if (Volatile.Read(ref _retired) != 0)
                return false;

            Interlocked.Increment(ref _activeRoutes);

            // Retirement may race the increment. In that case this lease never
            // enters the packet path and immediately releases itself.
            if (Volatile.Read(ref _retired) == 0)
                return true;

            Release();
            return false;
        }

        public void Release()
        {
            int remaining =
                Interlocked.Decrement(ref _activeRoutes);

            if (remaining == 0 &&
                Volatile.Read(ref _retired) != 0)
            {
                _drained.TrySetResult();
            }
        }

        public void Retire()
        {
            Interlocked.Exchange(ref _retired, 1);

            if (Volatile.Read(ref _activeRoutes) == 0)
                _drained.TrySetResult();
        }
    }
}
