namespace Dhmp.Server;

/// <summary>
/// Frames opaque fixed-size packages, copying only fragments into one reusable carry buffer.
/// Complete runs are borrowed directly from the input and published synchronously.
/// </summary>
/// <remarks>
/// One instance belongs to one ordered incoming stream. Calls must be serialized and
/// callbacks must not re-enter this instance. These preconditions are not checked at runtime.
/// Both callbacks must be non-null and consume their spans before returning. To queue work,
/// the receiving layer must copy the data or acquire ownership of its backing storage.
/// Callback exceptions propagate: abort the stream and discard this processor after a failure.
/// The processor does not retry publication or report partially consumed input.
/// </remarks>
public sealed class DHMPFixedStreamProcessor
{
    private readonly int _packageSize;
    private readonly int _packageMask;
    private readonly bool _powerOfTwo;
    private readonly byte[] _carry;
    private int _carryLength;

    public DHMPFixedStreamProcessor(int packageSize)
    {
        if (packageSize <= 0) throw new ArgumentOutOfRangeException(nameof(packageSize));
        _packageSize = packageSize;
        _packageMask = packageSize - 1;
        _powerOfTwo = (packageSize & _packageMask) == 0;
        _carry = new byte[packageSize];
    }

    public int PackageSize => _packageSize;

    /// <summary>
    /// Publishes a reconstructed package through <paramref name="publishCrossBoundary"/>
    /// and complete input runs through <paramref name="publishBorrowed"/>.
    /// Retains any incomplete trailing package for the next call; never publishes empty spans.
    /// </summary>
    public void Process(ReadOnlySpan<byte> input,
        Action<ReadOnlySpan<byte>> publishCrossBoundary,
        Action<ReadOnlySpan<byte>> publishBorrowed)
    {
        if (_carryLength != 0)
        {
            int take = Math.Min(_packageSize - _carryLength, input.Length);
            input[..take].CopyTo(_carry.AsSpan(_carryLength));
            _carryLength += take;
            input = input[take..];
            if (_carryLength != _packageSize) return;
            publishCrossBoundary(_carry);
            _carryLength = 0;
        }

        int remainder = _powerOfTwo
            ? input.Length & _packageMask
            : input.Length % _packageSize;
        int completeBytes = input.Length - remainder;

        if (completeBytes != 0)
        {
            publishBorrowed(input[..completeBytes]);
            input = input[completeBytes..];
        }
        if (input.IsEmpty) return;
        input.CopyTo(_carry);
        _carryLength = input.Length;
    }
}
