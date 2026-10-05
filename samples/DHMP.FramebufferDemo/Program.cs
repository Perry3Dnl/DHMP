using System.Buffers.Binary;
using DHMP.Protocol;
using DHMP.Server;

const int Width = 1280;
const int Height = 720;
const int InputRecordSize = 7;
const int DhmpRecordSize = 16;
const int MaxUpdatesPerRequest = Width * Height;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var wire = new DhmpWireContract(DhmpRecordSize);
var receivePolicy = new DhmpReceivePolicy(
    DhmpProcessingMode.Latest,
    maximumPayloadBytes: DhmpRecordSize);
var server = new DhmpServer(wire, receivePolicy);
long[] generations = new long[Width * Height];

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    width = Width,
    height = Height,
    mode = "Latest",
    recordSize = DhmpRecordSize
}));

app.MapGet("/version", () => Results.Json(new
{
    commit = Environment.GetEnvironmentVariable("DHMP_DEMO_COMMIT") ?? "dev"
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
    await request.Body.CopyToAsync(
        inputBuffer,
        cancellationToken);

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

    for (int index = 0; index < updateCount; index++)
    {
        ReadOnlySpan<byte> update =
            input.AsSpan(index * InputRecordSize, InputRecordSize);

        ushort x = BinaryPrimitives.ReadUInt16BigEndian(update[..2]);
        ushort y = BinaryPrimitives.ReadUInt16BigEndian(update.Slice(2, 2));

        if (x >= Width || y >= Height)
            continue;

        int pixelIndex = y * Width + x;
        ulong generation = unchecked((ulong)Interlocked.Increment(
            ref generations[pixelIndex]));

        Span<byte> recordSpan = record;
        BinaryPrimitives.WriteUInt16BigEndian(recordSpan[..2], x);
        BinaryPrimitives.WriteUInt16BigEndian(recordSpan.Slice(2, 2), y);
        BinaryPrimitives.WriteUInt64BigEndian(
            recordSpan.Slice(4, 8),
            generation);
        update.Slice(4, 3).CopyTo(recordSpan.Slice(12, 3));
        recordSpan[15] = 0;

        server.ProcessPacket(
            recordSpan,
            latest =>
            {
                latest.CopyTo(
                    output.AsSpan(
                        outputOffset,
                        DhmpRecordSize));
                outputOffset += DhmpRecordSize;
            });
    }

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
