using Dhmp.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Dhmp.AspNetCore;

public static class DhmpServiceCollectionExtensions
{
    /// <summary>
    /// Adds the DHMP server with safe defaults. Intended developer setup:
    /// builder.Services.AddDhmp();
    /// </summary>
    public static IServiceCollection AddDhmp(
        this IServiceCollection services,
        Action<DhmpServerOptions>? configure = null)
    {
        var options = new DhmpServerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        // Transport host is intentionally registered behind the server package boundary.
        // It will become an IHostedService once the retained Adaptive receive path is ported.
        return services;
    }
}
