using DHMP.Licensing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DHMP.AspNetCore;

internal sealed class DhmpLicenseStartupGate : IHostedService
{
    private readonly DhmpOptions _options;

    public DhmpLicenseStartupGate(IOptions<DhmpOptions> options)
    {
        _options = options.Value;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.ApplicationId == Guid.Empty)
            throw new InvalidOperationException("DHMP ApplicationId must be configured.");

        if (_options.PublicVerificationKey.Length == 0)
            throw new InvalidOperationException("DHMP public verification key must be configured.");

        var result = new DhmpLicenseValidator(_options.PublicVerificationKey)
            .Validate(_options.LicenseKey, _options.ApplicationId);

        if (!result.IsValid)
            throw new DhmpLicenseException(result.Status);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
