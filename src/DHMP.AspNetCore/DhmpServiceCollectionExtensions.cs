using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DHMP.AspNetCore;

public static class DhmpServiceCollectionExtensions
{
    private sealed class DhmpRegistrationMarker
    {
    }

    /// <summary>Enable the authenticated API integration using the DHMP configuration section.</summary>
    public static IServiceCollection AddDHMP(this IServiceCollection services, string licenseKey)
    {
        AddDHMP(services, Guid.Empty, licenseKey, ReadOnlySpan<byte>.Empty);
        services.AddOptions<DhmpOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.ApplicationId = configuration.GetValue<Guid>("DHMP:ApplicationId");
            options.PublicVerificationKey = Convert.FromBase64String(configuration["DHMP:PublicVerificationKeyBase64"] ?? "");
        });
        services.AddOptions<DhmpApiOptions>().Configure<IConfiguration>((options, configuration) =>
            configuration.GetSection("DHMP:Api").Bind(options));
        services.AddSingleton<DhmpApiPipeline>();
        services.TryAddSingleton<IDhmpApiTransportFactory, DhmpApiTransportFactory>();
        services.AddSingleton<DhmpApiRuntime>();
        services.AddSingleton<IDhmpApiExchange>(provider => provider.GetRequiredService<DhmpApiRuntime>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<DhmpApiRuntime>());
        services.AddHttpClient();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, DhmpApiHandlerFilter>();
        var server = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IServer));
        if (server is not null)
        {
            services.Remove(server);
            services.AddSingleton<IServer>(provider =>
            {
                var original = (IServer)(server.ImplementationInstance ?? server.ImplementationFactory?.Invoke(provider) ??
                    ActivatorUtilities.CreateInstance(provider, server.ImplementationType!));
                return new DhmpApiServer(original, provider.GetRequiredService<DhmpApiPipeline>());
            });
        }
        // Client-only generic hosts need no server pipeline; AcceptRequests is checked at runtime.
        else services.PostConfigure<DhmpApiOptions>(options =>
        {
            if (options.AcceptRequests) throw new InvalidOperationException("AcceptRequests requires an ASP.NET server registered before AddDHMP.");
        });
        return services;
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

        services.AddSingleton(
            new DhmpRegistrationMarker());

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

