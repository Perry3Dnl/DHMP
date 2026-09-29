using System.Security.Cryptography;

namespace DHMP.Security;

/// <summary>
/// 256-bit pre-shared key scoped by a non-zero application key identifier.
/// The key identifier is public metadata; the key bytes are secret.
/// </summary>
public sealed class DhmpPreSharedKey : IDisposable
{
    public const int KeySizeBytes = 32;

    private byte[]? _key;

    public DhmpPreSharedKey(
        uint keyId,
        ReadOnlySpan<byte> key)
    {
        if (keyId == 0)
            throw new ArgumentOutOfRangeException(nameof(keyId));

        if (key.Length != KeySizeBytes)
            throw new ArgumentException(
                $"DHMP PSK profile requires exactly {KeySizeBytes} key bytes.",
                nameof(key));

        KeyId = keyId;
        _key = key.ToArray();
    }

    public uint KeyId { get; }

    internal ReadOnlySpan<byte> KeySpan
    {
        get
        {
            var key = _key;
            ObjectDisposedException.ThrowIf(key is null, this);
            return key;
        }
    }

    public void Dispose()
    {
        byte[]? key =
            Interlocked.Exchange(
                ref _key,
                null);

        if (key is not null)
            CryptographicOperations.ZeroMemory(key);
    }
}
