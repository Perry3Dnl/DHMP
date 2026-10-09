using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DHMP.Client;
using DHMP.Protocol;
using DHMP.Server;

var results =
    new List<object>();

foreach (DhmpRatePolicy ratePolicy in new[]
{
    DhmpRatePolicy.Unlimited,
    DhmpRatePolicy.RejectWindow,
    DhmpRatePolicy.SmoothPacing
})
foreach (int bytes in new[]
{
    16,
    1408,
    65520
})
{
    var sender =
        new CopySender(
            bytes);

    var wire =
        new DhmpWireContract(
            bytes);

    var policy =
        new DhmpSendPolicy(
            long.MaxValue,
            65520,
            ratePolicy);

    var current =
        new DhmpClient(
            sender,
            wire,
            policy);

    var legacy =
        new DhmpLegacyClient(
            sender,
            wire,
            policy);

    byte[] record =
        new byte[bytes];

    record.AsSpan()
        .Fill(0x5a);

    Action[] actions =
    {
        () =>
            legacy.SendAsync(
                    record)
                .GetAwaiter()
                .GetResult(),
        () =>
            current.SendAsync(
                    record)
                .GetAwaiter()
                .GetResult()
    };

    foreach (Action action in actions)
    {
        long start =
            Stopwatch.GetTimestamp();

        do
        {
            for (int i = 0;
                 i < 10_000;
                 i++)
            {
                action();
            }
        }
        while (Stopwatch.GetElapsedTime(start)
                   .TotalSeconds <
               0.2);
    }

    var samples =
        new[]
        {
            new List<double>(),
            new List<double>()
        };

    for (int repetition = 0;
         repetition < 5;
         repetition++)
    for (int step = 0;
         step < 2;
         step++)
    {
        int index =
            repetition % 2 == 0
                ? step
                : 1 - step;

        long count = 0;
        long start =
            Stopwatch.GetTimestamp();

        double seconds;

        do
        {
            for (int i = 0;
                 i < 10_000;
                 i++)
            {
                actions[index]();
            }

            count += 10_000;

            seconds =
                Stopwatch.GetElapsedTime(start)
                    .TotalSeconds;
        }
        while (seconds < 0.1);

        samples[index]
            .Add(
                seconds *
                1e9 /
                count);
    }

    double before =
        samples[0]
            .Order()
            .ElementAt(2);

    double after =
        samples[1]
            .Order()
            .ElementAt(2);

    results.Add(
        new
        {
            ratePolicy =
                ratePolicy.ToString(),
            bytes,
            legacyMedianNs =
                before,
            productionMedianNs =
                after,
            timeReductionPercent =
                100 *
                (before - after) /
                before,
            legacySamplesNs =
                samples[0],
            productionSamplesNs =
                samples[1]
        });

    GC.KeepAlive(
        sender.Observed);
}

Console.WriteLine(
    "SEND_PATH_RESULTS=" +
    JsonSerializer.Serialize(
        new
        {
            runtime =
                RuntimeInformation.FrameworkDescription,
            architecture =
                RuntimeInformation.ProcessArchitecture.ToString(),
            scope =
                "Local hot-buffer sender, no socket; historical async DhmpClient baseline versus the shipping DhmpClient path. All three rate policies use the same sender and five interleaved >=100 ms samples. SmoothPacing uses long.MaxValue Pmax so the benchmark measures its no-delay fast path rather than intentional waiting.",
            results
        }));

sealed class CopySender :
    IDhmpPacketSender
{
    private readonly byte[] _slot;
    private readonly DhmpServer _server;
    private readonly Action<ReadOnlySpan<byte>> _publish;

    public long Observed
    {
        get;
        private set;
    }

    public int MaximumPayloadBytes =>
        65520;

    public CopySender(
        int bytes)
    {
        _slot =
            new byte[bytes];

        _server =
            new DhmpServer(
                new DhmpWireContract(bytes),
                new DhmpReceivePolicy(
                    DhmpProcessingMode.UnsafeLatest,
                    65520));

        _publish =
            span =>
                Observed += span[0];
    }

    public ValueTask SendPacketAsync(
        ReadOnlyMemory<byte> record,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        if (record.Length !=
            _slot.Length)
            throw new DhmpProtocolException(
                "Wrong record length.");

        record.Span.CopyTo(
            _slot);

        _server.ProcessNegotiatedRecord(
            _slot,
            _publish);

        return ValueTask.CompletedTask;
    }
}
