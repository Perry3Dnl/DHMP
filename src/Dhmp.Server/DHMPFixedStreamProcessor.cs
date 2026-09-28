namespace Dhmp.Server;

/// <summary>Fixed-contract framing. Payload remains opaque; complete runs are borrowed synchronously.</summary>
public sealed class DHMPFixedStreamProcessor
{
    private readonly int _packageSize;
    private readonly int _packageMask;
    private readonly bool _powerOfTwo;
    private readonly byte[] _carry0;
    private readonly byte[] _carry1;
    private bool _borrowed0,_borrowed1;
    private int _writeSlot,_carryLength;

    public DHMPFixedStreamProcessor(int packageSize)
    {
        if(packageSize<=0) throw new ArgumentOutOfRangeException(nameof(packageSize));
        _packageSize=packageSize;
        _powerOfTwo=IsPowerOfTwo(packageSize);
        _packageMask=_powerOfTwo?packageSize-1:0;
        _carry0=new byte[packageSize];
        _carry1=new byte[packageSize];
    }
    public int PackageSize=>_packageSize;

    public void Process(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> publishCrossBoundary,Action<ReadOnlySpan<byte>> publishBorrowed)
    {
        if(_carryLength!=0 && !CompleteCarry(ref input,publishCrossBoundary))return;

        int length=input.Length;
        int remainder=_powerOfTwo ? length&_packageMask : length%_packageSize;
        int completeBytes=length-remainder;

        if(completeBytes!=0)
        {
            publishBorrowed(input.Slice(0,completeBytes));
            input=input.Slice(completeBytes);
        }
        if(remainder==0)return;
        StoreRemainder(input);
    }

    private bool CompleteCarry(ref ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> publish)
    {
        int needed=_packageSize-_carryLength;
        int take=input.Length<needed?input.Length:needed;
        byte[] carry=_writeSlot==0?_carry0:_carry1;
        input[..take].CopyTo(carry.AsSpan(_carryLength));
        _carryLength+=take;
        input=input.Slice(take);
        if(_carryLength!=_packageSize)return false;
        BorrowCarry(_writeSlot,publish);
        _carryLength=0;
        _writeSlot^=1;
        return true;
    }

    private void StoreRemainder(ReadOnlySpan<byte> input)
    {
        if((_writeSlot==0?_borrowed0:_borrowed1))throw new InvalidOperationException("No free DHMP carry slot.");
        byte[] target=_writeSlot==0?_carry0:_carry1;
        input.CopyTo(target);
        _carryLength=input.Length;
    }

    private void BorrowCarry(int slot,Action<ReadOnlySpan<byte>> publish)
    {
        if(slot==0)
        {
            if(_borrowed0)throw new InvalidOperationException("DHMP carry slot is still borrowed.");
            _borrowed0=true;
            try{publish(_carry0);}
            finally{_borrowed0=false;}
            return;
        }
        if(_borrowed1)throw new InvalidOperationException("DHMP carry slot is still borrowed.");
        _borrowed1=true;
        try{publish(_carry1);}
        finally{_borrowed1=false;}
    }

    private static bool IsPowerOfTwo(int value)=>(value&(value-1))==0;
}
