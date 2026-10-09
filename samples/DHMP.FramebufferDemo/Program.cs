var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DhmpThroughputLab>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<DhmpThroughputLab>());

builder.Services.AddSingleton<DhmpAfXdpLiveLab>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<DhmpAfXdpLiveLab>());

builder.Services.AddSingleton<DhmpUnitySessionLab>();
builder.Services.AddSingleton<DhmpFullReportLab>();

var app = builder.Build();

// Keep the Unity browser session host warm and available immediately after deployment.
_ = app.Services.GetRequiredService<DhmpUnitySessionLab>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    demo = "DHMP Throughput Lab",
    canonicalDataShape = "one negotiated record per packet",
    minimumRecordSize = DhmpThroughputLab.RecordSize,
    maximumRecordSize = DhmpThroughputLab.MaximumPayloadBytes,
    maximumPayloadBytes = DhmpThroughputLab.MaximumPayloadBytes
}));

app.MapGet("/version", () => Results.Json(new
{
    commit = Environment.GetEnvironmentVariable("DHMP_DEMO_COMMIT") ?? "dev"
}));

app.MapGet(
    "/unity",
    () => Results.Redirect("/unity-test.html"));

app.MapGet(
    "/report",
    () => Results.Redirect("/full-report.html"));

app.MapPost(
    "/api/report/http-sink",
    async (
        HttpRequest request,
        CancellationToken cancellationToken) =>
    {
        await request.Body.CopyToAsync(
            Stream.Null,
            cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    });

app.MapGet(
    "/api/report/status",
    (DhmpFullReportLab lab) =>
        Results.Json(lab.Status()));

app.MapPost(
    "/api/report/run",
    (DhmpFullReportLab lab) =>
        lab.Start()
            ? Results.Accepted(
                "/api/report/status",
                lab.Status())
            : Results.Conflict(
                new
                {
                    error = "A full benchmark report is already running."
                }));

app.MapGet(
    "/api/unity/health",
    (DhmpUnitySessionLab lab) =>
    {
        UnityServerStats stats = lab.Stats();

        return Results.Json(new
        {
            status = "online",
            connected = stats.ConnectedConnections,
            active = stats.ActiveConnections,
            tickRate = stats.SimulatedTickRate,
            recordSize = stats.RecordSize
        });
    });

app.MapGet(
    "/api/stress/stats",
    (DhmpThroughputLab lab) =>
        Results.Json(lab.Snapshot()));

app.MapGet(
    "/api/afxdp/stats",
    (DhmpAfXdpLiveLab lab) =>
        Results.Json(lab.Snapshot()));

app.MapPost(
    "/api/unity/connect",
    (UnityConnectRequest request, DhmpUnitySessionLab lab) =>
        Results.Json(lab.Connect(request.Name)));

app.MapPost(
    "/api/unity/disconnect/{playerId:long}",
    async (long playerId, DhmpUnitySessionLab lab) =>
        await lab.DisconnectAsync(playerId).ConfigureAwait(false)
            ? Results.NoContent()
            : Results.NotFound(new { error = "Unknown player." }));

app.MapPost(
    "/api/unity/send/{playerId:long}",
    async (
        long playerId,
        UnityPlayerStateInput request,
        DhmpUnitySessionLab lab,
        CancellationToken cancellationToken) =>
    {
        PlayerSnapshot? snapshot = await lab.SendAsync(
            playerId,
            request,
            cancellationToken).ConfigureAwait(false);

        return snapshot is null
            ? Results.NotFound(new { error = "Unknown player." })
            : Results.Json(snapshot);
    });

app.MapGet(
    "/api/unity/receive/{playerId:long}",
    (long playerId, DhmpUnitySessionLab lab) =>
    {
        UnityReceiveResult? result = lab.Receive(playerId);
        return result is null
            ? Results.NotFound(new { error = "Unknown player." })
            : Results.Json(result);
    });

app.MapGet(
    "/api/unity/stats",
    (DhmpUnitySessionLab lab) =>
        Results.Json(lab.Stats()));

app.MapGet(
    "/api/unity/players",
    (DhmpUnitySessionLab lab) =>
        Results.Json(lab.Players()));

app.MapPost(
    "/api/afxdp/configure",
    (
        DhmpAfXdpConfigureRequest request,
        DhmpAfXdpLiveLab lab,
        DhmpThroughputLab throughputLab) =>
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

        throughputLab.Pause();

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
        DhmpThroughputLab lab,
        DhmpAfXdpLiveLab afXdpLab) =>
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
                not DHMP.Protocol.DhmpProcessingMode.UnsafeSequential and
                not DHMP.Protocol.DhmpProcessingMode.Latest and
                not DHMP.Protocol.DhmpProcessingMode.UnsafeLatest)
        {
            return Results.BadRequest(
                new { error = "Unsupported receive mode." });
        }

        if (!Enum.TryParse<DHMP.Protocol.DhmpRatePolicy>(
                request.RatePolicy,
                ignoreCase: true,
                out var ratePolicy) ||
            ratePolicy is not DHMP.Protocol.DhmpRatePolicy.RejectWindow and
                not DHMP.Protocol.DhmpRatePolicy.SmoothPacing and
                not DHMP.Protocol.DhmpRatePolicy.Unlimited)
        {
            return Results.BadRequest(
                new { error = "Unsupported rate policy." });
        }

        if (!Enum.TryParse<DhmpStressConfirmationMode>(
                request.ConfirmationMode,
                ignoreCase: true,
                out var confirmationMode) ||
            !Enum.IsDefined(confirmationMode))
        {
            return Results.BadRequest(
                new { error = "Unsupported confirmation mode." });
        }

        afXdpLab.Pause();

        if (!IsSupportedPacketRateCap(
                request.PacketRateCap))
        {
            return Results.BadRequest(
                new { error = "Unsupported packet-rate cap." });
        }

        lab.Configure(
            request.PacketBytes,
            request.Workers,
            receiveMode,
            ratePolicy,
            request.NativeSmoothing,
            confirmationMode,
            request.PacketRateCap);

        return Results.Json(lab.Snapshot());
    });

app.Run();

static bool IsSupportedPacketSize(int packetBytes) =>
    packetBytes is
        16 or
        32 or
        64 or
        128 or
        256 or
        512 or
        1024 or
        1200 or
        1408 or
        4096 or
        16384 or
        32768 or
        65520;


static bool IsSupportedPacketRateCap(long packetRateCap) =>
    packetRateCap is
        0 or
        1_000_000 or
        2_000_000 or
        5_000_000 or
        10_000_000 or
        20_000_000 or
        30_000_000 or
        40_000_000 or
        50_000_000 or
        75_000_000 or
        100_000_000;

static bool IsSupportedAfXdpPayloadSize(int payloadBytes) =>
    payloadBytes is
        16 or
        256 or
        1024 or
        1200 or
        1408;
