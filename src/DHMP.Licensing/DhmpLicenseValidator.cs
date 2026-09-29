using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DHMP.Licensing;

public sealed class DhmpLicenseValidator
{
    private const string Prefix = "DHMP1.";
    private const byte PayloadVersion = 1;
    private const int PayloadLength = 41; // version + ApplicationId + KeyId + issuedAt Unix seconds
    private readonly byte[] _publicKey;

    public DhmpLicenseValidator(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        if (subjectPublicKeyInfo.IsEmpty)
            throw new ArgumentException("A public verification key is required.", nameof(subjectPublicKeyInfo));

        _publicKey = subjectPublicKeyInfo.ToArray();
    }

    public DhmpLicenseValidationResult Validate(string? key, Guid expectedApplicationId)
    {
        if (string.IsNullOrWhiteSpace(key))
            return new(DhmpLicenseValidationStatus.MissingKey);

        if (!key.StartsWith(Prefix, StringComparison.Ordinal))
            return new(DhmpLicenseValidationStatus.UnsupportedVersion);

        var parts = key.Split('.');
        if (parts.Length != 3 || parts[0] != "DHMP1")
            return new(DhmpLicenseValidationStatus.MalformedKey);

        if (!TryDecodeBase64Url(parts[1], out var payload) ||
            !TryDecodeBase64Url(parts[2], out var signature) ||
            payload.Length != PayloadLength ||
            signature.Length == 0)
            return new(DhmpLicenseValidationStatus.MalformedKey);

        if (payload[0] != PayloadVersion)
            return new(DhmpLicenseValidationStatus.UnsupportedVersion);

        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(_publicKey, out var bytesRead);
            if (bytesRead != _publicKey.Length)
                return new(DhmpLicenseValidationStatus.InvalidSignature);

            if (!verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256))
                return new(DhmpLicenseValidationStatus.InvalidSignature);
        }
        catch (CryptographicException)
        {
            return new(DhmpLicenseValidationStatus.InvalidSignature);
        }

        var applicationId = new Guid(payload.AsSpan(1, 16), bigEndian: true);
        var keyId = new Guid(payload.AsSpan(17, 16), bigEndian: true);

        if (applicationId != expectedApplicationId)
            return new(DhmpLicenseValidationStatus.ApplicationMismatch, applicationId, keyId);

        return new(DhmpLicenseValidationStatus.Valid, applicationId, keyId);
    }

    internal static byte[] CreatePayload(Guid applicationId, Guid keyId, long issuedAtUnixSeconds)
    {
        var payload = new byte[PayloadLength];
        payload[0] = PayloadVersion;
        applicationId.TryWriteBytes(payload.AsSpan(1, 16), bigEndian: true, out _);
        keyId.TryWriteBytes(payload.AsSpan(17, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(33, 8), issuedAtUnixSeconds);
        return payload;
    }

    internal static string EncodeBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value))
            return false;

        var normalized = value.Replace('-', '+').Replace('_', '/');
        var remainder = normalized.Length % 4;
        if (remainder == 1)
            return false;
        if (remainder != 0)
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
