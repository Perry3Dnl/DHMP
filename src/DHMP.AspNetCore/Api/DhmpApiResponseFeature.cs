using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace DHMP.AspNetCore;

internal sealed class DhmpApiResponseFeature : HttpResponseFeature
{
    private readonly Stack<(Func<object, Task> Callback, object State)> _starting = new();
    private readonly Stack<(Func<object, Task> Callback, object State)> _completed = new();
    private bool _started;
    public override bool HasStarted => _started;
    public override void OnStarting(Func<object, Task> callback, object state)
    {
        if (_started) throw new InvalidOperationException("Response already started.");
        _starting.Push((callback, state));
    }
    public override void OnCompleted(Func<object, Task> callback, object state) => _completed.Push((callback, state));
    internal async Task StartAsync()
    {
        if (_started) return;
        while (_starting.TryPop(out var entry)) await entry.Callback(entry.State).ConfigureAwait(false);
        _started = true;
    }
    internal async Task CompleteAsync()
    {
        await StartAsync().ConfigureAwait(false);
        while (_completed.TryPop(out var entry)) await entry.Callback(entry.State).ConfigureAwait(false);
    }
}
