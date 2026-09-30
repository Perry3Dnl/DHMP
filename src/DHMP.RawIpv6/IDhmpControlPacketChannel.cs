namespace DHMP.RawIpv6;

// Packet boundary shared by the real raw socket and privilege-free handshake tests.
internal interface IDhmpControlPacketChannel : IDisposable
{
    ValueTask SendPacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
    ValueTask<int> ReceivePacketAsync(Memory<byte> destination, CancellationToken cancellationToken);
}
