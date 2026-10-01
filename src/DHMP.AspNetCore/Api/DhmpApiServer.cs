using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace DHMP.AspNetCore;

// Decorates the host server to reuse the exact configured middleware/controller/minimal-API pipeline.
internal sealed class DhmpApiServer(IServer inner, DhmpApiPipeline pipeline) : IServer
{
    public IFeatureCollection Features => inner.Features;
    public async Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
    {
        await inner.StartAsync(application, cancellationToken).ConfigureAwait(false);
        pipeline.Activate(async features =>
        {
            TContext context = application.CreateContext(features);
            Exception? error = null;
            try { await application.ProcessRequestAsync(context).ConfigureAwait(false); }
            catch (Exception exception) { error = exception; throw; }
            finally
            {
                try
                {
                    if (features.Get<IHttpResponseFeature>() is DhmpApiResponseFeature response)
                    {
                        await response.StartAsync().ConfigureAwait(false);
                        await features.Get<IHttpResponseBodyFeature>()!.CompleteAsync().ConfigureAwait(false);
                        await response.CompleteAsync().ConfigureAwait(false);
                    }
                }
                finally { application.DisposeContext(context, error); }
            }
        });
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        pipeline.Deactivate();
        await inner.StopAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Dispose() { pipeline.Deactivate(); inner.Dispose(); }
}

internal sealed class DhmpApiPipeline(IOptions<DhmpApiOptions> options)
{
    private readonly TaskCompletionSource<Func<IFeatureCollection, Task>> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopped;
    internal void Activate(Func<IFeatureCollection, Task> application) => _ready.TrySetResult(application);
    internal void Deactivate() => _stopped = true;

    internal async Task<byte[]> DispatchAsync(byte[] bytes, CancellationToken token)
    {
        var settings = options.Value;
        var request = DhmpApiEnvelope.Deserialize(bytes, settings.MaximumBodyBytes);
        if (request.Method is null || request.Target is null || request.Method.Length == 0 || request.Method.Length > 32 || request.Method.Any(c => !char.IsAsciiLetter(c)) ||
            !request.Target.StartsWith('/') || request.Target.StartsWith("//", StringComparison.Ordinal) || request.Target.Contains('#') ||
            request.Target.Any(c => char.IsControl(c) || c == '\\'))
            throw new InvalidDataException("Invalid API request target or method.");
        if (_stopped) throw new ObjectDisposedException(nameof(DhmpApiPipeline));
        var application = await _ready.Task.WaitAsync(token).ConfigureAwait(false);
        using var body = new MemoryStream(request.Body, writable: false);
        using var responseBody = new DhmpApiBuffer(settings.MaximumBodyBytes);
        var context = new DefaultHttpContext();
        var responseFeature = new DhmpApiResponseFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));
        context.Request.Method = request.Method;
        context.Request.Scheme = settings.Origin.Scheme;
        context.Request.Host = HostString.FromUriComponent(settings.Origin);
        int query = request.Target.IndexOf('?');
        context.Request.Path = PathString.FromUriComponent(query < 0 ? request.Target : request.Target[..query]);
        context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(request.Target[query..]);
        context.Request.Protocol = "DHMP-API/1";
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = request.Target;
        context.Request.Body = body;
        context.Request.ContentLength = request.Body.Length;
        context.RequestAborted = token;
        context.Connection.RemoteIpAddress = IPAddress.Parse(settings.RemoteAddress);
        context.Connection.LocalIpAddress = IPAddress.Parse(settings.LocalAddress);
        foreach (var header in request.Headers)
            if (!DhmpApiEnvelope.ExcludedHeader(header.Key)) context.Request.Headers[header.Key] = header.Value;
        DhmpApiEnvelope response;
        try
        {
            await application(context.Features).ConfigureAwait(false);
            await responseFeature.StartAsync().ConfigureAwait(false);
            await context.Features.Get<IHttpResponseBodyFeature>()!.CompleteAsync().ConfigureAwait(false);
            response = new DhmpApiEnvelope { Status = context.Response.StatusCode, Body = responseBody.ToArray() };
            foreach (var header in context.Response.Headers)
                if (!DhmpApiEnvelope.ExcludedHeader(header.Key)) response.Headers[header.Key] = header.Value.Select(v => v ?? "").ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Never leak controller exception details or a partial response to the caller.
            response = new DhmpApiEnvelope { Status = 500 };
        }
        finally { await responseFeature.CompleteAsync().ConfigureAwait(false); }
        return await DhmpApiEnvelope.SerializeAsync(response, settings.MaximumMessageBytes, token).ConfigureAwait(false);
    }
}
