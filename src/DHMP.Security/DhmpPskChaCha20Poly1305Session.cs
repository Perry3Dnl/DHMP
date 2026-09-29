using System.Buffers.Binary;
using System.Security.Cryptography;
using DHMP.Protocol;

namespace DHMP.Security;

/// <summary>
/// Explicit DHMP PSK security profile using HKDF-SHA256 derived directional keys
/// and ChaCha20-Poly1305 authenticated encryption.
/// </summary>
public sealed class DhmpPskChaCha20Poly1305Session :
    IDhmpPacketDecoder,
    IDisposable
{
    public const int CounterSize = 8;
    public const int TagSize = 16;
    public const int NonceSize = 12;
    public const int KeySize = 32;
    public const int Overhead = CounterSize + TagSize;

    public const int CongestionFeedbackBodySize = 48;
    public const int CongestionFeedbackTagSize = 16;
    public const int CongestionFeedbackPacketSize =
        CongestionFeedbackBodySize +
        CongestionFeedbackTagSize;

    public const int PathProbeBodySize = 64;
    public const int PathProbeTagSize = 16;
    public const int PathProbePacketSize =
        PathProbeBodySize +
        PathProbeTagSize;

    private readonly ChaCha20Poly1305 _sendCipher;
    private readonly ChaCha20Poly1305 _receiveCipher;
    private readonly uint _sendNoncePrefix;
    private readonly uint _receiveNoncePrefix;
    private readonly byte[] _sessionIdBytes;
    private readonly byte[] _sendFeedbackKey;
    private readonly byte[] _receiveFeedbackKey;
    private readonly byte[] _sendPathKey;
    private readonly byte[] _receivePathKey;
    private readonly DhmpReplayWindow _replayWindow = new();
    private readonly DhmpReplayWindow _feedbackReplayWindow = new();
    private readonly DhmpReplayWindow _pathRequestReplayWindow = new();
    private readonly DhmpReplayWindow _pathResponseReplayWindow = new();
    private readonly object _receiveTelemetryGate = new();

    private long _acceptedDataPackets;
    private long _reorderedDataPackets;
    private long _replayRejectedDataPackets;
    private long _authenticationFailures;

    private ulong _sendCounter;
    private ulong _sendFeedbackCounter;
    private int _disposed;

    public DhmpPskChaCha20Poly1305Session(
        DhmpPreSharedKey preSharedKey,
        Guid sessionId,
        DhmpSecurityRole role)
    {
        ArgumentNullException.ThrowIfNull(preSharedKey);

        if (!ChaCha20Poly1305.IsSupported)
            throw new PlatformNotSupportedException(
                "ChaCha20-Poly1305 is not supported on this platform.");

        if (sessionId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(sessionId));

        if (role is not DhmpSecurityRole.Initiator and
            not DhmpSecurityRole.Responder)
            throw new ArgumentOutOfRangeException(nameof(role));

        SessionId = sessionId;
        Role = role;

        _sessionIdBytes = new byte[16];

        if (!sessionId.TryWriteBytes(
                _sessionIdBytes,
                bigEndian: true,
                out int sessionBytes) ||
            sessionBytes != 16)
            throw new InvalidOperationException(
                "Could not encode DHMP security session ID.");

        Span<byte> initiatorToResponder =
            stackalloc byte[KeySize + sizeof(uint)];

        Span<byte> responderToInitiator =
            stackalloc byte[KeySize + sizeof(uint)];

        _sendFeedbackKey = new byte[KeySize];
        _receiveFeedbackKey = new byte[KeySize];
        _sendPathKey = new byte[KeySize];
        _receivePathKey = new byte[KeySize];

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            preSharedKey.KeySpan,
            initiatorToResponder,
            _sessionIdBytes,
            "DHMP-S1-I2R"u8);

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            preSharedKey.KeySpan,
            responderToInitiator,
            _sessionIdBytes,
            "DHMP-S1-R2I"u8);

        Span<byte> initiatorFeedback =
            stackalloc byte[KeySize];

        Span<byte> responderFeedback =
            stackalloc byte[KeySize];

        Span<byte> initiatorPath =
            stackalloc byte[KeySize];

        Span<byte> responderPath =
            stackalloc byte[KeySize];

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            preSharedKey.KeySpan,
            initiatorFeedback,
            _sessionIdBytes,
            "DHMP-S1-FB-I2R"u8);

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            preSharedKey.KeySpan,
            responderFeedback,
            _sessionIdBytes,
            "DHMP-S1-FB-R2I"u8);

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            preSharedKey.KeySpan,
            initiatorPath,
            _sessionIdBytes,
            "DHMP-S1-PATH-I2R"u8);

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            preSharedKey.KeySpan,
            responderPath,
            _sessionIdBytes,
            "DHMP-S1-PATH-R2I"u8);

        ReadOnlySpan<byte> sendMaterial =
            role == DhmpSecurityRole.Initiator
                ? initiatorToResponder
                : responderToInitiator;

        ReadOnlySpan<byte> receiveMaterial =
            role == DhmpSecurityRole.Initiator
                ? responderToInitiator
                : initiatorToResponder;

        ReadOnlySpan<byte> sendFeedback =
            role == DhmpSecurityRole.Initiator
                ? initiatorFeedback
                : responderFeedback;

        ReadOnlySpan<byte> receiveFeedback =
            role == DhmpSecurityRole.Initiator
                ? responderFeedback
                : initiatorFeedback;

        ReadOnlySpan<byte> sendPath =
            role == DhmpSecurityRole.Initiator
                ? initiatorPath
                : responderPath;

        ReadOnlySpan<byte> receivePath =
            role == DhmpSecurityRole.Initiator
                ? responderPath
                : initiatorPath;

        byte[] sendKey =
            sendMaterial[..KeySize].ToArray();

        byte[] receiveKey =
            receiveMaterial[..KeySize].ToArray();

        sendFeedback.CopyTo(_sendFeedbackKey);
        receiveFeedback.CopyTo(_receiveFeedbackKey);
        sendPath.CopyTo(_sendPathKey);
        receivePath.CopyTo(_receivePathKey);

        _sendNoncePrefix =
            BinaryPrimitives.ReadUInt32BigEndian(
                sendMaterial.Slice(
                    KeySize,
                    sizeof(uint)));

        _receiveNoncePrefix =
            BinaryPrimitives.ReadUInt32BigEndian(
                receiveMaterial.Slice(
                    KeySize,
                    sizeof(uint)));

        _sendCipher =
            new ChaCha20Poly1305(sendKey);

        _receiveCipher =
            new ChaCha20Poly1305(receiveKey);

        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(receiveKey);
        CryptographicOperations.ZeroMemory(initiatorToResponder);
        CryptographicOperations.ZeroMemory(responderToInitiator);
        CryptographicOperations.ZeroMemory(initiatorFeedback);
        CryptographicOperations.ZeroMemory(responderFeedback);
        CryptographicOperations.ZeroMemory(initiatorPath);
        CryptographicOperations.ZeroMemory(responderPath);
    }

    public Guid SessionId { get; }
    public DhmpSecurityRole Role { get; }

    int IDhmpPacketDecoder.OverheadBytes =>
        Overhead;

    public int Protect(
        ReadOnlySpan<byte> plaintext,
        Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (plaintext.IsEmpty)
            throw new ArgumentException(
                "Protected DHMP payload cannot be empty.",
                nameof(plaintext));

        int required = checked(
            plaintext.Length + Overhead);

        if (destination.Length < required)
            throw new ArgumentException(
                "Destination is too small for the protected DHMP packet.",
                nameof(destination));

        if (_sendCounter == ulong.MaxValue)
            throw new CryptographicException(
                "DHMP security packet counter exhausted; rekey before sending more data.");

        ulong counter = ++_sendCounter;

        Span<byte> counterBytes =
            destination[..CounterSize];

        BinaryPrimitives.WriteUInt64BigEndian(
            counterBytes,
            counter);

        Span<byte> nonce =
            stackalloc byte[NonceSize];

        BinaryPrimitives.WriteUInt32BigEndian(
            nonce[..4],
            _sendNoncePrefix);

        BinaryPrimitives.WriteUInt64BigEndian(
            nonce[4..],
            counter);

        Span<byte> ciphertext =
            destination.Slice(
                CounterSize,
                plaintext.Length);

        Span<byte> tag =
            destination.Slice(
                CounterSize + plaintext.Length,
                TagSize);

        _sendCipher.Encrypt(
            nonce,
            plaintext,
            ciphertext,
            tag,
            _sessionIdBytes);

        CryptographicOperations.ZeroMemory(nonce);

        return required;
    }

    public bool TryDecode(
        ReadOnlySpan<byte> packet,
        Span<byte> plaintextDestination,
        out int plaintextBytes)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        plaintextBytes = 0;

        if (packet.Length <= Overhead)
            return false;

        int ciphertextBytes =
            packet.Length - Overhead;

        if (plaintextDestination.Length <
            ciphertextBytes)
            throw new ArgumentException(
                "Plaintext destination is too small.",
                nameof(plaintextDestination));

        ulong counter =
            BinaryPrimitives.ReadUInt64BigEndian(
                packet[..CounterSize]);

        if (counter == 0)
            return false;

        Span<byte> nonce =
            stackalloc byte[NonceSize];

        BinaryPrimitives.WriteUInt32BigEndian(
            nonce[..4],
            _receiveNoncePrefix);

        BinaryPrimitives.WriteUInt64BigEndian(
            nonce[4..],
            counter);

        ReadOnlySpan<byte> ciphertext =
            packet.Slice(
                CounterSize,
                ciphertextBytes);

        ReadOnlySpan<byte> tag =
            packet.Slice(
                CounterSize + ciphertextBytes,
                TagSize);

        Span<byte> plaintext =
            plaintextDestination[..ciphertextBytes];

        try
        {
            _receiveCipher.Decrypt(
                nonce,
                ciphertext,
                tag,
                plaintext,
                _sessionIdBytes);
        }
        catch (CryptographicException)
        {
            Interlocked.Increment(
                ref _authenticationFailures);

            CryptographicOperations.ZeroMemory(
                plaintext);

            CryptographicOperations.ZeroMemory(
                nonce);

            return false;
        }

        CryptographicOperations.ZeroMemory(nonce);

        lock (_receiveTelemetryGate)
        {
            if (!_replayWindow.TryAccept(
                    counter,
                    out var decision))
            {
                _replayRejectedDataPackets++;

                CryptographicOperations.ZeroMemory(
                    plaintext);

                return false;
            }

            _acceptedDataPackets++;

            if (decision ==
                DhmpReplayDecision.AcceptedReordered)
                _reorderedDataPackets++;
        }

        plaintextBytes = ciphertextBytes;

        return true;
    }

    public DhmpSecureReceiveSnapshot GetReceiveSnapshot()
    {
        lock (_receiveTelemetryGate)
        {
            DhmpReplayWindowSnapshot replay =
                _replayWindow.GetSnapshot();

            return new DhmpSecureReceiveSnapshot(
                replay.HighestCounter,
                replay.WindowSpan,
                replay.MissingWithinWindow,
                _acceptedDataPackets,
                _reorderedDataPackets,
                _replayRejectedDataPackets,
                Interlocked.Read(
                    ref _authenticationFailures));
        }
    }

    /// <summary>
    /// Create one direction-authenticated, replay-protected congestion feedback packet.
    /// </summary>
    public int EncodeCongestionFeedback(
        DhmpCongestionFeedback feedback,
        Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (destination.Length <
            CongestionFeedbackPacketSize)
            throw new ArgumentException(
                $"DHMP congestion feedback requires {CongestionFeedbackPacketSize} bytes.",
                nameof(destination));

        if (_sendFeedbackCounter ==
            ulong.MaxValue)
            throw new CryptographicException(
                "DHMP congestion feedback counter exhausted; rekey required.");

        ulong sequence =
            ++_sendFeedbackCounter;

        Span<byte> packet =
            destination[
                ..CongestionFeedbackPacketSize];

        packet.Clear();

        "DHMF"u8.CopyTo(packet);
        packet[4] = 1;
        packet[5] =
            (byte)feedback.Pressure;

        BinaryPrimitives.WriteUInt16BigEndian(
            packet.Slice(6, 2),
            feedback.RateScalePermille);

        _sessionIdBytes.CopyTo(
            packet.Slice(8, 16));

        BinaryPrimitives.WriteUInt64BigEndian(
            packet.Slice(24, 8),
            sequence);

        BinaryPrimitives.WriteUInt32BigEndian(
            packet.Slice(32, 4),
            checked((uint)feedback.PendingBatches));

        BinaryPrimitives.WriteUInt32BigEndian(
            packet.Slice(36, 4),
            checked((uint)feedback.Capacity));

        BinaryPrimitives.WriteUInt64BigEndian(
            packet.Slice(40, 8),
            checked((ulong)feedback.LostPendingWork));

        Span<byte> fullTag =
            stackalloc byte[32];

        HMACSHA256.HashData(
            _sendFeedbackKey,
            packet[..CongestionFeedbackBodySize],
            fullTag);

        fullTag[..CongestionFeedbackTagSize]
            .CopyTo(
                packet.Slice(
                    CongestionFeedbackBodySize,
                    CongestionFeedbackTagSize));

        CryptographicOperations.ZeroMemory(
            fullTag);

        return CongestionFeedbackPacketSize;
    }

    public bool TryDecodeCongestionFeedback(
        ReadOnlySpan<byte> packet,
        out DhmpCongestionFeedback feedback)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        feedback = default;

        if (packet.Length !=
                CongestionFeedbackPacketSize ||
            !packet[..4].SequenceEqual("DHMF"u8) ||
            packet[4] != 1 ||
            !packet.Slice(8, 16)
                .SequenceEqual(_sessionIdBytes))
            return false;

        Span<byte> fullTag =
            stackalloc byte[32];

        HMACSHA256.HashData(
            _receiveFeedbackKey,
            packet[..CongestionFeedbackBodySize],
            fullTag);

        bool authenticated =
            CryptographicOperations.FixedTimeEquals(
                fullTag[..CongestionFeedbackTagSize],
                packet.Slice(
                    CongestionFeedbackBodySize,
                    CongestionFeedbackTagSize));

        CryptographicOperations.ZeroMemory(
            fullTag);

        if (!authenticated)
            return false;

        ulong sequence =
            BinaryPrimitives.ReadUInt64BigEndian(
                packet.Slice(24, 8));

        if (sequence == 0)
            return false;

        ulong lost =
            BinaryPrimitives.ReadUInt64BigEndian(
                packet.Slice(40, 8));

        if (lost > long.MaxValue)
            return false;

        try
        {
            var decoded =
                new DhmpCongestionFeedback(
                    (DhmpCongestionPressure)packet[5],
                    BinaryPrimitives.ReadUInt16BigEndian(
                        packet.Slice(6, 2)),
                    checked((int)
                        BinaryPrimitives.ReadUInt32BigEndian(
                            packet.Slice(32, 4))),
                    checked((int)
                        BinaryPrimitives.ReadUInt32BigEndian(
                            packet.Slice(36, 4))),
                    checked((long)lost));

            if (!_feedbackReplayWindow
                    .TryAccept(sequence))
                return false;

            feedback = decoded;

            return true;
        }
        catch (Exception error)
            when (error is
                ArgumentException or
                OverflowException)
        {
            return false;
        }
    }

    public int EncodePathProbe(
        DhmpPathProbeMessage message,
        Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        if (destination.Length < PathProbePacketSize)
            throw new ArgumentException(
                $"DHMP path probe requires {PathProbePacketSize} bytes.",
                nameof(destination));

        Span<byte> packet =
            destination[..PathProbePacketSize];

        packet.Clear();
        "DHMR"u8.CopyTo(packet);

        packet[4] = 1;
        packet[5] = (byte)message.Type;

        _sessionIdBytes.CopyTo(
            packet.Slice(8, 16));

        BinaryPrimitives.WriteUInt64BigEndian(
            packet.Slice(24, 8),
            message.ProbeId);

        BinaryPrimitives.WriteUInt64BigEndian(
            packet.Slice(32, 8),
            message.SenderTimestamp);

        BinaryPrimitives.WriteUInt64BigEndian(
            packet.Slice(40, 8),
            message.HighestPacketCounter);

        BinaryPrimitives.WriteUInt32BigEndian(
            packet.Slice(48, 4),
            checked((uint)message.WindowSpan));

        BinaryPrimitives.WriteUInt32BigEndian(
            packet.Slice(52, 4),
            checked((uint)message.MissingWithinWindow));

        BinaryPrimitives.WriteUInt64BigEndian(
            packet.Slice(56, 8),
            checked((ulong)message.AcceptedPackets));

        Span<byte> fullTag =
            stackalloc byte[32];

        HMACSHA256.HashData(
            _sendPathKey,
            packet[..PathProbeBodySize],
            fullTag);

        fullTag[..PathProbeTagSize]
            .CopyTo(
                packet.Slice(
                    PathProbeBodySize,
                    PathProbeTagSize));

        CryptographicOperations.ZeroMemory(fullTag);

        return PathProbePacketSize;
    }

    public bool TryDecodePathProbe(
        ReadOnlySpan<byte> packet,
        out DhmpPathProbeMessage message)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        message = default;

        if (packet.Length != PathProbePacketSize ||
            !packet[..4].SequenceEqual("DHMR"u8) ||
            packet[4] != 1 ||
            !packet.Slice(8, 16)
                .SequenceEqual(_sessionIdBytes))
            return false;

        Span<byte> fullTag =
            stackalloc byte[32];

        HMACSHA256.HashData(
            _receivePathKey,
            packet[..PathProbeBodySize],
            fullTag);

        bool authenticated =
            CryptographicOperations.FixedTimeEquals(
                fullTag[..PathProbeTagSize],
                packet.Slice(
                    PathProbeBodySize,
                    PathProbeTagSize));

        CryptographicOperations.ZeroMemory(fullTag);

        if (!authenticated)
            return false;

        try
        {
            var decoded =
                new DhmpPathProbeMessage(
                    (DhmpPathProbeType)packet[5],
                    BinaryPrimitives.ReadUInt64BigEndian(
                        packet.Slice(24, 8)),
                    BinaryPrimitives.ReadUInt64BigEndian(
                        packet.Slice(32, 8)),
                    BinaryPrimitives.ReadUInt64BigEndian(
                        packet.Slice(40, 8)),
                    checked((int)
                        BinaryPrimitives.ReadUInt32BigEndian(
                            packet.Slice(48, 4))),
                    checked((int)
                        BinaryPrimitives.ReadUInt32BigEndian(
                            packet.Slice(52, 4))),
                    checked((long)
                        BinaryPrimitives.ReadUInt64BigEndian(
                            packet.Slice(56, 8))));

            DhmpReplayWindow replay =
                decoded.Type == DhmpPathProbeType.Request
                    ? _pathRequestReplayWindow
                    : _pathResponseReplayWindow;

            if (!replay.TryAccept(decoded.ProbeId))
                return false;

            message = decoded;
            return true;
        }
        catch (Exception error)
            when (error is
                ArgumentException or
                OverflowException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(
                ref _disposed,
                1) != 0)
            return;

        _sendCipher.Dispose();
        _receiveCipher.Dispose();

        CryptographicOperations.ZeroMemory(
            _sessionIdBytes);

        CryptographicOperations.ZeroMemory(
            _sendFeedbackKey);

        CryptographicOperations.ZeroMemory(
            _receiveFeedbackKey);

        CryptographicOperations.ZeroMemory(
            _sendPathKey);

        CryptographicOperations.ZeroMemory(
            _receivePathKey);
    }
}
