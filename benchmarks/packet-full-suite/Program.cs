using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.Security;
using DHMP.Server;

if (args.Length == 2 && args[0] == "--raw-echo") { await RawEcho.RunAsync(args[1]); return; }

var cases = new List<Case>();
long guard = 0;
foreach (var mode in new[] { DhmpProcessingMode.Latest, DhmpProcessingMode.Sequential })
foreach (int batch in new[] { 1, 8, 44 })
{
    byte[] packet = Enumerable.Range(0, batch * 32).Select(i => (byte)i).ToArray();
    var processor = new DhmpPacketProcessor(new(32), new(mode, packet.Length));
    Action<ReadOnlySpan<byte>> consume = bytes => { foreach (byte value in bytes) guard += value; };
    cases.Add(new($"core-{mode}-batch-{batch}", packet.Length, mode == DhmpProcessingMode.Latest ? 32 : packet.Length,
        () => processor.Process(packet, consume), "memory-core-plus-consumer"));
}
byte[] control = new byte[32];
var hello = new DhmpControlMessage(DhmpControlMessageType.Hello, 1, 32, 1408, Guid.NewGuid(), 1);
cases.Add(new("control-codec-roundtrip", 32, 32, () =>
{
    DhmpControlCodec.Encode(hello, control);
    if (!DhmpControlCodec.TryDecode(control, out var decoded) || decoded != hello) throw new InvalidDataException("Control mismatch.");
    guard += decoded.RecordSize;
}, "memory-control-codec"));
var latest = new DhmpLatestGenerationFilter(32);
byte[] generated = new byte[32]; ulong generation = 0;
Action<ReadOnlySpan<byte>> publishGeneration = bytes => guard += bytes[7];
cases.Add(new("latest-generation-filter", 32, 32, () =>
{
    BinaryPrimitives.WriteUInt64BigEndian(generated, ++generation);
    if (!latest.TryPublish(generated, publishGeneration)) throw new InvalidDataException("Fresh generation rejected.");
}, "memory-application-filter"));
using var key = new DhmpPreSharedKey(1, Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
var disposables = new List<IDisposable>();
foreach (int size in new[] { 32, 1200, 1408 })
{
    Guid id = Guid.NewGuid();
    var tx = new DhmpPskChaCha20Poly1305Session(key, id, DhmpSecurityRole.Initiator);
    var rx = new DhmpPskChaCha20Poly1305Session(key, id, DhmpSecurityRole.Responder);
    disposables.Add(tx); disposables.Add(rx);
    byte[] plain = Enumerable.Range(0, size).Select(i => (byte)i).ToArray(), protectedBytes = new byte[size + 24], decoded = new byte[size];
    cases.Add(new($"psk-protect-decode-{size}", size, size, () =>
    {
        int length = tx.Protect(plain, protectedBytes);
        if (!rx.TryDecode(protectedBytes.AsSpan(0, length), decoded, out int count) || count != size || !plain.AsSpan().SequenceEqual(decoded))
            throw new InvalidDataException("Protected content mismatch.");
        guard += decoded[0];
    }, "memory-protect-plus-decode-plus-byte-check"));
}
Guid feedbackId = Guid.NewGuid();
using var feedbackSender = new DhmpPskChaCha20Poly1305Session(key, feedbackId, DhmpSecurityRole.Initiator);
using var feedbackReceiver = new DhmpPskChaCha20Poly1305Session(key, feedbackId, DhmpSecurityRole.Responder);
byte[] feedbackPacket = new byte[64];
var feedback = new DhmpCongestionFeedback(DhmpCongestionPressure.Soft, 750, 1, 8, 0);
cases.Add(new("authenticated-feedback-roundtrip", 64, 64, () =>
{
    int length = feedbackSender.EncodeCongestionFeedback(feedback, feedbackPacket);
    if (!feedbackReceiver.TryDecodeCongestionFeedback(feedbackPacket.AsSpan(0, length), out var decoded) || decoded != feedback)
        throw new InvalidDataException("Feedback mismatch.");
}, "memory-authenticated-feedback"));
cases.Add(new("psk-session-key-derivation", 0, 0, () =>
{
    using var session = new DhmpPskChaCha20Poly1305Session(key, Guid.NewGuid(), DhmpSecurityRole.Initiator);
    guard += DhmpPskChaCha20Poly1305Session.Overhead;
}, "memory-hkdf-session-construction-disposal-not-network-handshake"));
// Same fixed record/path/protection and content size; echo adds reverse protection and confirmation.
Guid echoSession = Guid.NewGuid();
using var aSession = new DhmpPskChaCha20Poly1305Session(key, echoSession, DhmpSecurityRole.Initiator);
using var bSession = new DhmpPskChaCha20Poly1305Session(key, echoSession, DhmpSecurityRole.Responder);
var aBackend = new MemoryProtectedPeer(aSession, bSession);
var bBackend = new MemoryProtectedPeer(bSession, aSession);
var aClient = new DhmpClient(aBackend, new(1200), new(int.MaxValue, 1200));
var bClient = new DhmpClient(bBackend, new(1200), new(int.MaxValue, 1200));
await using var aEcho = new DhmpEchoConfirmation(aClient, echoSession);
await using var bEcho = new DhmpEchoConfirmation(bClient, echoSession);
byte[] body = Enumerable.Range(0, 1160).Select(i => (byte)i).ToArray();
byte[] ordinaryRecord = new byte[1200]; body.CopyTo(ordinaryRecord, 40);
Func<ReadOnlyMemory<byte>, ValueTask> plainHandler = record =>
{
    if (!record.Span.SequenceEqual(ordinaryRecord)) throw new InvalidDataException("One-way mismatch.");
    return ValueTask.CompletedTask;
};
Func<ReadOnlyMemory<byte>, ValueTask> requestHandler = async record =>
{
    var received = await bEcho.ReceiveAsync(record);
    if (received is null || !received.Value.Span.SequenceEqual(body)) throw new InvalidDataException("Echo content mismatch.");
};
Func<ReadOnlyMemory<byte>, ValueTask> responseHandler = async record => { await aEcho.ReceiveAsync(record); };
cases.Add(new("protected-one-way-record-1200", 1160, 1160,
    () => aClient.SendAsync(ordinaryRecord).GetAwaiter().GetResult(), "memory-composed-client-protect-decode-byte-check",
    () => aBackend.Receive = plainHandler));
cases.Add(new("protected-full-echo-record-1200", 1160, 1160,
    () => aEcho.SendAsync(body).GetAwaiter().GetResult(), "memory-composed-two-way-protected-echo",
    () => { aBackend.Receive = requestHandler; bBackend.Receive = responseHandler; }));

foreach (Case test in cases) Run(test, 100, false, 0);
for (int repeat = 0; repeat < 5; repeat++)
for (int index = 0; index < cases.Count; index++)
    Run(cases[(index + repeat) % cases.Count], 500, true, repeat + 1);
foreach (var item in disposables) item.Dispose();
GC.KeepAlive(guard);

static void Run(Case test, int milliseconds, bool print, int repetition)
{
    test.Setup?.Invoke();
    long allocations = GC.GetTotalAllocatedBytes(true);
    long start = Stopwatch.GetTimestamp(), operations = 0;
    do
    {
        for (int i = 0; i < 128; i++) { test.Operation(); operations++; }
    } while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < milliseconds);
    double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
    long allocated = GC.GetTotalAllocatedBytes(true) - allocations;
    if (print) Console.WriteLine("RESULT_JSON " + JsonSerializer.Serialize(new
    {
        name = test.Name, scope = test.Scope, repetition, operations, seconds,
        operations_per_second = operations / seconds, ns_per_operation = seconds * 1e9 / operations,
        offered_GBps = operations * (double)test.OfferedBytes / seconds / 1e9,
        consumed_GBps = operations * (double)test.ConsumedBytes / seconds / 1e9,
        allocated_bytes_per_operation = allocated / (double)operations
    }));
}
sealed record Case(string Name, int OfferedBytes, int ConsumedBytes, Action Operation, string Scope, Action? Setup = null);
sealed class MemoryProtectedPeer(DhmpPskChaCha20Poly1305Session sender, DhmpPskChaCha20Poly1305Session receiver) : IDhmpPacketSender
{
    private readonly byte[] _encrypted = new byte[1224], _plain = new byte[1200];
    public Func<ReadOnlyMemory<byte>, ValueTask> Receive { get; set; } = _ => ValueTask.CompletedTask;
    public int MaximumPayloadBytes => 1200;
    public ValueTask SendPacketAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int length = sender.Protect(payload.Span, _encrypted);
        if (!receiver.TryDecode(_encrypted.AsSpan(0, length), _plain, out int count) || count != 1200)
            throw new InvalidDataException("Peer protection failed.");
        return Receive(_plain.AsMemory(0, count));
    }
}
