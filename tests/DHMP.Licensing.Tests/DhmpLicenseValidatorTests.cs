using System.Security.Cryptography;

namespace DHMP.Licensing.Tests;

public sealed class DhmpLicenseValidatorTests
{
    [Fact]
    public void HappyFlow_ValidKeyForApplication_IsAccepted()
    {
        using var issuer = new TestLicenseIssuer();
        var applicationId = Guid.NewGuid();
        var result = new DhmpLicenseValidator(issuer.PublicKey).Validate(issuer.Issue(applicationId), applicationId);

        Assert.True(result.IsValid);
        Assert.Equal(DhmpLicenseValidationStatus.Valid, result.Status);
        Assert.Equal(applicationId, result.ApplicationId);
    }

    [Fact]
    public void HappyFlow_SeparateClientAndServerKeys_AreAcceptedForTheirOwnIds()
    {
        using var issuer = new TestLicenseIssuer();
        var clientId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var validator = new DhmpLicenseValidator(issuer.PublicKey);

        Assert.True(validator.Validate(issuer.Issue(clientId), clientId).IsValid);
        Assert.True(validator.Validate(issuer.Issue(serverId), serverId).IsValid);
    }

    [Theory]
    [InlineData(null, DhmpLicenseValidationStatus.MissingKey)]
    [InlineData("", DhmpLicenseValidationStatus.MissingKey)]
    [InlineData("   ", DhmpLicenseValidationStatus.MissingKey)]
    [InlineData("garbage", DhmpLicenseValidationStatus.UnsupportedVersion)]
    [InlineData("DHMP2.abc.def", DhmpLicenseValidationStatus.UnsupportedVersion)]
    [InlineData("DHMP1.only-two-parts", DhmpLicenseValidationStatus.MalformedKey)]
    [InlineData("DHMP1.***.***", DhmpLicenseValidationStatus.MalformedKey)]
    public void CriticalPath_MalformedOrUnsupportedInput_IsRejected(string? key, DhmpLicenseValidationStatus expected)
    {
        using var issuer = new TestLicenseIssuer();
        var result = new DhmpLicenseValidator(issuer.PublicKey).Validate(key, Guid.NewGuid());
        Assert.Equal(expected, result.Status);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void BlockedPath_KeyForDifferentApplication_IsRejected()
    {
        using var issuer = new TestLicenseIssuer();
        var key = issuer.Issue(Guid.NewGuid());

        var result = new DhmpLicenseValidator(issuer.PublicKey).Validate(key, Guid.NewGuid());

        Assert.Equal(DhmpLicenseValidationStatus.ApplicationMismatch, result.Status);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void BlockedPath_TamperedPayload_IsRejected()
    {
        using var issuer = new TestLicenseIssuer();
        var applicationId = Guid.NewGuid();
        var key = issuer.Issue(applicationId);
        var parts = key.Split('.');
        var payload = Decode(parts[1]);
        payload[10] ^= 0x01;
        var tampered = $"DHMP1.{Encode(payload)}.{parts[2]}";

        var result = new DhmpLicenseValidator(issuer.PublicKey).Validate(tampered, applicationId);

        Assert.Equal(DhmpLicenseValidationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public void BlockedPath_TamperedSignature_IsRejected()
    {
        using var issuer = new TestLicenseIssuer();
        var applicationId = Guid.NewGuid();
        var parts = issuer.Issue(applicationId).Split('.');
        var signature = Decode(parts[2]);
        signature[^1] ^= 0x01;
        var tampered = $"DHMP1.{parts[1]}.{Encode(signature)}";

        var result = new DhmpLicenseValidator(issuer.PublicKey).Validate(tampered, applicationId);

        Assert.Equal(DhmpLicenseValidationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public void BlockedPath_KeySignedByDifferentIssuer_IsRejected()
    {
        using var trustedIssuer = new TestLicenseIssuer();
        using var attackerIssuer = new TestLicenseIssuer();
        var applicationId = Guid.NewGuid();

        var result = new DhmpLicenseValidator(trustedIssuer.PublicKey)
            .Validate(attackerIssuer.Issue(applicationId), applicationId);

        Assert.Equal(DhmpLicenseValidationStatus.InvalidSignature, result.Status);
    }

    [Fact]
    public void CriticalPath_InvalidPublicKey_FailsClosed()
    {
        var validator = new DhmpLicenseValidator(RandomNumberGenerator.GetBytes(32));
        var result = validator.Validate("DHMP1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.AQ", Guid.NewGuid());

        Assert.False(result.IsValid);
        Assert.Contains(result.Status, new[]
        {
            DhmpLicenseValidationStatus.MalformedKey,
            DhmpLicenseValidationStatus.InvalidSignature
        });
    }

    private static byte[] Decode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
