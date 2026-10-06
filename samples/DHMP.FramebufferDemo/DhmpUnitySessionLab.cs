using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.Server;

public sealed class DhmpUnitySessionLab : IAsyncDisposable
{
    public const int RecordSize = 64;
    public const int MaximumPayloadBytes = 1408;
    public const int SimulatedTickRate = 20;
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<long, PlayerSession> _sessions = new();
    private readonly ConcurrentDictionary<long, PlayerSnapshot> _latest = new();
    private readonly ConcurrentDictionary<long, PlayerSnapshot[]> _nativeHistory = new();
    private readonly DhmpWireContract _wireContract = new(RecordSize);
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private long _nextPlayerId;
    private long _totalConnections;
    private long _messagesReceived;
    private long _bytesReceived;
    private long _previousRateMessages;
    private long _previousRateBytes;
    private long _rateSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private long _lastSecondMessages;
    private long _lastSecondBytes;

    public UnityConnectResult Connect(string? name)
    {
        long playerId = Interlocked.Increment(ref _nextPlayerId);
        string displayName = NormalizeName(name, playerId);

        var server = new DhmpServer(
            _wireContract,
            new DhmpReceivePolicy(
                DhmpProcessingMode.Latest,
                MaximumPayloadBytes,
                nativeSmoothing: true));

        var sender = new LoopbackSender(
            server,
            PublishBatch);

        var client = new DhmpClient(
            sender,
            _wireContract,
            new DhmpSendPolicy(
                pmax: 10_000,
                maximumPayloadBytes: MaximumPayloadBytes,
                ratePolicy: DhmpRatePolicy.SmoothPacing));

        var session =
            new PlayerSession(
                playerId,
                displayName,
                client,
                server);

        if (!_sessions.TryAdd(playerId, session))
            throw new InvalidOperationException("Could not register simulated player.");

        Interlocked.Increment(ref _totalConnections);

        PlayerSnapshot initial = session.Snapshot();
        _latest[playerId] = initial;
        _nativeHistory[playerId] = [initial];

        session.Start(() => SendCurrentAsync(session));

        return new UnityConnectResult(
            playerId,
            displayName,
            SimulatedTickRate,
            RecordSize);
    }

    public async Task<bool> DisconnectAsync(long playerId)
    {
        if (!_sessions.TryRemove(playerId, out PlayerSession? session))
            return false;

        await session.DisposeAsync().ConfigureAwait(false);
        _latest.TryRemove(playerId, out _);
        _nativeHistory.TryRemove(playerId, out _);
        return true;
    }

    public async Task<PlayerSnapshot?> SendAsync(
        long playerId,
        UnityPlayerStateInput input,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(playerId, out PlayerSession? session))
            return null;

        session.Update(input);
        await SendCurrentAsync(session, cancellationToken).ConfigureAwait(false);

