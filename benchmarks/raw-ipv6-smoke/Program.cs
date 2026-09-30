using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Security.Cryptography;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.RawIpv6;
using DHMP.Security;
using DHMP.Server;

// Controlled two-host functional smoke run, not a throughput benchmark.
if (args.Length is < 4 or > 6 || args[0] is not ("send" or "receive") || args[3] is not ("plain" or "psk"))
{
    Console.Error.WriteLine("Usage: send|receive LOCAL_IPV6 REMOTE_IPV6 plain|psk [records=6000] [psk-file]");
    return 2;
}
int records = args.Length > 4 ? int.Parse(args[4]) : 6000;
if (records is < 1 or > 360000) throw new ArgumentOutOfRangeException(nameof(records));
bool sending = args[0] == "send";
bool secure = args[3] == "psk";
if (secure && args.Length != 6) throw new ArgumentException("PSK mode requires a file containing 32 key bytes as hexadecimal.");
const int RecordSize = 32;
const int Rate = 100;
var wire = new DhmpWireContract(RecordSize);
var receivePolicy = new DhmpReceivePolicy(DhmpProcessingMode.Sequential, RecordSize);
var sendPolicy = new DhmpSendPolicy(Rate, RecordSize, DhmpRatePolicy.SmoothPacing);
var options = DhmpRawIpv6Options.FromPathMtu(IPAddress.Parse(args[1]), IPAddress.Parse(args[2]),
    1280, handshakeTimeout: TimeSpan.FromSeconds(120));
var schema = Guid.Parse("20112765-1a09-4dca-b34a-c88de3ead032");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
DhmpPskChaCha20Poly1305Session? session = null;
try
{
    if (secure)
    {
        byte[] bytes = Convert.FromHexString(File.ReadAllText(args[5]).Trim());
        DhmpPreSharedKey key;
        try { key = new DhmpPreSharedKey(1, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        using var ownedKey = key;
        session = sending
            ? await DhmpRawIpv6SecurityHandshake.InitiateAsync(options, key, stop.Token)
            : await DhmpRawIpv6SecurityHandshake.RespondOnceAsync(options, key, stop.Token);
        Console.WriteLine($"SETUP profile=psk-v2 session={session.SessionId}");
    }
    else
    {
        _ = sending
            ? await DhmpRawIpv6Handshake.InitiateAsync(options, wire, sendPolicy, receivePolicy, schema, stop.Token)
            : await DhmpRawIpv6Handshake.RespondOnceAsync(options, wire, sendPolicy, receivePolicy, schema, stop.Token);
        Console.WriteLine("SETUP profile=plain-control-v1");
    }

    if (sending)
    {
        using var backend = new DhmpRawIpv6PacketSender(options);
        var protectedSender = session is null ? null : new DhmpProtectedPacketSender(backend, session);
        IDhmpPacketSender sender = protectedSender is null ? backend : protectedSender;
        var client = new DhmpClient(sender, wire, sendPolicy);
        long submitted = 0;
        try
        {
            Console.WriteLine("Wait for receiver READY, then press Enter. Local completion does not prove delivery.");
            if (await Console.In.ReadLineAsync(stop.Token) is null)
                throw new IOException("Interactive input is required for the receiver readiness handoff.");
            byte[] record = new byte[RecordSize];
            for (ulong sequence = 1; sequence <= (ulong)records; sequence++)
            {
                BinaryPrimitives.WriteUInt64BigEndian(record, sequence);
                for (int i = 8; i < record.Length; i++) record[i] = (byte)((sequence + (ulong)i) % 256);
                await client.SendAsync(record, stop.Token);
                submitted++;
            }
        }
        finally
        {
            if (protectedSender is not null) await protectedSender.DisposeAsync();
            Console.WriteLine($"SEND submitted={submitted} expected={records} rate_limit={Rate}");
        }
    }
    else
    {
        using var receiver = new DhmpRawIpv6Receiver(options, new DhmpServer(wire, receivePolicy), session);
        var seen = new BitArray(records);
        long unique = 0, duplicates = 0, reordered = 0, malformed = 0;
        ulong highest = 0;
        // One fixed record per packet; sequence/pattern belong to this test application's schema.
        Task receiving = receiver.RunAsync(batch =>
        {
            ulong sequence = BinaryPrimitives.ReadUInt64BigEndian(batch);
            if (sequence == 0 || sequence > (ulong)records) { malformed++; return; }
            for (int i = 8; i < batch.Length; i++)
                if (batch[i] != (byte)((sequence + (ulong)i) % 256)) { malformed++; return; }
            int index = (int)sequence - 1;
            if (seen[index]) { duplicates++; return; }
            seen[index] = true;
            unique++;
            if (sequence < highest) reordered++;
            highest = Math.Max(highest, sequence);
        }, stop.Token);
        Console.WriteLine($"READY expected={records}; press Enter after sender finishes (or Ctrl+C to abort).");
        bool endedByInput = false;
        try
        {
            // Bounded overall receive lifetime, including an absent or failed sender.
            stop.CancelAfter(TimeSpan.FromSeconds(records / (double)Rate + 120));
            Task<string?> input = Console.In.ReadLineAsync(stop.Token).AsTask();
            Task completed = await Task.WhenAny(receiving, input);
            await completed;
            if (completed == input)
            {
                if (await input is null) throw new IOException("Interactive input is required to end the receive run.");
                await Task.Delay(TimeSpan.FromSeconds(2), stop.Token); // Explicit final drain grace, no remote ACK.
                endedByInput = true;
            }
        }
        finally
        {
            stop.Cancel();
            await receiving;
            Console.WriteLine($"RECEIVE unique={unique} missing={records - unique} duplicates={duplicates} reordered={reordered} " +
                $"malformed_records={malformed} accepted_packets={receiver.AcceptedPackets} rejected_packets={receiver.RejectedPackets} " +
                $"protection_rejected={receiver.ProtectionRejectedPackets} foreign_peer={receiver.ForeignPeerPackets}");
        }
        if (!endedByInput || unique != records || malformed != 0 || receiver.RejectedPackets != 0 || receiver.ProtectionRejectedPackets != 0) return 1;
    }
    return 0;
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.Error.WriteLine("Run cancelled or receive deadline reached; retain partial results, do not count as a pass.");
    return 1;
}
finally
{
    // Incoming loop and outgoing wrapper have completed before caller-owned crypto is retired.
    session?.Dispose();
}
