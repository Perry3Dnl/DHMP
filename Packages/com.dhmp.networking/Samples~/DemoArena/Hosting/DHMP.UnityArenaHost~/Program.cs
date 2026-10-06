using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using DHMP.Unity;

namespace DHMP.UnityArenaHost;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            if (options.Command == "help")
            {
                Console.WriteLine("DHMP.UnityArenaHost serve|connect|check-host --bind <local IPv6> --experimental-plaintext [--development] [--server <IPv6>] [--allow-client <IPv6>] [--max-players <1..32>]");
                Console.WriteLine("Development mode requires a Debug build. Release uses DHMP_APPLICATION_ID, DHMP_LICENSE_KEY and DHMP_LICENSE_PUBLIC_KEY. Public lab hosting requires explicit --allow-client addresses.");
                return 0;
            }
            options.ValidateLicense();
            using var data = new DhmpRawIpv6Socket(options.Bind, DhmpWire.DataProtocol, true, true);
            using var control = new DhmpRawIpv6Socket(options.Bind, DhmpWire.ControlProtocol, true, true);
            if (options.Command == "check-host")
            {
                Write("host_ready", new { bind = options.Bind.ToString(), detail = "Raw IPv6 bind succeeded locally; remote reachability is not established." });
                return 0;
            }
            return options.Command == "serve" ? Serve(options, data, control) : Connect(options, data, control);
        }
        catch (Exception error)
        {
            // Never serialize configuration objects or license material in logs.
            Write("error", new { message = error.Message }); return 1;
        }
    }

    private static int Serve(Options options, IDhmpPacketSocket data, IDhmpPacketSocket control)
    {
        using var lifetime = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
        using var termination = OperatingSystem.IsLinux()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; lifetime.Cancel(); }) : null;
        using var server = new ArenaServer(new SourceFilter(data, options.AllowedClients),
            new SourceFilter(control, options.AllowedClients), options.MaximumPlayers);
        var clock = Stopwatch.StartNew();
        double nextTick = 0, nextStatus = 5;
        int previousPlayers = 0;
        Write("ready", new { bind = options.Bind.ToString(), maximumPlayers = options.MaximumPlayers,
            simulationHz = ArenaMotor.TickRate, allowedClients = options.AllowedClients.Select(ip => ip.ToString()).ToArray(),
            mode = options.Development ? "development-plaintext-lab" : "licensed-plaintext-lab" });
        while (!lifetime.IsCancellationRequested)
        {
            double now = clock.Elapsed.TotalSeconds;
            server.Poll(now);
            if (now >= nextTick)
            {
                server.Tick(); nextTick += ArenaMotor.StepSeconds;
                if (now - nextTick > 4 * ArenaMotor.StepSeconds) nextTick = now + ArenaMotor.StepSeconds;
            }
            if (server.PlayerCount != previousPlayers || now >= nextStatus)
            {
                Write("status", new { players = server.PlayerCount, sentPayloadBytes = server.SentPayloadBytes,
                    receivedPayloadBytes = server.ReceivedPayloadBytes, droppedSends = server.DroppedSends });
                previousPlayers = server.PlayerCount; nextStatus = now + 5;
            }
            Thread.Sleep(1);
        }
        Write("stopped", new { players = server.PlayerCount }); return 0;
    }

    private static int Connect(Options options, IDhmpPacketSocket data, IDhmpPacketSocket control)
    {
        var clock = Stopwatch.StartNew();
        using var client = new ArenaClient(data, control, options.Server, "Connection test", 0);
        bool acknowledged = false, announced = false;
        client.PlayerState += record =>
        {
            if (record.PlayerId == client.PlayerId && record.Kind == ArenaMessage.State && record.AcknowledgedInput > 0)
                acknowledged = true;
        };
        double nextInput = 0;
        while (clock.Elapsed.TotalSeconds < 12)
        {
            double now = clock.Elapsed.TotalSeconds;
            client.Poll(now);
            if (client.Phase == ArenaClientPhase.Connected && !announced)
            { Write("connected", new { server = options.Server.ToString(), playerId = client.PlayerId }); announced = true; }
            if (client.Phase == ArenaClientPhase.Connected && now >= nextInput)
            { client.SubmitInput(0, 0, false, 0, now); nextInput = now + ArenaMotor.StepSeconds; }
            if (acknowledged)
            {
                Write("connection_verified", new { playerId = client.PlayerId, inputToSnapshotMilliseconds = client.RoundTripMilliseconds,
                    detail = "Compatibility, guest join and applied input/state reply verified; peer identity is not authenticated." });
                return 0;
            }
            Thread.Sleep(1);
        }
        throw new TimeoutException("No applied-input snapshot returned from the arena server within twelve seconds.");
    }

    private static void Write(string kind, object detail) => Console.WriteLine(JsonSerializer.Serialize(new { kind, detail }));

    private sealed class SourceFilter : IDhmpPacketSocket
    {
        private readonly IDhmpPacketSocket inner;
        private readonly HashSet<IPAddress> allowed;
        public SourceFilter(IDhmpPacketSocket socket, HashSet<IPAddress> addresses) { inner = socket; allowed = addresses; }
        public bool Send(IPAddress peer, byte[] payload, int length) => allowed.Contains(peer) && inner.Send(peer, payload, length);
        public bool TryReceive(byte[] buffer, out IPAddress peer, out int length)
        {
            for (int i = 0; i < 32; i++)
                if (!inner.TryReceive(buffer, out peer, out length)) return false;
                else if (allowed.Contains(peer)) return true;
            peer = null; length = 0; return false;
        }
        public void Dispose() => inner.Dispose();
    }

    private sealed class Options
    {
        public string Command;
        public IPAddress Bind, Server;
        public readonly HashSet<IPAddress> AllowedClients = new();
        public bool Development;
        public int MaximumPlayers = 16;
        public static Options Parse(string[] args)
        {
            var result = new Options { Command = args.Length == 0 ? "help" : args[0] };
            if (result.Command is "help" or "--help") { result.Command = "help"; return result; }
            if (result.Command is not ("serve" or "connect" or "check-host")) throw new ArgumentException("Unknown host command.");
            bool plaintext = false;
            for (int i = 1; i < args.Length; i++)
            {
                string option = args[i];
                if (option == "--development") { result.Development = true; continue; }
                if (option == "--experimental-plaintext") { plaintext = true; continue; }
                if (++i >= args.Length) throw new ArgumentException("Missing value for " + option);
                switch (option)
                {
                    case "--bind": result.Bind = Address(args[i]); break;
                    case "--server": result.Server = Address(args[i]); break;
                    case "--allow-client": result.AllowedClients.Add(Address(args[i])); break;
                    case "--max-players": result.MaximumPlayers = int.Parse(args[i]); break;
                    default: throw new ArgumentException("Unknown option " + option);
                }
            }
            if (!plaintext) throw new InvalidOperationException("This controlled preview requires --experimental-plaintext.");
            if (result.Bind == null) result.Bind = Address(Environment.GetEnvironmentVariable("DHMP_LOCAL_IPV6") ?? "");
            if (result.MaximumPlayers < 1 || result.MaximumPlayers > 32) throw new ArgumentException("Maximum players must be between 1 and 32.");
            if (result.Command == "connect" && result.Server == null) throw new ArgumentException("Set --server to the arena's IPv6 address.");
            if (result.Command == "serve" && result.AllowedClients.Count == 0)
            {
                if (result.Bind.Equals(IPAddress.IPv6Loopback)) result.AllowedClients.Add(IPAddress.IPv6Loopback);
                else throw new InvalidOperationException("For this public-address plaintext lab, specify each permitted source with --allow-client. This filter is not cryptographic authentication.");
            }
            if (result.AllowedClients.Count > 32) throw new ArgumentException("This preview accepts at most 32 configured source addresses.");
            return result;
        }
        private static IPAddress Address(string text)
        {
            if (!IPAddress.TryParse(text, out IPAddress address)) throw new ArgumentException("An explicit native IPv6 address is required.");
            DhmpRawIpv6Socket.ValidateAddress(address); return address;
        }
        public void ValidateLicense()
        {
            if (Development)
            {
#if DEBUG
                return;
#else
                throw new InvalidOperationException("--development is accepted only by a Debug host build. A Release host requires its DHMP application license.");
#endif
            }
            if (!Guid.TryParse(Environment.GetEnvironmentVariable("DHMP_APPLICATION_ID"), out Guid application))
                throw new InvalidOperationException("Set the host application's DHMP_APPLICATION_ID.");
            string publicKey = Environment.GetEnvironmentVariable("DHMP_LICENSE_PUBLIC_KEY");
            if (string.IsNullOrEmpty(publicKey)) throw new InvalidOperationException("Set the trusted DHMP_LICENSE_PUBLIC_KEY (base64 SubjectPublicKeyInfo).");
            DhmpUnityLicense.Validate(Environment.GetEnvironmentVariable("DHMP_LICENSE_KEY"), application, Convert.FromBase64String(publicKey));
        }
    }
}
