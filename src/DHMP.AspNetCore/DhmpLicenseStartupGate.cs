using DHMP.Licensing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DHMP.AspNetCore;

internal sealed class DhmpLicenseStartupGate : IHostedService
{
    private readonly DhmpOptions _options;
    private readonly DhmpRuntimeState _state;

    public DhmpLicenseStartupGate(IOptions<DhmpOptions> options, DhmpRuntimeState state)
    {
        _options = options.Value;
        _state = state;
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

        _state.LicenseValidated = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _state.LicenseValidated = false;
        return Task.CompletedTask;
    }
}
