namespace DHMP.Protocol;

public sealed class DhmpFixedFrameReader
{
    private readonly byte[] _frame;
    private int _written;

    public DhmpFixedFrameReader(DhmpFixedContract contract)
    {
        Contract = contract;
        _frame = new byte[contract.PayloadSize];
    }

    public DhmpFixedContract Contract { get; }

    public async ValueTask PushAsync(
        ReadOnlyMemory<byte> transportBytes,
        Func<ReadOnlyMemory<byte>, ValueTask> onFrame)
    {
        while (!transportBytes.IsEmpty)
        {
            var copy = Math.Min(_frame.Length - _written, transportBytes.Length);
            transportBytes.Span[..copy].CopyTo(_frame.AsSpan(_written));
            _written += copy;
            transportBytes = transportBytes[copy..];

            if (_written == _frame.Length)
            {
                await onFrame(_frame.ToArray());
                _written = 0;
            }
        }
    }

    public void Complete()
    {
        if (_written != 0)
            throw new DhmpProtocolException($"Transport ended with {_written} bytes of an incomplete {_frame.Length}-byte DHMP payload.");
    }
}
