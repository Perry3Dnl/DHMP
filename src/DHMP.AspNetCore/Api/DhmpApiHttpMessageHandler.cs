using System.Net;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace DHMP.AspNetCore;

// HttpClient is the existing application facade only. DHMP bytes use raw IPv6, never HTTP transport.
internal sealed class DhmpApiHttpMessageHandler(IDhmpApiExchange exchange, DhmpApiOptions options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is not { IsAbsoluteUri: true } uri || !options.Matches(uri))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        var envelope = new DhmpApiEnvelope { Method = request.Method.Method, Target = uri.PathAndQuery };
        foreach (var header in request.Headers)
            if (!DhmpApiEnvelope.ExcludedHeader(header.Key)) envelope.Headers.Add(header.Key, header.Value.ToArray());
        if (request.Content is not null)
        {
            if (request.Content.Headers.ContentLength > options.MaximumBodyBytes) throw new InvalidDataException("API request body exceeds configured limit.");
            foreach (var header in request.Content.Headers)
                if (!DhmpApiEnvelope.ExcludedHeader(header.Key)) envelope.Headers[header.Key] = header.Value.ToArray();
            using var buffer = new DhmpApiBuffer(options.MaximumBodyBytes);
            await request.Content.CopyToAsync(buffer, deadline.Token).ConfigureAwait(false);
            envelope.Body = buffer.ToArray();
        }
        byte[] serialized = await DhmpApiEnvelope.SerializeAsync(envelope, options.MaximumMessageBytes, deadline.Token).ConfigureAwait(false);
        // No fallback or retry for the mapped origin, including timeout and authentication failure.
        byte[] bytes = await exchange.RequestAsync(serialized, deadline.Token).ConfigureAwait(false);
        var result = DhmpApiEnvelope.Deserialize(bytes, options.MaximumBodyBytes);
        if (result.Status is < 100 or > 599) throw new InvalidDataException("Invalid API response status.");
        var response = new HttpResponseMessage((HttpStatusCode)result.Status) { RequestMessage = request, Content = new ByteArrayContent(result.Body) };
        foreach (var header in result.Headers)
        {
            if (DhmpApiEnvelope.ExcludedHeader(header.Key)) continue;
            if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value))
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return response;
    }
}

internal sealed class DhmpApiHandlerFilter(IDhmpApiExchange exchange, IOptions<DhmpApiOptions> options) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        builder.PrimaryHandler = new DhmpApiHttpMessageHandler(exchange, options.Value) { InnerHandler = builder.PrimaryHandler };
    };
}
