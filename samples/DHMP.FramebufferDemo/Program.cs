using System.Buffers.Binary;
using System.Net.WebSockets;
using DHMP.Protocol;
using DHMP.Server;

const int Width = 160;
const int Height = 90;
const int InputRecordSize = 7;
const int DhmpRecordSize = 16;
const int MaxUpdatesPerMessage = Width * Height;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

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

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();

    var wire = new DhmpWireContract(DhmpRecordSize);
    var receivePolicy = new DhmpReceivePolicy(
        DhmpProcessingMode.Latest,
        maximumPayloadBytes: DhmpRecordSize);
    var server = new DhmpServer(wire, receivePolicy);

    ulong[] generations = new ulong[Width * Height];
    byte[] input = new byte[InputRecordSize * MaxUpdatesPerMessage];
    byte[] output = new byte[DhmpRecordSize * MaxUpdatesPerMessage];

    while (socket.State == WebSocketState.Open &&
           !context.RequestAborted.IsCancellationRequested)
    {
        ValueWebSocketReceiveResult received = await socket.ReceiveAsync(
            input.AsMemory(),
            context.RequestAborted);

        if (received.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "closed",
                CancellationToken.None);
            break;
        }

        if (received.MessageType != WebSocketMessageType.Binary ||
            !received.EndOfMessage ||
            received.Count == 0 ||
            received.Count % InputRecordSize != 0)
        {
            await socket.CloseAsync(
                WebSocketCloseStatus.InvalidPayloadData,
                "Expected complete 7-byte pixel update records.",
                CancellationToken.None);
            break;
        }

        int updateCount = received.Count / InputRecordSize;
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
            ulong generation = ++generations[pixelIndex];

            Span<byte> record = stackalloc byte[DhmpRecordSize];
            BinaryPrimitives.WriteUInt16BigEndian(record[..2], x);
            BinaryPrimitives.WriteUInt16BigEndian(record.Slice(2, 2), y);
            BinaryPrimitives.WriteUInt64BigEndian(record.Slice(4, 8), generation);
            update.Slice(4, 3).CopyTo(record.Slice(12, 3));
            record[15] = 0;

            server.ProcessPacket(
                record,
                latest =>
                {
                    latest.CopyTo(
                        output.AsSpan(
                            outputOffset,
                            DhmpRecordSize));
                    outputOffset += DhmpRecordSize;
                });
        }

        if (outputOffset > 0)
        {
            await socket.SendAsync(
                output.AsMemory(0, outputOffset),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                context.RequestAborted);
        }
    }
});

app.Run();
