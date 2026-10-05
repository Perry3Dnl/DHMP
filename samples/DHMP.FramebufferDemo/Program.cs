var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DhmpThroughputLab>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<DhmpThroughputLab>());

builder.Services.AddSingleton<DhmpAfXdpLiveLab>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<DhmpAfXdpLiveLab>());

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

app.MapGet(
    "/api/afxdp/stats",
    (DhmpAfXdpLiveLab lab) =>
        Results.Json(lab.Snapshot()));

app.MapPost(
    "/api/afxdp/configure",
    (
        DhmpAfXdpConfigureRequest request,
        DhmpAfXdpLiveLab lab) =>
    {
        if (!IsSupportedAfXdpPayloadSize(
                request.PayloadBytes))
        {
            return Results.BadRequest(
                new { error = "Unsupported AF_XDP payload size." });
        }

        if (request.Workers <= 0 ||
            request.Workers > lab.MaxWorkers)
        {
            return Results.BadRequest(
                new { error = "Unsupported AF_XDP worker count." });
        }

        lab.Configure(
            request.PayloadBytes,
            request.Workers);

        return Results.Json(
            lab.Snapshot());
    });

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

        if (!Enum.TryParse<DHMP.Protocol.DhmpProcessingMode>(
                request.ReceiveMode,
                ignoreCase: true,
                out var receiveMode) ||
            receiveMode is not DHMP.Protocol.DhmpProcessingMode.Sequential and
                not DHMP.Protocol.DhmpProcessingMode.Latest)
        {
            return Results.BadRequest(
                new { error = "Unsupported receive mode." });
        }

        if (!Enum.TryParse<DHMP.Protocol.DhmpRatePolicy>(
                request.RatePolicy,
                ignoreCase: true,
                out var ratePolicy) ||
            ratePolicy is not DHMP.Protocol.DhmpRatePolicy.RejectWindow and
                not DHMP.Protocol.DhmpRatePolicy.SmoothPacing)
        {
            return Results.BadRequest(
                new { error = "Unsupported rate policy." });
        }

        lab.Configure(
            request.PacketBytes,
            request.Workers,
            receiveMode,
            ratePolicy);

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


static bool IsSupportedAfXdpPayloadSize(int payloadBytes) =>
    payloadBytes is
        16 or
        256 or
        1024 or
        1200 or
        1408;
