using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DHMP.Protocol;

const int Msg = 32;
const int Header = 40;
const int NextHeader = DhmpProtocol.ExperimentalIpv6NextHeader;

int messages = args.Length > 0 ? int.Parse(args[0]) : 5_000_000;
int batch = args.Length > 1 ? int.Parse(args[1]) : 44;
int payloadBytes = checked(batch * Msg);

if (payloadBytes + Header > 1500)
    throw new ArgumentOutOfRangeException(nameof(batch), "IPv6 packet exceeds 1500-byte test MTU.");

var contract = new DhmpFixedContract(Msg, int.MaxValue, payloadBytes);
var processor = new DhmpPacketProcessor(contract, DhmpProcessingMode.Latest);

using var rx = new Socket(AddressFamily.InterNetworkV6, SocketType.Raw, (ProtocolType)NextHeader);
using var tx = new Socket(AddressFamily.InterNetworkV6, SocketType.Raw, (ProtocolType)NextHeader);
var ep = new IPEndPoint(IPAddress.IPv6Loopback, 0);

rx.Bind(ep);
rx.ReceiveBufferSize = 16 * 1024 * 1024;
tx.SendBufferSize = 16 * 1024 * 1024;
rx.ReceiveTimeout = 2000;

byte[] send = new byte[payloadBytes];
byte[] recv = new byte[payloadBytes + Header + 256];

long offered = 0;
long received = 0;
long invalidPackets = 0;
long guard = 0;

using var gate = new ManualResetEventSlim(false);

var receiver = Task.Run(() =>
{
    gate.Wait();

    try
    {
        while (Volatile.Read(ref offered) < messages || received < messages)
        {
            int n;
            try
            {
                n = rx.Receive(recv);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                if (Volatile.Read(ref offered) >= messages)
                    break;

                continue;
            }

            ReadOnlySpan<byte> payload = recv.AsSpan(0, n);

            // Unix raw IPv6 behavior can expose either protocol payload bytes or an IPv6 header.
            // If a base IPv6 header is present, validate the fields the experiment owns before slicing it.
            if (n >= Header && (payload[0] >> 4) == 6)
            {
                int declaredPayloadLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));

                if (payload[6] != NextHeader ||
                    declaredPayloadLength != n - Header)
                {
                    invalidPackets++;
                    continue;
                }

                payload = payload.Slice(Header, declaredPayloadLength);
            }

            int complete = payload.Length / Msg;

            try
            {
                processor.Process(payload, latest =>
                {
                    guard += BinaryPrimitives.ReadInt32LittleEndian(latest.Slice(0, 4));
                });
            }
            catch (DhmpProtocolException)
            {
                invalidPackets++;
                continue;
            }

            received += complete;
        }
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"RX_ERROR {e.GetType().Name}: {e.Message}");
        throw;
    }
});

gate.Set();

var sw = Stopwatch.StartNew();

for (int seq = 0; seq < messages;)
{
    int n = Math.Min(batch, messages - seq);
    var payload = send.AsSpan(0, n * Msg);

    for (int j = 0; j < n; j++)
        BinaryPrimitives.WriteInt32LittleEndian(payload.Slice(j * Msg, 4), seq + j);

    contract.ValidatePacket(payload.Length);
    tx.SendTo(payload, SocketFlags.None, ep);

    seq += n;
    Volatile.Write(ref offered, seq);
}

receiver.Wait();
sw.Stop();

double sec = sw.Elapsed.TotalSeconds;
double missingPct = 100.0 * Math.Max(0, messages - received) / messages;

Console.WriteLine(
    $"RAW_IPV6_CURRENT_V1 batch={batch} offered={messages} received={received} " +
    $"invalid_packets={invalidPackets} missing_pct={missingPct:F6} wall_s={sec:F6} " +
    $"offered_mps={messages / sec / 1e6:F3} received_mps={received / sec / 1e6:F3} " +
    $"received_GBps={received * Msg / sec / 1e9:F3} " +
    $"ns_received={(received > 0 ? sec * 1e9 / received : 0):F3} guard={guard}");
