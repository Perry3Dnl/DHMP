using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using DHMP.Protocol;

namespace DHMP.RawIpv6;

/// <summary>
/// Pre-handshake exact-echo Poke over the native DHMP control protocol.
/// The peer must already be explicitly configured and running a DHMP accept path.
/// </summary>
public static class DhmpRawIpv6Poke
{
    public static Task<TimeSpan> ProbeAsync(
        DhmpRawIpv6Options options,
        int packetBytes = DhmpPokeCodec.MinimumPacketSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureExperimentalProtocolNumbersEnabled();

        return ProbeCoreAsync(
            options.HandshakeTimeout,
            packetBytes,
            () => new DhmpRawIpv6ControlChannel(options),
            TimeProvider.System,
            cancellationToken);
    }

    internal static Task<TimeSpan> ProbeCoreAsync(
        TimeSpan timeout,
        int packetBytes,
        Func<IDhmpControlPacketChannel> channelFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        if (packetBytes < DhmpPokeCodec.MinimumPacketSize ||
            packetBytes > DhmpPokeCodec.MaximumPacketSize)
            throw new ArgumentOutOfRangeException(nameof(packetBytes));

        ArgumentNullException.ThrowIfNull(channelFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        return DhmpHandshakeDeadline.RunAsync(
            timeout,
            timeProvider,
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

                    using IDhmpControlPacketChannel channel =
                        channelFactory();

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
                            "DHMP Poke did not receive an exact echo from the configured peer.");
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

        do
        {
            RandomNumberGenerator.Fill(
                bytes);
        }
        while (System.Buffers.Binary.BinaryPrimitives
                   .ReadUInt64BigEndian(bytes) == 0);

        return System.Buffers.Binary.BinaryPrimitives
            .ReadUInt64BigEndian(bytes);
    }
}
