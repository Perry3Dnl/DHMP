using Microsoft.Extensions.DependencyInjection;

namespace DHMP.AspNetCore;

public static class DhmpServiceCollectionExtensions
{
    private sealed class DhmpRegistrationMarker
    {
    }

    public static IServiceCollection AddDHMP(
        this IServiceCollection services,
        Guid applicationId,
        string licenseKey,
        ReadOnlySpan<byte> publicVerificationKey)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(
                descriptor =>
                    descriptor.ServiceType ==
                    typeof(DhmpRegistrationMarker)))
            throw new InvalidOperationException(
                "DHMP is already registered in this service collection.");

        var publicKey =
            publicVerificationKey.ToArray();

        services.AddSingleton<DhmpRegistrationMarker>();

        services
            .AddOptions<DhmpOptions>()
            .Configure(options =>
            {
                options.ApplicationId =
                    applicationId;

                options.LicenseKey =
                    licenseKey;

                options.PublicVerificationKey =
                    publicKey;
            });

        services.AddSingleton<DhmpRuntimeState>();
        services.AddHostedService<DhmpLicenseStartupGate>();
        services.AddHostedService<DhmpRuntimeHostedService>();

        return services;
    }
}
