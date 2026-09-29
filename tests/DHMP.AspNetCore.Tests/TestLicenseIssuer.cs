using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DHMP.AspNetCore.Tests;

internal sealed class TestLicenseIssuer : IDisposable
{
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] PublicKey => _signer.ExportSubjectPublicKeyInfo();

    public string Issue(Guid applicationId)
    {
        var payload = new byte[41];
        payload[0] = 1;
        applicationId.TryWriteBytes(payload.AsSpan(1, 16), bigEndian: true, out _);
        Guid.NewGuid().TryWriteBytes(payload.AsSpan(17, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(33, 8), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var signature = _signer.SignData(payload, HashAlgorithmName.SHA256);
        return $"DHMP1.{Encode(payload)}.{Encode(signature)}";
    }

    public void Dispose() => _signer.Dispose();

    private static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
