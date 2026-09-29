using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DHMP.AspNetCore.Tests;

public sealed class DhmpStartupHardeningTests
{
    [Fact]
    public async Task HappyFlow_StopResetsLicenseAndRuntimeState()
    {
        using var issuer =
            new TestLicenseIssuer();

        Guid applicationId =
            Guid.NewGuid();

        using IHost host =
            BuildHost(
                applicationId,
                issuer.Issue(applicationId),
                issuer.PublicKey);

        await host.StartAsync(
            TestContext.Current.CancellationToken);

        var state =
            host.Services
                .GetRequiredService<
                    DhmpRuntimeState>();

        Assert.True(state.LicenseValidated);
        Assert.True(state.RuntimeActivated);

        await host.StopAsync(
            TestContext.Current.CancellationToken);

        Assert.False(state.RuntimeActivated);
        Assert.False(state.LicenseValidated);
    }

    [Fact]
    public async Task HappyFlow_AddDhmpCopiesCallerOwnedVerificationKey()
    {
        using var issuer =
            new TestLicenseIssuer();

        Guid applicationId =
            Guid.NewGuid();

        byte[] publicKey =
            issuer.PublicKey;

        string license =
            issuer.Issue(
                applicationId);

        using IHost host =
            BuildHost(
                applicationId,
                license,
                publicKey);

        publicKey.AsSpan().Fill(0);

        await host.StartAsync(
            TestContext.Current.CancellationToken);

        Assert.True(
            host.Services
                .GetRequiredService<
                    DhmpRuntimeState>()
                .RuntimeActivated);

        await host.StopAsync(
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CriticalFlow_EmptyApplicationIdBlocksBeforeRuntimeActivation()
    {
        using var issuer =
            new TestLicenseIssuer();

        using IHost host =
            BuildHost(
                Guid.Empty,
                issuer.Issue(Guid.Empty),
                issuer.PublicKey);

        var error =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => host.StartAsync(
                    TestContext.Current.CancellationToken));

        Assert.Contains(
            "ApplicationId",
            error.Message,
            StringComparison.Ordinal);

        var state =
            host.Services
                .GetRequiredService<
                    DhmpRuntimeState>();

        Assert.False(state.LicenseValidated);
        Assert.False(state.RuntimeActivated);
    }

    [Fact]
    public async Task CriticalFlow_EmptyVerificationKeyBlocksBeforeRuntimeActivation()
    {
        using var issuer =
            new TestLicenseIssuer();

        Guid applicationId =
            Guid.NewGuid();

        using IHost host =
            BuildHost(
                applicationId,
                issuer.Issue(applicationId),
                Array.Empty<byte>());

        var error =
            await Assert.ThrowsAsync<
                InvalidOperationException>(
                () => host.StartAsync(
                    TestContext.Current.CancellationToken));

        Assert.Contains(
            "verification key",
            error.Message,
            StringComparison.OrdinalIgnoreCase);

        var state =
            host.Services
                .GetRequiredService<
                    DhmpRuntimeState>();

        Assert.False(state.LicenseValidated);
        Assert.False(state.RuntimeActivated);
    }

    [Fact]
    public void CriticalFlow_DuplicateAddDhmpRegistrationIsRejected()
    {
        using var issuer =
            new TestLicenseIssuer();

        var services =
            new ServiceCollection();

        Guid firstApplication =
            Guid.NewGuid();

        Guid secondApplication =
            Guid.NewGuid();

        services.AddDHMP(
            firstApplication,
            issuer.Issue(firstApplication),
            issuer.PublicKey);

        var error =
            Assert.Throws<InvalidOperationException>(() =>
                services.AddDHMP(
                    secondApplication,
                    issuer.Issue(secondApplication),
                    issuer.PublicKey));

        Assert.Contains(
            "already registered",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CriticalFlow_AddDhmpRejectsNullServiceCollection()
    {
        IServiceCollection? services =
            null;

        Assert.Throws<ArgumentNullException>(() =>
            DhmpServiceCollectionExtensions
                .AddDHMP(
                    services!,
                    Guid.NewGuid(),
                    "key",
                    new byte[] { 1 }));
    }

    private static IHost BuildHost(
        Guid applicationId,
        string license,
        byte[] publicKey)
        => Host.CreateDefaultBuilder()
            .ConfigureServices(
                services =>
                    services.AddDHMP(
                        applicationId,
                        license,
                        publicKey))
            .Build();
}
