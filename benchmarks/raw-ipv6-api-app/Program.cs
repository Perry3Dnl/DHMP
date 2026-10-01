using DHMP.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
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
app.MapGet("/dhmp/status", (DhmpRuntimeState state) => Results.Json(new { state.LicenseValidated, state.ApiReady }));
app.Run();

public sealed record EchoRequest(string Message);
