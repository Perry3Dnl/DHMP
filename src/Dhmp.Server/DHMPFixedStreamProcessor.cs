namespace Dhmp.Server;

/// <summary>Fixed-contract framing. Payload remains opaque; complete runs are borrowed synchronously.</summary>
public sealed class DHMPFixedStreamProcessor
{
    private readonly int _packageSize;
    private readonly int _packageMask;
    private readonly bool _powerOfTwo;
    private readonly byte[][] _carry;
    private readonly bool[] _borrowed=new bool[2];
    private int _writeSlot,_carryLength;

    public DHMPFixedStreamProcessor(int packageSize)
    {
        if(packageSize<=0) throw new ArgumentOutOfRangeException(nameof(packageSize));
        _packageSize=packageSize;
        _powerOfTwo=IsPowerOfTwo(packageSize);
        _packageMask=_powerOfTwo?packageSize-1:0;
        _carry=[new byte[packageSize],new byte[packageSize]];
    }
    public int PackageSize=>_packageSize;

    public void Process(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> publishCrossBoundary,Action<ReadOnlySpan<byte>> publishBorrowed)
    {
        if(_carryLength!=0)
        {
            int take=Math.Min(_packageSize-_carryLength,input.Length);
            input[..take].CopyTo(_carry[_writeSlot].AsSpan(_carryLength));
            _carryLength+=take; input=input[take..];
            if(_carryLength!=_packageSize)return;
            BorrowCarry(_writeSlot,publishCrossBoundary);
            _carryLength=0; _writeSlot^=1;
        }

        // Division/remainder used to be on every transport chunk. For the overwhelmingly
        // common power-of-two contracts (16/32/64...) this reduces framing to one mask.
        int remainder=_powerOfTwo
            ? input.Length&_packageMask
            : input.Length%_packageSize;
        int completeBytes=input.Length-remainder;

        if(completeBytes!=0)
        {
            publishBorrowed(input[..completeBytes]);
            input=input[completeBytes..];
        }
        if(input.IsEmpty)return;
        if(_borrowed[_writeSlot])throw new InvalidOperationException("No free DHMP carry slot.");
        input.CopyTo(_carry[_writeSlot]);
        _carryLength=input.Length;
    }

    private void BorrowCarry(int slot,Action<ReadOnlySpan<byte>> publish)
    {
        if(_borrowed[slot])throw new InvalidOperationException("DHMP carry slot is still borrowed.");
        _borrowed[slot]=true;
        try{publish(_carry[slot]);}
        finally{_borrowed[slot]=false;}
    }

    private static bool IsPowerOfTwo(int value)=>(value&(value-1))==0;
}
