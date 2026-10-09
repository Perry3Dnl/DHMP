using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using DHMP.Licensing;
using DHMP.Protocol;
using DHMP.Unity;

namespace DHMP.Unity.Tests;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); checks++; }
    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidOperationException) { checks++; return; } throw new Exception(message); }

    private static void Main(string[] args)
    {
        if (args.Contains("--raw-smoke")) { RawSmoke(); return; }
        ControlInterop(); PacketValidation(); Motor(); Licensing(); Sessions();
        Console.WriteLine($"PASS: {checks} Unity portable-runtime checks (not a Unity Editor/IL2CPP run).");
    }

    private static void ControlInterop()
    {
        foreach (byte type in new byte[] { 1, 2, 3 })
        {
            byte[] bytes = DhmpWire.Control(type, 0x12345678, type == 3 ? (byte)3 : (byte)0);
            Check(DhmpControlCodec.TryDecode(bytes, out DhmpControlMessage decoded), "Reference codec rejected portable control.");
            Check(decoded.SchemaId == ArenaRecord.SchemaId && decoded.RecordSize == 128 && decoded.CorrelationId == 0x12345678,
                "Control fields/UUID byte order differ from DHMP.Protocol.");
            var encoded = new byte[32]; DhmpControlCodec.Encode(decoded, encoded);
            Check(encoded.SequenceEqual(bytes), "Control V1 bytes drifted.");
            Check(DhmpWire.TryControl(encoded, encoded.Length, out ControlMessage portable) && portable.Type == type, "Portable decode failed.");
        }
        var profile = new DhmpPeerProfile(new DhmpWireContract(ArenaRecord.Size), 128, ArenaRecord.SchemaId);
        var small = new byte[32]; DhmpControlCodec.Encode(DhmpControlMessage.Hello(profile, 1), small);
        Check(DhmpWire.TryControl(small, 32, out var hello) && hello.MaximumPayload == 128, "Small receive capability lost.");
        small[7] = 1; Check(!DhmpWire.TryControl(small, 32, out _), "HELLO accepted a rejection reason.");
        Check(!DhmpWire.ValidDataLength(0) && !DhmpWire.ValidDataLength(127) && !DhmpWire.ValidDataLength(1280), "Invalid packet length accepted.");
        Check(DhmpWire.Newer(1, uint.MaxValue) && !DhmpWire.Newer(uint.MaxValue, 1), "Sequence wrap comparison failed.");
    }
    private static void PacketValidation()
    {
        var record = new ArenaRecord { Kind = ArenaMessage.State, Session = 7, PlayerId = 5, Sequence = 9,
            Name = "Perry", State = new MotorState { X = -3, Y = 2, Yaw = 270 } };
        var bytes = new byte[256]; record.Encode(bytes); record.Encode(bytes, 128);
        var records = new ArenaRecord[9];
        Check(ArenaRecord.TryDecodePacket(bytes, bytes.Length, records, out int count) && count == 2 && records[0].State.X == -3 && records[1].Name == "Perry",
            "Arena record roundtrip failed.");
        bytes[129] = 2; Check(!ArenaRecord.TryDecodePacket(bytes, 256, records, out count) && count == 0, "Malformed tail partially published a packet.");
        record.Encode(bytes); bytes[32] = 0x7f; bytes[33] = 0xc0; bytes[34] = bytes[35] = 0;
        Check(!ArenaRecord.TryDecodePacket(bytes, 128, records, out _), "NaN entered simulation.");
        record.Encode(bytes); bytes[127] = 1; Check(!ArenaRecord.TryDecodePacket(bytes, 128, records, out _), "Reserved bytes accepted.");
        Check(ArenaRecord.NormalizeName("<b>Hi</b>\n").IndexOf('<') < 0, "Name markup leaked.");
        record.Name = new string('界', 40); record.Encode(bytes);
        Check(ArenaRecord.TryDecodePacket(bytes, 128, records, out _) && System.Text.Encoding.UTF8.GetByteCount(records[0].Name) <= 32, "UTF8 name boundary invalid.");
    }
    private static void Motor()
    {
        MotorState state = ArenaMotor.Spawn(1), start = state;
        for (int i = 0; i < 30; i++) state = ArenaMotor.Step(state, 1, 1, false, 0);
        double distance = Math.Sqrt(Math.Pow(state.X - start.X, 2) + Math.Pow(state.Z - start.Z, 2));
        Check(Math.Abs(distance - 5) < 0.01, "Diagonal movement bypassed speed limit.");
        state = ArenaMotor.Spawn(1); state = ArenaMotor.Step(state, 0, 0, true, 0);
        Check(state.Y > 0 && !state.Grounded, "Jump did not leave ground.");
        float maximum = state.Y;
        for (int i = 0; i < 60; i++) { state = ArenaMotor.Step(state, 0, 0, false, 0); maximum = Math.Max(maximum, state.Y); }
        Check(maximum > 1 && state.Grounded && state.Y == 0, "Jump did not land.");
        state = new MotorState { X = -6, Z = 3, Y = 3 };
        for (int i = 0; i < 60; i++) state = ArenaMotor.Step(state, 0, 0, false, 0);
        Check(state.Grounded && Math.Abs(state.Y - 0.75f) < 0.001, "Server/shared platform landing differs.");
        state = new MotorState { X = 23.5f, Grounded = true };
        for (int i = 0; i < 60; i++) state = ArenaMotor.Step(state, 1, 0, false, 0);
        Check(state.X <= ArenaMotor.Extent - ArenaMotor.Radius, "Arena boundary bypassed.");
    }
    private static void Licensing()
    {
        using ECDsa issuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Guid application = Guid.NewGuid(); var payload = new byte[41]; payload[0] = 1;
        application.TryWriteBytes(payload.AsSpan(1, 16), true, out _); Guid.NewGuid().TryWriteBytes(payload.AsSpan(17, 16), true, out _);
        string Encode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string license = "DHMP1." + Encode(payload) + "." + Encode(issuer.SignData(payload, HashAlgorithmName.SHA256));
        byte[] publicKey = issuer.ExportSubjectPublicKeyInfo();
        Check(new DhmpLicenseValidator(publicKey).Validate(license, application).IsValid, "Reference license rejected fixture.");
        DhmpUnityLicense.Validate(license, application, publicKey); checks++;
        Reject(() => DhmpUnityLicense.Validate(license, Guid.NewGuid(), publicKey), "Wrong application licensed.");
        Reject(() => DhmpUnityLicense.Validate(null, application, publicKey), "Missing license accepted.");
        payload[1] ^= 1;
        string changed = "DHMP1." + Encode(payload) + "." + license.Split('.')[2];
        Reject(() => DhmpUnityLicense.Validate(changed, application, publicKey), "Tampered license accepted.");
    }

    private static void Sessions()
    {
        var bus = new Bus(); IPAddress serverIp = IPAddress.Parse("2001:db8::1"), clientIp = IPAddress.Parse("2001:db8::2");
        var serverData = bus.Open(serverIp, false); var serverControl = bus.Open(serverIp, true);
        using var server = new ArenaServer(serverData, serverControl, 16);
        using var client = new ArenaClient(bus.Open(clientIp, false), bus.Open(clientIp, true), serverIp, "Perry", 0);
        for (int i = 0; i < 4; i++) { server.Poll(0); client.Poll(0); }
        Check(client.Phase == ArenaClientPhase.Connected && server.PlayerCount == 1, "Join/compatibility did not complete.");
        MotorState first = client.Predicted;
        ArenaRecord lastOwn = default; client.PlayerState += r => { if (r.PlayerId == client.PlayerId) lastOwn = r; };
        byte[] staleInput = null;
        for (int i = 1; i <= 30; i++)
        {
            double now = i / 30d; client.SubmitInput(1, 0, i == 2, 90, now);
            staleInput = bus.LastData;
            // Deliver duplicates and out-of-order redundant commands; they must not grant extra simulation steps.
            if (i % 3 == 0) serverData.Inject(clientIp, staleInput);
            server.Poll(now); server.Tick(); client.Poll(now);
        }
        Check(Math.Abs(lastOwn.State.X - first.X - 5) < 0.02, "Duplicate inputs changed authoritative movement speed.");
        Check(lastOwn.AcknowledgedInput == 30, "Input acknowledgement/reconciliation stalled.");
        Check(Math.Abs(client.Predicted.X - lastOwn.State.X) < 0.01, "Local prediction did not reconcile.");
        float previousX = lastOwn.State.X;
        for (int i = 31; i <= 33; i++) { serverData.Inject(clientIp, staleInput); server.Poll(i / 30d); server.Tick(); client.Poll(i / 30d); }
        Check(Math.Abs(lastOwn.State.X - previousX) < 0.01, "Replayed acknowledged input moved a player.");
        client.Dispose(); server.Poll(1.2); Check(server.PlayerCount == 0, "Leave did not remove player.");
        using var replacement = new ArenaClient(bus.Open(clientIp, false), bus.Open(clientIp, true), serverIp, "New", 1.3);
        for (int i = 0; i < 4; i++) { server.Poll(1.3); replacement.Poll(1.3); }
        Check(replacement.Phase == ArenaClientPhase.Connected && replacement.PlayerId != client.PlayerId, "Reconnect retained stale session identity.");
        ArenaRecord replacementState = default; replacement.PlayerState += r => { if (r.PlayerId == replacement.PlayerId) replacementState = r; };
        float spawnX = replacement.Predicted.X;
        serverData.Inject(clientIp, staleInput); server.Poll(1.4);
        for (int i = 0; i < 3; i++) server.Tick(); replacement.Poll(1.4);
        Check(Math.Abs(replacementState.State.X - spawnX) < 0.01, "Old-session packet moved a new player.");

        // More entities than one packet: every actor must be published, not just packet-local Latest.
        var clients = new List<ArenaClient>(); var seen = new HashSet<uint>();
        replacement.PlayerState += r => seen.Add(r.PlayerId);
        try
        {
            for (int i = 3; i <= 13; i++)
            {
                IPAddress ip = IPAddress.Parse("2001:db8::" + i.ToString("x"));
                var other = new ArenaClient(bus.Open(ip, false), bus.Open(ip, true), serverIp, "Other", 1.5); clients.Add(other);
                for (int pass = 0; pass < 4; pass++) { server.Poll(1.5); other.Poll(1.5); }
            }
            for (int i = 0; i < 3; i++) server.Tick(); replacement.Poll(1.6);
            Check(seen.Count == 12 && server.PlayerCount == 12, "Batched snapshots lost actors.");
            Check(bus.MaximumPacket <= DhmpWire.MaximumPayload, "Snapshot exceeded conservative path budget.");
            server.Poll(12); Check(server.PlayerCount == 0, "Inactive peers were retained without bound.");
        }
        finally { foreach (ArenaClient other in clients) other.Dispose(); }
    }

    private static void RawSmoke()
    {
        using var server = new ArenaServer(new DhmpRawIpv6Socket(IPAddress.IPv6Loopback, 253, true, true),
            new DhmpRawIpv6Socket(IPAddress.IPv6Loopback, 254, true, true));
        var clock = Stopwatch.StartNew();
        using var client = new ArenaClient(new DhmpRawIpv6Socket(IPAddress.IPv6Loopback, 253, true, true),
            new DhmpRawIpv6Socket(IPAddress.IPv6Loopback, 254, true, true), IPAddress.IPv6Loopback, "RawSmoke", clock.Elapsed.TotalSeconds);
        double nextTick = 0; float firstX = 0; bool joined = false; int inputs = 0; ArenaRecord authoritative = default;
        client.PlayerState += r => { if (r.PlayerId == client.PlayerId) authoritative = r; };
        while (clock.Elapsed.TotalSeconds < 4)
        {
            double now = clock.Elapsed.TotalSeconds;
            server.Poll(now); client.Poll(now);
            if (client.Phase == ArenaClientPhase.Connected && !joined) { joined = true; firstX = client.Predicted.X; }
            if (now >= nextTick)
            {
                if (joined && inputs < 45) { client.SubmitInput(1, 0, inputs == 5, 90, now); inputs++; }
                server.Poll(now); server.Tick(); nextTick = now + ArenaMotor.StepSeconds;
            }
            Thread.Sleep(1);
        }
        Check(joined && inputs == 45 && authoritative.State.X > firstX + 6, "Raw IPv6 arena input/state exchange failed.");
        client.Dispose(); server.Poll(clock.Elapsed.TotalSeconds);
        Check(server.PlayerCount == 0, "Raw IPv6 disconnect failed.");
        Console.WriteLine("PASS: real raw IPv6 253/254 loopback, compatibility, join, movement/jump input, snapshots, disconnect. Not two-host or Unity runtime evidence.");
    }

    private sealed class Bus
    {
        private readonly Dictionary<(IPAddress, bool), MemorySocket> sockets = new();
        public byte[] LastData;
        public int MaximumPacket;
        public MemorySocket Open(IPAddress address, bool control)
        { var socket = new MemorySocket(this, address, control); sockets[(address, control)] = socket; return socket; }
        public void Send(IPAddress source, IPAddress destination, bool control, byte[] bytes, int length)
        {
            var copy = bytes.AsSpan(0, length).ToArray(); MaximumPacket = Math.Max(MaximumPacket, length);
            if (!control && copy[0] == (byte)ArenaMessage.Input) LastData = copy;
            if (sockets.TryGetValue((destination, control), out MemorySocket socket)) socket.Inject(source, copy);
        }
    }
    private sealed class MemorySocket : IDhmpPacketSocket
    {
        private readonly Bus bus; private readonly IPAddress local; private readonly bool control;
        private readonly Queue<(IPAddress, byte[])> queue = new();
        public MemorySocket(Bus b, IPAddress address, bool isControl) { bus = b; local = address; control = isControl; }
        public void Inject(IPAddress source, byte[] bytes) => queue.Enqueue((source, bytes));
        public bool Send(IPAddress peer, byte[] bytes, int length) { bus.Send(local, peer, control, bytes, length); return true; }
        public bool TryReceive(byte[] buffer, out IPAddress peer, out int length)
        {
            peer = null; length = 0; if (!queue.TryDequeue(out var item)) return false;
            peer = item.Item1; length = item.Item2.Length; Array.Copy(item.Item2, buffer, length); return true;
        }
        public void Dispose() => queue.Clear();
    }
}
