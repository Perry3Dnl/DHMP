using DHMP.Protocol;
using DHMP.Server;

const int MaxWidth = 61440;
const int MaxHeight = 34560;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(
    _ =>
    {
        var wire = new DhmpWireContract(DhmpDemoLoadEngine.RecordSize);
        var receivePolicy = new DhmpReceivePolicy(
            DhmpProcessingMode.Latest,
            maximumPayloadBytes: DhmpDemoLoadEngine.RecordSize);

        return new DhmpServer(wire, receivePolicy);
    });

builder.Services.AddSingleton<DhmpDemoLoadEngine>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<DhmpDemoLoadEngine>());

builder.Services.AddSingleton<DhmpScadaDemoEngine>();
builder.Services.AddHostedService(
    services => services.GetRequiredService<DhmpScadaDemoEngine>());

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    maxWidth = MaxWidth,
    maxHeight = MaxHeight,
    mode = "Latest",
    recordSize = DhmpDemoLoadEngine.RecordSize
}));

app.MapGet("/version", () => Results.Json(new
{
    commit = Environment.GetEnvironmentVariable("DHMP_DEMO_COMMIT") ?? "dev"
}));

app.MapGet(
    "/api/stats",
    (DhmpDemoLoadEngine engine) =>
    {
        DhmpDemoSnapshot snapshot = engine.Snapshot();

        return Results.Json(new
        {
            uptimeMilliseconds = snapshot.UptimeMilliseconds,
            receivedRecords = snapshot.ReceivedRecords,
            publishedRecords = snapshot.PublishedRecords,
            receivedRecordBytes = snapshot.ReceivedRecordBytes,
            publishedRecordBytes = snapshot.PublishedRecordBytes,
            width = snapshot.Width,
            height = snapshot.Height,
            label = snapshot.Label,
            targetRecordsPerSecond = snapshot.TargetRecordsPerSecond,
            dirtyFractionPerFrame = snapshot.DirtyFractionPerFrame,
            simulatedFramesPerSecond = snapshot.SimulatedFramesPerSecond,
            recordSize = DhmpDemoLoadEngine.RecordSize,
            mode = "Latest"
        });
    });

app.MapPost(
    "/api/workload",
    (
        DhmpDemoWorkloadRequest request,
        DhmpDemoLoadEngine engine) =>
    {
        if (!IsSupportedResolution(
                request.Width,
                request.Height,
                request.Label))
        {
            return Results.BadRequest(
                new { error = "Unsupported demo resolution." });
        }

        engine.Configure(
            request.Width,
            request.Height,
            request.Label);

        return Results.Json(engine.Snapshot());
    });


app.MapGet(
    "/api/scada/stats",
    (DhmpScadaDemoEngine engine) =>
    {
        DhmpScadaSnapshot snapshot = engine.Snapshot();

        return Results.Json(new
        {
            uptimeMilliseconds = snapshot.UptimeMilliseconds,
            receivedRecords = snapshot.ReceivedRecords,
            publishedRecords = snapshot.PublishedRecords,
            receivedRecordBytes = snapshot.ReceivedRecordBytes,
            publishedRecordBytes = snapshot.PublishedRecordBytes,
            activeTags = snapshot.ActiveTags,
            profile = snapshot.Profile,
            targetRecordsPerSecond = snapshot.TargetRecordsPerSecond,
            frequencyHz = snapshot.FrequencyHz,
            northBusKv = snapshot.NorthBusKv,
            southBusKv = snapshot.SouthBusKv,
            gridLoadMw = snapshot.GridLoadMw,
            transformerTempC = snapshot.TransformerTempC,
            breakerClosed = snapshot.BreakerClosed,
            alarmCount = snapshot.AlarmCount,
            recordSize = DhmpScadaDemoEngine.RecordSize,
            mode = "Latest"
        });
    });

app.MapPost(
    "/api/scada/workload",
    (
        DhmpScadaWorkloadRequest request,
        DhmpScadaDemoEngine engine) =>
    {
        if (!IsSupportedScadaWorkload(
                request.ActiveTags,
                request.Profile))
        {
            return Results.BadRequest(
                new { error = "Unsupported SCADA workload." });
        }

        engine.Configure(
            request.ActiveTags,
            request.Profile);

        return Results.Json(engine.Snapshot());
    });

app.Run();

static bool IsSupportedResolution(
    int width,
    int height,
    string label)
{
    return (width, height, label) switch
    {
        (284, 160, "160p") => true,
        (426, 240, "240p") => true,
        (640, 360, "360p") => true,
        (854, 480, "480p") => true,
        (1280, 720, "720p") => true,
        (1920, 1080, "1080p") => true,
        (2560, 1440, "1440p") => true,
        (3840, 2160, "4K") => true,
        (7680, 4320, "8K") => true,
        (15360, 8640, "16K") => true,
        (30720, 17280, "32K") => true,
        (61440, 34560, "64K") => true,
        _ => false
    };
}

static bool IsSupportedScadaWorkload(
    int activeTags,
    string profile)
{
    return (activeTags, profile) switch
    {
        (1_000, "single-site") => true,
        (10_000, "industrial-site") => true,
        (100_000, "regional-grid") => true,
        (1_000_000, "large-grid") => true,
        _ => false
    };
}