        return _latest.TryGetValue(playerId, out PlayerSnapshot? snapshot) &&
               snapshot is not null
            ? snapshot
            : session.Snapshot();
    }

    public UnityReceiveResult? Receive(long playerId)
    {
        if (!_sessions.ContainsKey(playerId))
            return null;

        PlayerSnapshot[] players = _latest.Values
            .OrderBy(static player => player.PlayerId)
            .ToArray();

        return new UnityReceiveResult(
            playerId,
            DateTimeOffset.UtcNow,
            players);
    }

    public UnityServerStats Stats()
    {
        long nowTimestamp = Stopwatch.GetTimestamp();
        int connected = _sessions.Count;
        int active = 0;

        foreach (PlayerSession session in _sessions.Values)
        {
            long last = session.LastMessageTimestamp;
            if (last != 0 &&
                Stopwatch.GetElapsedTime(last, nowTimestamp) <= ActiveWindow)
                active++;
        }

        RotateRateCounters();

        return new UnityServerStats(
            connected,
            active,
            Interlocked.Read(ref _totalConnections),
            Interlocked.Read(ref _messagesReceived),
            Interlocked.Read(ref _bytesReceived),
            Interlocked.Read(ref _lastSecondMessages),
            Interlocked.Read(ref _lastSecondBytes),
            _uptime.Elapsed,
            SimulatedTickRate,
            RecordSize);
    }

    public UnityPlayerView[] Players() =>
        _latest.Values
            .OrderBy(static player => player.PlayerId)
            .Select(snapshot =>
                new UnityPlayerView(
                    snapshot.PlayerId,
                    snapshot.Sequence,
                    snapshot.X,
                    snapshot.Y,
                    snapshot.Z,
                    snapshot.RotationX,
                    snapshot.RotationY,
                    snapshot.RotationZ,
                    snapshot.RotationW,
                    snapshot.SentAtUnixMilliseconds,
                    snapshot.Flags,
                    snapshot.LastMessageUtc,
                    _nativeHistory.TryGetValue(
                        snapshot.PlayerId,
                        out PlayerSnapshot[]? history)
                        ? history
                        : [snapshot]))
            .ToArray();

    public async ValueTask DisposeAsync()
    {
        PlayerSession[] sessions = _sessions.Values.ToArray();
        _sessions.Clear();
        _latest.Clear();
        _nativeHistory.Clear();

        foreach (PlayerSession session in sessions)
            await session.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask SendCurrentAsync(
        PlayerSession session,
        CancellationToken cancellationToken = default)
    {
        await session.SendGate.WaitAsync(
            cancellationToken).ConfigureAwait(false);

        try
        {
            PlayerSnapshot snapshot =
                session.NextSnapshot();

            byte[] record =
                GC.AllocateUninitializedArray<byte>(
                    RecordSize);

            Encode(
                snapshot,
                record);

            await session.Client.SendAsync(
                record,
                cancellationToken).ConfigureAwait(false);

            CaptureNativeHistory(session);
        }
        finally
        {
            session.SendGate.Release();
        }
    }

    private void CaptureNativeHistory(
        PlayerSession session)
    {
        Span<byte> window =
            stackalloc byte[
                RecordSize *
                DhmpLatestStateWindow.Capacity];

        int count =
            session.Server.CopyNativeSmoothingWindow(
                window);

        if (count == 0)
            return;

        var history =
            new PlayerSnapshot[count];

        for (int index = 0;
             index < count;
             index++)
        {
            history[index] =
                Decode(
                    window.Slice(
                        index * RecordSize,
                        RecordSize));
        }

        _nativeHistory[session.PlayerId] =
            history;
    }

    private void PublishBatch(
        ReadOnlySpan<byte> batch)
    {
        PlayerSnapshot snapshot =
            Decode(batch);

        if (_sessions.TryGetValue(
                snapshot.PlayerId,
                out PlayerSession? session))
        {
            session.MarkMessageReceived();

            _latest[snapshot.PlayerId] =
                snapshot with
                {
                    LastMessageUtc =
                        DateTimeOffset.UtcNow
                };
        }

        Interlocked.Increment(
            ref _messagesReceived);

        Interlocked.Add(
            ref _bytesReceived,
            RecordSize);

        AddRate(
            1,
            RecordSize);
    }

    private void AddRate(long messages, long bytes)
    {
        RotateRateCounters();
        Interlocked.Add(ref _previousRateMessages, messages);
        Interlocked.Add(ref _previousRateBytes, bytes);
    }

    private void RotateRateCounters()
    {
        long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long observed = Interlocked.Read(ref _rateSecond);
        if (second == observed)
            return;

        if (Interlocked.CompareExchange(ref _rateSecond, second, observed) != observed)
            return;

        Interlocked.Exchange(
            ref _lastSecondMessages,
            Interlocked.Exchange(ref _previousRateMessages, 0));
        Interlocked.Exchange(
            ref _lastSecondBytes,
            Interlocked.Exchange(ref _previousRateBytes, 0));
    }

    private static void Encode(
        PlayerSnapshot snapshot,
        Span<byte> span)
    {
        if (span.Length != RecordSize)
            throw new ArgumentException(
                $"Expected a {RecordSize}-byte destination.",
                nameof(span));

        BinaryPrimitives.WriteInt64LittleEndian(span[0..8], snapshot.PlayerId);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..16], snapshot.Sequence);
        WriteSingle(span[16..20], snapshot.X);
        WriteSingle(span[20..24], snapshot.Y);
        WriteSingle(span[24..28], snapshot.Z);
        WriteSingle(span[28..32], snapshot.RotationX);
        WriteSingle(span[32..36], snapshot.RotationY);
        WriteSingle(span[36..40], snapshot.RotationZ);
        WriteSingle(span[40..44], snapshot.RotationW);
        BinaryPrimitives.WriteInt64LittleEndian(
            span[44..52],
            snapshot.SentAtUnixMilliseconds);
        BinaryPrimitives.WriteInt32LittleEndian(span[52..56], snapshot.Flags);
    }

    private static PlayerSnapshot Decode(ReadOnlySpan<byte> span)
    {
        if (span.Length != RecordSize)
            throw new ArgumentException(
                $"Expected exactly one {RecordSize}-byte player record.",
                nameof(span));

        return new PlayerSnapshot(
            BinaryPrimitives.ReadInt64LittleEndian(span[0..8]),
            BinaryPrimitives.ReadInt64LittleEndian(span[8..16]),
            ReadSingle(span[16..20]),
            ReadSingle(span[20..24]),
            ReadSingle(span[24..28]),
            ReadSingle(span[28..32]),
            ReadSingle(span[32..36]),
            ReadSingle(span[36..40]),
            ReadSingle(span[40..44]),
            BinaryPrimitives.ReadInt64LittleEndian(span[44..52]),
            BinaryPrimitives.ReadInt32LittleEndian(span[52..56]),
            DateTimeOffset.UtcNow);
    }

    private static void WriteSingle(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(source));

    private static string NormalizeName(string? name, long playerId)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return $"WebPlayer-{playerId}";

        return trimmed.Length <= 32
            ? trimmed
            : trimmed[..32];
    }

    private sealed class LoopbackSender : IDhmpPacketSender
    {
        private readonly DhmpServer _server;
        private readonly Action<ReadOnlySpan<byte>> _publish;

        public LoopbackSender(
            DhmpServer server,
            Action<ReadOnlySpan<byte>> publish)
        {
            _server = server;
            _publish = publish;
        }

        public int MaximumPayloadBytes =>
            DhmpUnitySessionLab.MaximumPayloadBytes;

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _server.ProcessPacket(
                payload.Span,
                _publish);

            return ValueTask.CompletedTask;
        }
    }

    private sealed class PlayerSession : IAsyncDisposable
    {
        private readonly object _stateGate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly long _connectedAtUnixMilliseconds =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        private UnityPlayerStateInput _state =
            new(0, 0, 0, 0, 0, 0, 1, 0);

        private long _sequence;
        private Task? _sendLoop;

        public PlayerSession(
            long playerId,
            string name,
            DhmpClient client,
            DhmpServer server)
        {
            PlayerId = playerId;
            Name = name;
            Client = client;
            Server = server;
        }

        public long PlayerId { get; }
        public string Name { get; }
        public DhmpClient Client { get; }
        public DhmpServer Server { get; }
        public SemaphoreSlim SendGate { get; } = new(1, 1);
        public long LastMessageTimestamp { get; private set; }

        public void Start(Func<ValueTask> sendCurrent)
        {
            _sendLoop = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(
                    TimeSpan.FromSeconds(
                        1d /
                        SimulatedTickRate));

                try
                {
                    await sendCurrent().ConfigureAwait(false);

                    while (await timer.WaitForNextTickAsync(_lifetime.Token)
                        .ConfigureAwait(false))
                    {
                        await sendCurrent().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                    when (_lifetime.IsCancellationRequested)
                {
                }
            });
        }

        public void Update(UnityPlayerStateInput state)
        {
            lock (_stateGate)
                _state = state;
        }

        public PlayerSnapshot NextSnapshot()
        {
            lock (_stateGate)
            {
                long sequence = ++_sequence;
                long now =
                    DateTimeOffset.UtcNow
                        .ToUnixTimeMilliseconds();

                return new PlayerSnapshot(
                    PlayerId,
                    sequence,
                    _state.X,
                    _state.Y,
                    _state.Z,
                    _state.RotationX,
                    _state.RotationY,
                    _state.RotationZ,
                    _state.RotationW,
                    now,
                    _state.Flags,
                    DateTimeOffset.FromUnixTimeMilliseconds(now));
            }
        }

        public PlayerSnapshot Snapshot()
        {
            lock (_stateGate)
            {
                return new PlayerSnapshot(
                    PlayerId,
                    _sequence,
                    _state.X,
                    _state.Y,
                    _state.Z,
                    _state.RotationX,
                    _state.RotationY,
                    _state.RotationZ,
                    _state.RotationW,
                    _connectedAtUnixMilliseconds,
                    _state.Flags,
                    DateTimeOffset.FromUnixTimeMilliseconds(
                        _connectedAtUnixMilliseconds));
            }
        }

        public void MarkMessageReceived() =>
            LastMessageTimestamp =
                Stopwatch.GetTimestamp();

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();

            if (_sendLoop is not null)
            {
                try
                {
                    await _sendLoop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            SendGate.Dispose();
            _lifetime.Dispose();
        }
    }
}

