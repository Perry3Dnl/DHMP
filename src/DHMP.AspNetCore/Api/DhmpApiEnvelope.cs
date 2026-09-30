using System.Text.Json;

namespace DHMP.AspNetCore;

internal sealed class DhmpApiEnvelope
{
    public string Method { get; set; } = "";
    public string Target { get; set; } = "";
    public int Status { get; set; }
    public Dictionary<string, string[]> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; set; } = [];

    internal static async Task<byte[]> SerializeAsync(DhmpApiEnvelope envelope, int maximum, CancellationToken token)
    {
        using var buffer = new DhmpApiBuffer(maximum);
        await JsonSerializer.SerializeAsync(buffer, envelope, cancellationToken: token).ConfigureAwait(false);
        return buffer.ToArray();
    }
    internal static DhmpApiEnvelope Deserialize(byte[] bytes, int maximumBody)
    {
        var envelope = JsonSerializer.Deserialize<DhmpApiEnvelope>(bytes) ?? throw new InvalidDataException("Missing API envelope.");
        if (envelope.Body is null || envelope.Body.Length > maximumBody || envelope.Headers is null || envelope.Headers.Count > 100)
            throw new InvalidDataException("API envelope exceeds supported limits.");
        foreach (var header in envelope.Headers)
            if (header.Key.Length == 0 || header.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) ||
                header.Value is null || header.Value.Length > 100 || header.Value.Any(v => v is null || v.Any(c => c is '\r' or '\n' or '\0')))
                throw new InvalidDataException("Malformed API header.");
        return envelope;
    }
    internal static bool ExcludedHeader(string name) => name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) || name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Trailer", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase);
}

// Enforces buffering limits before growing. API profile supports bounded buffered bodies only.
internal sealed class DhmpApiBuffer(int maximum) : MemoryStream
{
    private void Check(int count)
    {
        if (count < 0 || Position > maximum - count) throw new InvalidDataException("DHMP API buffered body/message limit exceeded.");
    }
    public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
    public override void SetLength(long value)
    { if (value < 0 || value > maximum) throw new InvalidDataException("DHMP API buffer length limit exceeded."); base.SetLength(value); }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => Length + offset, _ => -1 };
        if (position < 0 || position > maximum) throw new InvalidDataException("DHMP API buffer position limit exceeded.");
        return base.Seek(offset, origin);
    }
}
