using DHMP.Protocol;
using Xunit;

namespace DHMP.Security.Tests;

public sealed class DhmpProtectedDynamicPayloadTests
{
    private static readonly Guid SessionId =
        Guid.Parse("aa112233-4455-6677-8899-aabbccddeeff");

    [Fact]
    public async Task ProtectedSender_TracksInnerLiveCeilingMinusEnvelope()
    {
        using var key =
            new DhmpPreSharedKey(
                7,
                new byte[
                    DhmpPreSharedKey
                        .KeySizeBytes]);

        using var session =
            new DhmpPskChaCha20Poly1305Session(
                key,
                SessionId,
                DhmpSecurityRole.Initiator);

        var inner =
            new DynamicSender(
                maximumPayloadBytes: 1500,
                currentMaximumPayloadBytes: 1240);

        await using var sender =
            new DhmpProtectedPacketSender(
                inner,
                session);

        Assert.Equal(
            1500 -
                DhmpPskChaCha20Poly1305Session
                    .Overhead,
            sender.MaximumPayloadBytes);

        Assert.Equal(
            1240 -
                DhmpPskChaCha20Poly1305Session
                    .Overhead,
            sender.CurrentMaximumPayloadBytes);

        inner.SetCurrentMaximumPayloadBytes(
            1460);

        Assert.Equal(
            1460 -
                DhmpPskChaCha20Poly1305Session
                    .Overhead,
            sender.CurrentMaximumPayloadBytes);

        inner.SetCurrentMaximumPayloadBytes(
            40);

        Assert.Equal(
            16,
            sender.CurrentMaximumPayloadBytes);

        await Assert.ThrowsAsync<DhmpProtocolException>(() =>
            sender.SendPacketAsync(
                new byte[32],
                TestContext.Current.CancellationToken)
            .AsTask());

        Assert.Equal(0, inner.Calls);
    }

    private sealed class DynamicSender :
        IDhmpDynamicPacketSender
    {
        private int _currentMaximumPayloadBytes;

        public DynamicSender(
            int maximumPayloadBytes,
            int currentMaximumPayloadBytes)
        {
            MaximumPayloadBytes =
                maximumPayloadBytes;

            SetCurrentMaximumPayloadBytes(
                currentMaximumPayloadBytes);
        }

        public int MaximumPayloadBytes { get; }

        public int CurrentMaximumPayloadBytes =>
            Volatile.Read(
                ref _currentMaximumPayloadBytes);

        public int Calls { get; private set; }

        public void SetCurrentMaximumPayloadBytes(
            int value)
        {
            if (value < 0 ||
                value > MaximumPayloadBytes)
                throw new ArgumentOutOfRangeException(
                    nameof(value));

            Volatile.Write(
                ref _currentMaximumPayloadBytes,
                value);
        }

        public ValueTask SendPacketAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            Calls++;

            return ValueTask.CompletedTask;
        }
    }
}