public sealed record UnityConnectRequest(string? Name);

public sealed record UnityConnectResult(
    long PlayerId,
    string Name,
    int TickRate,
    int RecordSize);

public sealed record UnityPlayerStateInput(
    float X,
    float Y,
    float Z,
    float RotationX,
    float RotationY,
    float RotationZ,
    float RotationW,
    int Flags);

public sealed record PlayerSnapshot(
    long PlayerId,
    long Sequence,
    float X,
    float Y,
    float Z,
    float RotationX,
    float RotationY,
    float RotationZ,
    float RotationW,
    long SentAtUnixMilliseconds,
    int Flags,
    DateTimeOffset LastMessageUtc);

public sealed record UnityReceiveResult(
    long PlayerId,
    DateTimeOffset ServerTimeUtc,
    PlayerSnapshot[] Players);

public sealed record UnityServerStats(
    int ConnectedConnections,
    int ActiveConnections,
    long TotalConnections,
    long MessagesReceived,
    long BytesReceived,
    long MessagesPerSecond,
    long BytesPerSecond,
    TimeSpan Uptime,
    int SimulatedTickRate,
    int RecordSize);

public sealed record UnityPlayerView(
    long PlayerId,
    long Sequence,
    float X,
    float Y,
    float Z,
    float RotationX,
    float RotationY,
    float RotationZ,
    float RotationW,
    long SentAtUnixMilliseconds,
    int Flags,
    DateTimeOffset LastMessageUtc,
    PlayerSnapshot[] NativeHistory);
