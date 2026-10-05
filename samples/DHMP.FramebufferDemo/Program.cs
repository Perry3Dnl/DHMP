var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DhmpThroughputLab>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<DhmpThroughputLab>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    demo = "DHMP Throughput Lab",
    recordSize = DhmpThroughputLab.RecordSize,
    maximumPayloadBytes = DhmpThroughputLab.MaximumPayloadBytes
}));

app.MapGet("/version", () => Results.Json(new
{
    commit = Environment.GetEnvironmentVariable("DHMP_DEMO_COMMIT") ?? "dev"
}));

app.MapGet(
    "/api/stress/stats",
    (DhmpThroughputLab lab) =>
        Results.Json(lab.Snapshot()));

app.MapPost(
    "/api/stress/configure",
    (
        DhmpThroughputRequest request,
        DhmpThroughputLab lab) =>
    {
        if (!IsSupportedPacketSize(request.PacketBytes))
        {
            return Results.BadRequest(
                new { error = "Unsupported packet size." });
        }

        if (request.Workers <= 0 ||
            request.Workers > Math.Min(64, Environment.ProcessorCount * 2))
        {
            return Results.BadRequest(
                new { error = "Unsupported worker count." });
        }

        lab.Configure(
            request.PacketBytes,
            request.Workers);

        return Results.Json(lab.Snapshot());
    });

app.Run();

static bool IsSupportedPacketSize(int packetBytes) =>
    packetBytes is
        16 or
        256 or
        1024 or
        4096 or
        16384 or
        32768 or
        65520;
