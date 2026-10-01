using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.RawIpv6;
using DHMP.Security;
using DHMP.Server;

// Privileged laboratory runner only. Separate session/schema from the DAPI sample.
internal static class RawEcho
{
    internal static async Task RunAsync(string configuration)
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(configuration));
        var api = json.RootElement.GetProperty("DHMP").GetProperty("Api");
        bool initiator = api.GetProperty("Initiator").GetBoolean();
        var options = DhmpRawIpv6Options.FromPathMtu(IPAddress.Parse(api.GetProperty("LocalAddress").GetString()!),
            IPAddress.Parse(api.GetProperty("RemoteAddress").GetString()!), 1280, handshakeTimeout: TimeSpan.FromSeconds(30));
        byte[] keyBytes = Convert.FromBase64String(api.GetProperty("PreSharedKeyBase64").GetString()!);
        using var key = new DhmpPreSharedKey(1, keyBytes);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(keyBytes);
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        using var session = initiator ? await DhmpRawIpv6SecurityHandshake.InitiateAsync(options, key, stop.Token)
            : await DhmpRawIpv6SecurityHandshake.RespondOnceAsync(options, key, stop.Token);
        using var backend = new DhmpRawIpv6PacketSender(options);
        await using var protectedSender = new DhmpProtectedPacketSender(backend, session);
        var client = new DhmpClient(protectedSender, new(1200), new(int.MaxValue, 1200));
        using var receiver = new DhmpRawIpv6Receiver(options, new DhmpServer(new(1200), new(DhmpProcessingMode.Sequential, 1200)), session);
        await using var profile = new DhmpEchoConfirmation(client, session.SessionId, new() { MaximumInFlight = 32, ConfirmationTimeout = TimeSpan.FromSeconds(5) });
        var incoming = Channel.CreateBounded<byte[]>(64);
        Task receiving = receiver.RunAsync(bytes => { if (!incoming.Writer.TryWrite(bytes.ToArray())) throw new InvalidOperationException("Echo lab queue saturated."); }, stop.Token);
        Task consuming = Task.Run(async () =>
        {
            await foreach (byte[] record in incoming.Reader.ReadAllAsync(stop.Token))
            {
                var content = await profile.ReceiveAsync(record, stop.Token);
                if (content is { } body)
                    for (int i = 0; i < body.Length; i++) if (body.Span[i] != (byte)i) throw new InvalidDataException("Raw echo content mismatch.");
            }
        });
        string ready = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configuration))!, "raw-echo-backend-ready");
        try
        {
            if (!initiator)
            {
                await File.WriteAllTextAsync(ready, session.SessionId.ToString(), stop.Token);
                await Task.Delay(Timeout.Infinite, stop.Token);
            }
            else
            {
                while (!File.Exists(ready)) await Task.Delay(10, stop.Token);
                if (await File.ReadAllTextAsync(ready, stop.Token) != session.SessionId.ToString()) throw new InvalidDataException("Session mismatch.");
                byte[] content = Enumerable.Range(0, 1160).Select(i => (byte)i).ToArray();
                for (int i = 0; i < 8; i++) await profile.SendAsync(content, stop.Token);
                for (int repetition = 1; repetition <= 5; repetition++)
                foreach (int concurrency in new[] { 1, 8, 32 })
                {
                    var latencies = new List<double>();
                    long start = Stopwatch.GetTimestamp();
                    for (int group = 0; group < 8; group++)
                    {
                        var calls = Enumerable.Range(0, concurrency).Select(_ => profile.SendAsync(content, stop.Token)).ToArray();
                        foreach (var receipt in await Task.WhenAll(calls)) latencies.Add(receipt.RoundTripTime.TotalMilliseconds);
                    }
                    double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    latencies.Sort();
                    Console.WriteLine("RESULT_JSON " + JsonSerializer.Serialize(new
                    {
                        name = $"raw-protected-echo-concurrency-{concurrency}", scope = "single-vm-two-netns-real-raw-ipv6-protected-echo",
                        repetition, concurrency, confirmations = latencies.Count, seconds,
                        useful_MBps = latencies.Count * 1160.0 / seconds / 1e6,
                        p50_ms = latencies[latencies.Count / 2], p95_ms = latencies[(int)Math.Ceiling(latencies.Count * .95) - 1],
                        min_ms = latencies[0], max_ms = latencies[^1], session = session.SessionId,
                        rejected_packets = receiver.RejectedPackets, protection_rejections = receiver.ProtectionRejectedPackets
                    }));
                }
            }
        }
        catch (OperationCanceledException) when (!initiator && stop.IsCancellationRequested) { }
        finally
        {
            stop.Cancel();
            try { await receiving; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            try { await consuming; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}
