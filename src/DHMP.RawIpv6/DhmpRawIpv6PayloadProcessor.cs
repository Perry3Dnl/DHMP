using System.Security.Cryptography;
using DHMP.Protocol;
using DHMP.Server;

namespace DHMP.RawIpv6;

// Shared ownership boundary for single-peer receive and leased multi-peer routing.
internal static class DhmpRawIpv6PayloadProcessor
{
    internal static bool TryProcess(DhmpServer server, IDhmpPacketDecoder? decoder,
        ReadOnlySpan<byte> networkPayload, Span<byte> plaintextScratch,
        Action<ReadOnlySpan<byte>> publishBatch, out bool protectionRejected,
        out bool slotSizeIgnored)
    {
        protectionRejected = false;
        slotSizeIgnored = false;

        int recordSize =
            server.WireContract.RecordSize;

        // Canonical plaintext path: the handshake already fixed the slot size.
        // A mismatched payload is simply ignored. No decoder/max-payload math,
        // framing calculation or cleanup path is entered.
        if (decoder is null)
        {
            if (networkPayload.Length != recordSize)
            {
                slotSizeIgnored = true;
                return false;
            }

            server.ProcessNegotiatedRecord(
                networkPayload,
                publishBatch);
            return true;
        }

        int maximumPlaintext =
            server.ReceivePolicy.MaximumPayloadBytes;

        if (networkPayload.IsEmpty ||
            networkPayload.Length >
                checked(maximumPlaintext + decoder.OverheadBytes))
            return false;

        if (plaintextScratch.Length < maximumPlaintext)
            throw new ArgumentException(
                "Plaintext scratch buffer is smaller than the receive policy.",
                nameof(plaintextScratch));

        try
        {
            if (!decoder.TryDecode(
                    networkPayload,
                    plaintextScratch,
                    out int length) ||
                length <= 0 ||
                length > maximumPlaintext ||
                length > plaintextScratch.Length)
            {
                protectionRejected = true;
                return false;
            }

            if (length != recordSize)
            {
                slotSizeIgnored = true;
                return false;
            }

            server.ProcessNegotiatedRecord(
                plaintextScratch[..length],
                publishBatch);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(
                plaintextScratch);
        }
    }
}
