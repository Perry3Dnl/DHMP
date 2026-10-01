using DHMP.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
if (Environment.GetEnvironmentVariable("DHMP_TEST_CONFIG") is { Length: > 0 } testConfiguration)
    builder.Configuration.AddJsonFile(testConfiguration, optional: false, reloadOnChange: false);
builder.Services.AddHttpClient("backend", client =>
    client.BaseAddress = new Uri(builder.Configuration["Backend:BaseAddress"] ?? "https://api.example/"));
builder.Services.AddDHMP(builder.Configuration["DHMP:LicenseKey"] ?? "");
var app = builder.Build();

// Ordinary application routes and factory calls; no DHMP message codec/controller rewrite.
app.MapGet("/api/hello", () => Results.Json(new { message = "Hello from the existing API", process = Environment.ProcessId }));
app.MapPost("/api/echo", (EchoRequest request) => Results.Json(request));
app.MapGet("/demo", async (IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    using var client = clients.CreateClient("backend");
    using var response = await client.GetAsync("/api/hello", cancellationToken);
    return Results.Content(await response.Content.ReadAsStringAsync(cancellationToken), "application/json", statusCode: (int)response.StatusCode);
});
app.MapGet("/dhmp/status", (DhmpRuntimeState state) => Results.Json(new { state.LicenseValidated, state.ApiReady, state.ApiSessionId }));
if (builder.Configuration.GetValue<bool>("Lab:Enabled"))
{
    int mutations = 0;
    app.MapGet("/api/lab/private", (HttpContext context) => context.Request.Headers.Authorization == "Bearer lab-test"
        ? Results.Json(new { authorized = true }) : Results.Unauthorized());
    app.MapPost("/api/lab/mutate", () => Results.Json(new { mutations = Interlocked.Increment(ref mutations) }));
    app.MapGet("/api/lab/metrics", () => Results.Json(new { mutations = Volatile.Read(ref mutations) }));
    app.Map("/lab/proxy/{**target}", async (HttpContext context, IHttpClientFactory clients) =>
    {
        string target = context.Request.RouteValues["target"]?.ToString() ?? "";
        if (!target.StartsWith("api/", StringComparison.Ordinal)) { context.Response.StatusCode = 400; return; }
        using var client = clients.CreateClient("backend");
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), "/" + target + context.Request.QueryString);
        if (context.Request.ContentLength > 0)
        {
            request.Content = new StreamContent(context.Request.Body);
            request.Content.Headers.ContentLength = context.Request.ContentLength;
            if (context.Request.ContentType is { } contentType) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        if (context.Request.Headers.Authorization.Count > 0) request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToArray());
        try
        {
            using var response = await client.SendAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (InvalidDataException) { context.Response.StatusCode = 413; }
        catch (Exception error) when ((error is TimeoutException or OperationCanceledException) && !context.RequestAborted.IsCancellationRequested)
        { context.Response.StatusCode = 504; }
    });
}
app.Run();

public sealed record EchoRequest(string Message);
