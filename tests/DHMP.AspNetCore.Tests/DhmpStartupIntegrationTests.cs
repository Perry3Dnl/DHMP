using DHMP.Licensing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpStartupIntegrationTests
{
    [Fact]
    public async Task HappyPath_ValidLicense_AllowsHostStartup()
    {
        using var issuer = new TestLicenseIssuer();
        var applicationId = Guid.NewGuid();
        using var host = BuildHost(applicationId, issuer.Issue(applicationId), issuer.PublicKey);

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task BlockedPath_MissingLicense_BlocksHostStartup()
    {
        using var issuer = new TestLicenseIssuer();
        var applicationId = Guid.NewGuid();
        using var host = BuildHost(applicationId, "", issuer.PublicKey);

        var exception = await Assert.ThrowsAsync<DhmpLicenseException>(() => host.StartAsync());

        Assert.Equal(DhmpLicenseValidationStatus.MissingKey, exception.Status);
    }

    [Fact]
    public async Task BlockedPath_WrongApplication_BlocksHostStartup()
    {
        using var issuer = new TestLicenseIssuer();
        using var host = BuildHost(Guid.NewGuid(), issuer.Issue(Guid.NewGuid()), issuer.PublicKey);

        var exception = await Assert.ThrowsAsync<DhmpLicenseException>(() => host.StartAsync());

        Assert.Equal(DhmpLicenseValidationStatus.ApplicationMismatch, exception.Status);
    }

    [Fact]
    public async Task BlockedPath_TamperedLicense_BlocksHostStartup()
    {
        using var issuer = new TestLicenseIssuer();
        var applicationId = Guid.NewGuid();
        var key = issuer.Issue(applicationId);
        var parts = key.Split('.');
        var signature = Decode(parts[2]);
        signature[^1] ^= 1;
        var tampered = $"DHMP1.{parts[1]}.{Encode(signature)}";
        using var host = BuildHost(applicationId, tampered, issuer.PublicKey);

        var exception = await Assert.ThrowsAsync<DhmpLicenseException>(() => host.StartAsync());

        Assert.Equal(DhmpLicenseValidationStatus.InvalidSignature, exception.Status);
    }

    [Fact]
    public async Task CriticalPath_UntrustedIssuer_BlocksHostStartup()
    {
        using var trustedIssuer = new TestLicenseIssuer();
        using var untrustedIssuer = new TestLicenseIssuer();
        var applicationId = Guid.NewGuid();
        using var host = BuildHost(applicationId, untrustedIssuer.Issue(applicationId), trustedIssuer.PublicKey);

        var exception = await Assert.ThrowsAsync<DhmpLicenseException>(() => host.StartAsync());

        Assert.Equal(DhmpLicenseValidationStatus.InvalidSignature, exception.Status);
    }

    private static IHost BuildHost(Guid applicationId, string key, byte[] publicKey) =>
        Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddDHMP(applicationId, key, publicKey))
            .Build();

    private static byte[] Decode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
