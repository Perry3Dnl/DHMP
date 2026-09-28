using Dhmp.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Dhmp.AspNetCore;

public static class DHMPServiceCollectionExtensions
{
    /// <summary>Adds the DHMP server with safe defaults.</summary>
    public static IServiceCollection AddDHMP(this IServiceCollection services, Action<DHMPServerOptions>? configure = null)
    {
        var options = new DHMPServerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        return services;
    }
}
