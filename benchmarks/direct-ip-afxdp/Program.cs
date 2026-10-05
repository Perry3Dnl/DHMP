using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DHMP.AfXdp;
using DHMP.Protocol;
using DHMP.RawIpv6;

const int RecordSize = 16;
const int DefaultPayloadBytes = 1200;
const long DefaultPackets = 1_000_000;

string interfaceName = args.Length > 0 ? args[0] : "dhmpxdp0";
IPAddress localAddress =
    IPAddress.Parse(args.Length > 1 ? args[1] : "fd42:6468:6d70::1");
IPAddress peerAddress =
    IPAddress.Parse(args.Length > 2 ? args[2] : "fd42:6468:6d70::2");
int payloadBytes =
    args.Length > 3 ? int.Parse(args[3]) : DefaultPayloadBytes;
long packets =
    args.Length > 4 ? long.Parse(args[4]) : DefaultPackets;

if (payloadBytes <= 0 ||
    payloadBytes > 1408 ||
    payloadBytes % RecordSize != 0)
    throw new ArgumentOutOfRangeException(nameof(payloadBytes));

if (packets <= 0 || packets > 100_000_000)
    throw new ArgumentOutOfRangeException(nameof(packets));

Console.WriteLine(
    $"CONTRACT=DHMP_AFXDP_COMPARE_V1 interface={interfaceName} " +
    $"payload={payloadBytes} packets={packets} record={RecordSize}");

var probe =
    DhmpAfXdpHostProbe.Probe(
        interfaceName,
        queueId: 0,
        preferZeroCopy: true);

Console.WriteLine(
    "AFXDP_PROBE_JSON " +
    JsonSerializer.Serialize(probe));

RawResult? raw = null;

try
{
    raw = await RunRawIpv6Async(
        localAddress,
        peerAddress,
        payloadBytes,
        packets);

    Console.WriteLine(
        "RAW_RESULT_JSON " +
        JsonSerializer.Serialize(raw));
}
catch (Exception exception)
{
    Console.WriteLine(
        "RAW_SKIPPED_JSON " +
        JsonSerializer.Serialize(new
        {
            supported = false,
            error = exception.GetBaseException().Message
        }));
}

DhmpAfXdpBenchmarkResult afxdp =
    DhmpAfXdpBenchmark.RunTransmit(
        interfaceName,
        payloadBytes,
        packets,
        queueId: 0,
        preferZeroCopy: true);

Console.WriteLine(
    "AFXDP_RESULT_JSON " +
    JsonSerializer.Serialize(afxdp));

if (raw is not null && afxdp.Supported)
{
    Console.WriteLine(
        "COMPARISON_JSON " +
        JsonSerializer.Serialize(new
        {
            contract = "directional-send-path-microbenchmark-not-end-to-end",
            payloadBytes,
            packets,
            rawIpv6PacketsPerSecond =
                raw.PacketsPerSecond,
            afXdpPacketsPerSecond =
                afxdp.PacketsPerSecond,
            packetRateSpeedup =
                afxdp.PacketsPerSecond /
                raw.PacketsPerSecond,
            rawIpv6PayloadGBps =
                raw.PayloadGigabytesPerSecond,
            afXdpPayloadGBps =
                afxdp.PayloadGigabytesPerSecond,
            payloadRateSpeedup =
                afxdp.PayloadGigabytesPerSecond /
                raw.PayloadGigabytesPerSecond,
            afXdpMode = afxdp.Mode.ToString()
        }));
}
else
{
    Console.WriteLine(
        "COMPARISON_SKIPPED " +
        "A real AF_XDP result and raw IPv6 result are both required.");
}

static async Task<RawResult> RunRawIpv6Async(
    IPAddress localAddress,
    IPAddress peerAddress,
    int payloadBytes,
    long packets)
{
    using Socket receiver =
        DhmpLinuxRawIpv6Socket.Open(
            DhmpProtocol.ExperimentalIpv6DataNextHeader);

    using Socket sender =
        DhmpLinuxRawIpv6Socket.Open(
            DhmpProtocol.ExperimentalIpv6DataNextHeader);

    receiver.Bind(
        new IPEndPoint(peerAddress, 0));

    sender.Bind(
        new IPEndPoint(localAddress, 0));

    receiver.ReceiveBufferSize =
        16 * 1024 * 1024;

    sender.SendBufferSize =
        16 * 1024 * 1024;

    receiver.ReceiveTimeout = 250;

    byte[] payload =
        GC.AllocateUninitializedArray<byte>(
            payloadBytes);

    byte[] receive =
        GC.AllocateUninitializedArray<byte>(
            payloadBytes + 256);

    var contract =
        new DhmpWireContract(RecordSize);

    contract.ValidatePacket(
        payload.Length,
        payloadBytes);

    long received = 0;
    long senderFinished = 0;
    using var ready =
        new ManualResetEventSlim();

    Task drain =
        Task.Run(
            () =>
            {
                ready.Set();

                while (true)
                {
                    try
                    {
                        int count =
                            receiver.Receive(receive);

                        if (count <= 0)
                            continue;

                        Interlocked.Increment(
                            ref received);
                    }
                    catch (SocketException error)
                        when (error.SocketErrorCode ==
                            SocketError.TimedOut)
                    {
                        if (Volatile.Read(
                                ref senderFinished) != 0)
                            break;
                    }
                }
            });

    ready.Wait();

    EndPoint target =
        new IPEndPoint(
            peerAddress,
            0);

    long started =
        Stopwatch.GetTimestamp();

    for (long packet = 0;
         packet < packets;
         packet++)
    {
        BitConverter.TryWriteBytes(
            payload.AsSpan(0, 8),
            packet);

        sender.SendTo(
            payload,
            SocketFlags.None,
            target);
    }

    long finished =
        Stopwatch.GetTimestamp();

    Volatile.Write(
        ref senderFinished,
        1);

    await drain;

    double seconds =
        (finished - started) /
        (double)Stopwatch.Frequency;

    return new RawResult(
        packets,
        Volatile.Read(ref received),
        seconds,
        packets / seconds,
        packets *
            (double)payloadBytes /
            seconds /
            1e9);
}

internal sealed record RawResult(
    long PacketsSubmitted,
    long PacketsObservedByDrain,
    double Seconds,
    double PacketsPerSecond,
    double PayloadGigabytesPerSecond);
