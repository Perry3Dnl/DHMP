using System.Buffers.Binary;
using System.Security.Cryptography;
using DHMP.Protocol;

namespace DHMP.Security;

/// <summary>
/// Explicit DHMP PSK security profile using HKDF-SHA256 derived directional keys
/// and ChaCha20-Poly1305 authenticated encryption.
/// </summary>
/// <remarks>
/// Protected packets carry an 8-byte counter and 16-byte authentication tag around
/// the encrypted DHMP V1 payload. This is an explicit security envelope, not part of
/// the base headerless V1 data contract.
/// </remarks>
public sealed class DhmpPskChaCha20Poly1305Session :
    IDhmpPacketDecoder,
    IDisposable
{
    public const int CounterSize = 8;
    public const int TagSize = 16;
    public const int NonceSize = 12;
    public const int KeySize = 32;
    public const int Overhead = CounterSize + TagSize;

    private readonly ChaCha20Poly1305 _sendCipher;
    private readonly ChaCha20Poly1305 _receiveCipher;
    private readonly uint _sendNoncePrefix;
    private readonly uint _receiveNoncePrefix;
    private readonly byte[] _sessionIdBytes;
    private readonly DhmpReplayWindow _replayWindow = new();

    private ulong _sendCounter;
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

        ReadOnlySpan<byte> sendMaterial =
            role == DhmpSecurityRole.Initiator
                ? initiatorToResponder
                : responderToInitiator;

        ReadOnlySpan<byte> receiveMaterial =
            role == DhmpSecurityRole.Initiator
                ? responderToInitiator
                : initiatorToResponder;

        byte[] sendKey =
            sendMaterial[..KeySize].ToArray();
        byte[] receiveKey =
            receiveMaterial[..KeySize].ToArray();

        _sendNoncePrefix =
            BinaryPrimitives.ReadUInt32BigEndian(
                sendMaterial.Slice(KeySize, sizeof(uint)));

        _receiveNoncePrefix =
            BinaryPrimitives.ReadUInt32BigEndian(
                receiveMaterial.Slice(KeySize, sizeof(uint)));

        _sendCipher = new ChaCha20Poly1305(sendKey);
        _receiveCipher = new ChaCha20Poly1305(receiveKey);

        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(receiveKey);
        CryptographicOperations.ZeroMemory(initiatorToResponder);
        CryptographicOperations.ZeroMemory(responderToInitiator);
    }

    int IDhmpPacketDecoder.OverheadBytes => Overhead;

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

        if (plaintextDestination.Length < ciphertextBytes)
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
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonce);
            return false;
        }

        CryptographicOperations.ZeroMemory(nonce);

        if (!_replayWindow.TryAccept(counter))
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }

        plaintextBytes = ciphertextBytes;
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _sendCipher.Dispose();
        _receiveCipher.Dispose();
        CryptographicOperations.ZeroMemory(_sessionIdBytes);
    }
}
