using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using DHMP.Protocol;

namespace DHMP.Connector;

internal static class DhmpUdpPoke
{
    public static Task<TimeSpan> ProbeAsync(
        DhmpUdpRuntime runtime,
        System.Net.IPAddress remoteAddress,
        int packetBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(remoteAddress);

        if (packetBytes < DhmpPokeCodec.MinimumPacketSize ||
            packetBytes > DhmpPokeCodec.MaximumPacketSize)
            throw new ArgumentOutOfRangeException(nameof(packetBytes));

        return RunWithDeadlineAsync(
            timeout,
            cancellationToken,
            async token =>
            {
                ulong pokeToken =
                    CreateToken();

                byte[] outbound =
                    ArrayPool<byte>.Shared.Rent(
                        packetBytes);

                byte[] inbound =
                    ArrayPool<byte>.Shared.Rent(
                        packetBytes);

                try
                {
                    DhmpPokeCodec.Encode(
                        pokeToken,
                        outbound.AsSpan(
                            0,
                            packetBytes));

                    using DhmpUdpControlChannel channel =
                        runtime.CreateControlChannel(
                            remoteAddress);

                    long started =
                        Stopwatch.GetTimestamp();

                    await channel.SendPacketAsync(
                        outbound.AsMemory(
                            0,
                            packetBytes),
                        token).ConfigureAwait(false);

                    int received =
                        await channel.ReceivePacketAsync(
                            inbound.AsMemory(
                                0,
                                packetBytes),
                            token).ConfigureAwait(false);

                    long elapsed =
                        Stopwatch.GetTimestamp() -
                        started;

                    if (received != packetBytes ||
                        !DhmpPokeCodec.TryReadToken(
                            inbound.AsSpan(
                                0,
                                received),
                            out ulong echoedToken) ||
                        echoedToken != pokeToken ||
                        !inbound.AsSpan(
                                0,
                                received)
                            .SequenceEqual(
                                outbound.AsSpan(
                                    0,
                                    packetBytes)))
                    {
                        throw new DhmpProtocolException(
                            "DHMP Poke did not receive an exact UDP echo from the configured peer.");
                    }

                    return TimeSpan.FromSeconds(
                        (double)elapsed /
                        Stopwatch.Frequency);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(
                        outbound,
                        clearArray: true);

                    ArrayPool<byte>.Shared.Return(
                        inbound,
                        clearArray: true);
                }
            });
    }

    private static ulong CreateToken()
    {
        Span<byte> bytes =
            stackalloc byte[sizeof(ulong)];

        ulong token;

        do
        {
            RandomNumberGenerator.Fill(
                bytes);

            token =
                BinaryPrimitives.ReadUInt64BigEndian(
                    bytes);
        }
        while (token == 0);

        return token;
    }

    private static async Task<T> RunWithDeadlineAsync<T>(
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> action)
    {
        using var linked =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        linked.CancelAfter(
            timeout);

        try
        {
            return await action(
                linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "DHMP UDP Poke timed out.");
        }
    }
}
