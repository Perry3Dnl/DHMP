using Microsoft.Extensions.DependencyInjection;

namespace DHMP.AspNetCore;

public static class DhmpServiceCollectionExtensions
{
    public static IServiceCollection AddDHMP(
        this IServiceCollection services,
        Guid applicationId,
        string licenseKey,
        ReadOnlySpan<byte> publicVerificationKey)
    {
        ArgumentNullException.ThrowIfNull(services);

        var publicKey = publicVerificationKey.ToArray();

        services.AddOptions<DhmpOptions>().Configure(options =>
        {
            options.ApplicationId = applicationId;
            options.LicenseKey = licenseKey;
            options.PublicVerificationKey = publicKey;
        });

        services.AddSingleton<DhmpRuntimeState>();
        services.AddSingleton<DHMP.Client.DhmpClient>();
        services.AddSingleton<DHMP.Server.DhmpServer>();
        services.AddHostedService<DhmpLicenseStartupGate>();
        services.AddHostedService<DhmpRuntimeHostedService>();
        return services;
    }
}
