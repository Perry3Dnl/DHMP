using Microsoft.Extensions.Hosting;

namespace DHMP.AspNetCore;

internal sealed class DhmpRuntimeHostedService : IHostedService
{
    private readonly DhmpRuntimeState _state;
    public DhmpRuntimeHostedService(DhmpRuntimeState state) => _state = state;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_state.LicenseValidated)
            throw new InvalidOperationException("DHMP runtime cannot activate before license validation.");
        _state.RuntimeActivated = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _state.RuntimeActivated = false;
        return Task.CompletedTask;
    }
}
