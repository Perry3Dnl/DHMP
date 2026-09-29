using System.Security.Cryptography;
using Xunit;

namespace DHMP.Licensing.Tests;

public sealed class DhmpLicenseHardeningTests
{
    [Fact]
    public void HappyFlow_ValidatorCopiesCallerOwnedPublicKey()
    {
        using var issuer =
            new TestLicenseIssuer();

        Guid applicationId =
            Guid.NewGuid();

        byte[] publicKey =
            issuer.PublicKey;

        var validator =
            new DhmpLicenseValidator(
                publicKey);

        publicKey.AsSpan().Fill(0);

        var result =
            validator.Validate(
                issuer.Issue(applicationId),
                applicationId);

        Assert.True(result.IsValid);
        Assert.Equal(
            applicationId,
            result.ApplicationId);

        Assert.NotEqual(
            Guid.Empty,
            result.KeyId);
    }

    [Fact]
    public void CriticalFlow_EmptyVerificationKeyIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() =>
            new DhmpLicenseValidator(
                ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData("DHMP1.")]
    [InlineData("DHMP1.a.b.extra")]
    [InlineData("DHMP1.a.b.c.d")]
    [InlineData("DHMP1.A.A")]
    public void CriticalFlow_StructurallyInvalidKeysFailClosed(
        string key)
    {
        using var issuer =
            new TestLicenseIssuer();

        var result =
            new DhmpLicenseValidator(
                issuer.PublicKey)
            .Validate(
                key,
                Guid.NewGuid());

        Assert.False(result.IsValid);
        Assert.NotEqual(
            DhmpLicenseValidationStatus.Valid,
            result.Status);
    }

    [Fact]
    public void CriticalFlow_PublicKeyWithTrailingGarbageFailsClosed()
    {
        using var issuer =
            new TestLicenseIssuer();

        Guid applicationId =
            Guid.NewGuid();

        byte[] trusted =
            issuer.PublicKey;

        byte[] malformed =
            new byte[trusted.Length + 4];

        trusted.CopyTo(
            malformed,
            0);

        RandomNumberGenerator.Fill(
            malformed.AsSpan(
                trusted.Length));

        var result =
            new DhmpLicenseValidator(
                malformed)
            .Validate(
                issuer.Issue(applicationId),
                applicationId);

        Assert.False(result.IsValid);
        Assert.Equal(
            DhmpLicenseValidationStatus.InvalidSignature,
            result.Status);
    }

    [Fact]
    public void CriticalFlow_ApplicationMismatchReturnsSignedIdentityWithoutValidatingWrongApp()
    {
        using var issuer =
            new TestLicenseIssuer();

        Guid signedApplication =
            Guid.NewGuid();

        Guid expectedApplication =
            Guid.NewGuid();

        var result =
            new DhmpLicenseValidator(
                issuer.PublicKey)
            .Validate(
                issuer.Issue(
                    signedApplication),
                expectedApplication);

        Assert.False(result.IsValid);
        Assert.Equal(
            DhmpLicenseValidationStatus.ApplicationMismatch,
            result.Status);

        Assert.Equal(
            signedApplication,
            result.ApplicationId);

        Assert.NotEqual(
            Guid.Empty,
            result.KeyId);
    }

    [Fact]
    public void CriticalFlow_MalformedPayloadLengthIsRejectedBeforeSignatureUse()
    {
        using var issuer =
            new TestLicenseIssuer();

        string key =
            issuer.Issue(
                Guid.NewGuid());

        string[] parts =
            key.Split('.');

        byte[] shortPayload =
            new byte[40];

        string malformed =
            $"DHMP1.{Encode(shortPayload)}.{parts[2]}";

        var result =
            new DhmpLicenseValidator(
                issuer.PublicKey)
            .Validate(
                malformed,
                Guid.NewGuid());

        Assert.Equal(
            DhmpLicenseValidationStatus.MalformedKey,
            result.Status);
    }

    private static string Encode(
        ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(
                bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
