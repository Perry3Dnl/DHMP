namespace Dhmp.Server;

/// <summary>
/// Fixed-contract framing only. Payload bytes remain opaque.
/// Complete runs are exposed as borrowed spans. A package crossing transport-input
/// boundaries is reconstructed in one of two preallocated carry slots.
/// </summary>
public sealed class DHMPFixedStreamProcessor
{
    private readonly int _packageSize;
    private readonly byte[][] _carry;
    private readonly bool[] _borrowed;
    private int _writeSlot;
    private int _carryLength;

    public DHMPFixedStreamProcessor(int packageSize)
    {
        if(packageSize<=0) throw new ArgumentOutOfRangeException(nameof(packageSize));
        _packageSize=packageSize;
        _carry=[new byte[packageSize],new byte[packageSize]];
    }

    public int PackageSize => _packageSize;

    /// <summary>
    /// Processes one transport chunk synchronously. Complete input runs are borrowed only
    /// during <paramref name="publishBorrowed"/>. Crossing packages use a preallocated
    /// carry slot which cannot be reused until its callback returns.
    /// </summary>
    public void Process(
        ReadOnlySpan<byte> input,
        Action<ReadOnlySpan<byte>> publishCrossBoundary,
        Action<ReadOnlySpan<byte>> publishBorrowed)
    {
        if(_carryLength!=0)
        {
            ref byte[] carry=ref _carry[_writeSlot];
            int take=Math.Min(_packageSize-_carryLength,input.Length);
            input[..take].CopyTo(carry.AsSpan(_carryLength));
            _carryLength+=take;
            input=input[take..];

            if(_carryLength==_packageSize)
            {
                BorrowCarry(_writeSlot,publishCrossBoundary);
                _carryLength=0;
                _writeSlot^=1;
            }
            else return;
        }

        int completeBytes=input.Length-(input.Length%_packageSize);
        if(completeBytes!=0)
        {
            // The transport input itself is borrowed. The callback must finish before
            // Process returns; retaining it belongs outside this DHMP boundary.
            publishBorrowed(input[..completeBytes]);
            input=input[completeBytes..];
        }

        if(!input.IsEmpty)
        {
            if(_borrowed[_writeSlot])
                throw new InvalidOperationException("No free DHMP carry slot.");
            input.CopyTo(_carry[_writeSlot]);
            _carryLength=input.Length;
        }
    }

    private void BorrowCarry(int slot,Action<ReadOnlySpan<byte>> publish)
    {
        if(_borrowed[slot]) throw new InvalidOperationException("DHMP carry slot is still borrowed.");
        _borrowed[slot]=true;
        try { publish(_carry[slot]); }
        finally { _borrowed[slot]=false; }
    }
}
