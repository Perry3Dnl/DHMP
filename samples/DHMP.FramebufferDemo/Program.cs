using System.Buffers.Binary;
using System.Diagnostics;
using DHMP.Protocol;
using DHMP.Server;

const int InputRecordSize = 7;
const int DhmpRecordSize = 16;
const int MaxUpdatesPerRequest = 1_000_000;
const int MaxWidth = 7680;
const int MaxHeight = 4320;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var wire = new DhmpWireContract(DhmpRecordSize);
var receivePolicy = new DhmpReceivePolicy(
    DhmpProcessingMode.Latest,
    maximumPayloadBytes: DhmpRecordSize);
var server = new DhmpServer(wire, receivePolicy);

long generation = 0;
long receivedRecords = 0;
long publishedRecords = 0;
long receivedRecordBytes = 0;
long publishedRecordBytes = 0;
long updateRequests = 0;
var started = Stopwatch.StartNew();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    maxWidth = MaxWidth,
    maxHeight = MaxHeight,
    mode = "Latest",
    recordSize = DhmpRecordSize
}));

app.MapGet("/version", () => Results.Json(new
{
    commit = Environment.GetEnvironmentVariable("DHMP_DEMO_COMMIT") ?? "dev"
}));

app.MapGet("/api/stats", () => Results.Json(new
{
    uptimeMilliseconds = started.ElapsedMilliseconds,
    receivedRecords = Interlocked.Read(ref receivedRecords),
    publishedRecords = Interlocked.Read(ref publishedRecords),
    receivedRecordBytes = Interlocked.Read(ref receivedRecordBytes),
    publishedRecordBytes = Interlocked.Read(ref publishedRecordBytes),
    updateRequests = Interlocked.Read(ref updateRequests),
    recordSize = DhmpRecordSize,
    mode = "Latest"
}));

app.MapPost("/api/updates", async (
    HttpRequest request,
    HttpResponse response,
    CancellationToken cancellationToken) =>
{
    if (request.ContentLength is > InputRecordSize * MaxUpdatesPerRequest)
    {
        response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }

    using var inputBuffer = new MemoryStream();
    await request.Body.CopyToAsync(inputBuffer, cancellationToken);
    byte[] input = inputBuffer.ToArray();

    if (input.Length == 0 ||
        input.Length % InputRecordSize != 0 ||
        input.Length > InputRecordSize * MaxUpdatesPerRequest)
    {
        response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    int updateCount = input.Length / InputRecordSize;
    byte[] output = GC.AllocateUninitializedArray<byte>(
        updateCount * DhmpRecordSize);
    byte[] record = new byte[DhmpRecordSize];
    int outputOffset = 0;
    int accepted = 0;
    int published = 0;

    Interlocked.Increment(ref updateRequests);

    for (int index = 0; index < updateCount; index++)
    {
        ReadOnlySpan<byte> update =
            input.AsSpan(index * InputRecordSize, InputRecordSize);

        ushort x = BinaryPrimitives.ReadUInt16BigEndian(update[..2]);
        ushort y = BinaryPrimitives.ReadUInt16BigEndian(update.Slice(2, 2));

        if (x >= MaxWidth || y >= MaxHeight)
            continue;

        ulong currentGeneration = unchecked((ulong)Interlocked.Increment(
            ref generation));

        Span<byte> recordSpan = record;
        BinaryPrimitives.WriteUInt16BigEndian(recordSpan[..2], x);
        BinaryPrimitives.WriteUInt16BigEndian(recordSpan.Slice(2, 2), y);
        BinaryPrimitives.WriteUInt64BigEndian(
            recordSpan.Slice(4, 8),
            currentGeneration);
        update.Slice(4, 3).CopyTo(recordSpan.Slice(12, 3));
        recordSpan[15] = 0;

        accepted++;

        server.ProcessPacket(
            recordSpan,
            latest =>
            {
                latest.CopyTo(
                    output.AsSpan(
                        outputOffset,
                        DhmpRecordSize));
                outputOffset += DhmpRecordSize;
                published++;
            });
    }

    Interlocked.Add(ref receivedRecords, accepted);
    Interlocked.Add(
        ref receivedRecordBytes,
        (long)accepted * DhmpRecordSize);
    Interlocked.Add(ref publishedRecords, published);
    Interlocked.Add(
        ref publishedRecordBytes,
        (long)published * DhmpRecordSize);

    response.ContentType = "application/octet-stream";
    response.ContentLength = outputOffset;

    if (outputOffset > 0)
    {
        await response.Body.WriteAsync(
            output.AsMemory(0, outputOffset),
            cancellationToken);
    }
});

app.Run();
