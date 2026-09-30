using System.Security.Cryptography;
using DHMP.Protocol;
using DHMP.Server;

namespace DHMP.RawIpv6;

// Shared ownership boundary for single-peer receive and leased multi-peer routing.
internal static class DhmpRawIpv6PayloadProcessor
{
    internal static bool TryProcess(DhmpServer server, IDhmpPacketDecoder? decoder,
        ReadOnlySpan<byte> networkPayload, Span<byte> plaintextScratch,
        Action<ReadOnlySpan<byte>> publishBatch, out bool protectionRejected)
    {
        protectionRejected = false;
        int maximumPlaintext = server.ReceivePolicy.MaximumPayloadBytes;
        if (networkPayload.IsEmpty || networkPayload.Length > checked(maximumPlaintext + (decoder?.OverheadBytes ?? 0)))
            return false;
        if (decoder is not null && plaintextScratch.Length < maximumPlaintext)
            throw new ArgumentException("Plaintext scratch buffer is smaller than the receive policy.", nameof(plaintextScratch));

        try
        {
            ReadOnlySpan<byte> payload = networkPayload;
            if (decoder is not null)
            {
                if (!decoder.TryDecode(networkPayload, plaintextScratch, out int length) ||
                    length <= 0 || length > maximumPlaintext || length > plaintextScratch.Length)
                {
                    protectionRejected = true;
                    return false;
                }
                payload = plaintextScratch[..length];
            }
            if (payload.Length <= 0 || payload.Length > maximumPlaintext ||
                payload.Length % server.WireContract.RecordSize != 0)
                return false;
            server.ProcessPacket(payload, publishBatch);
            return true;
        }
        finally
        {
            // Includes malformed plaintext, decoder exceptions and writes beyond the reported length.
            if (decoder is not null)
                CryptographicOperations.ZeroMemory(plaintextScratch);
        }
    }
}
