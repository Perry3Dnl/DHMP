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
        _powerOfTwo=(packageSize&(packageSize-1))==0;
        _packageMask=_powerOfTwo?packageSize-1:0;
        _carry0=new byte[packageSize];
        _carry1=new byte[packageSize];
    }
    public int PackageSize=>_packageSize;

    public void Process(ReadOnlySpan<byte> input,Action<ReadOnlySpan<byte>> publishCrossBoundary,Action<ReadOnlySpan<byte>> publishBorrowed)
    {
        if(_carryLength!=0)
        {
            int take=Math.Min(_packageSize-_carryLength,input.Length);
            bool useSlot0=_writeSlot==0;
            byte[] carry=useSlot0?_carry0:_carry1;
            input[..take].CopyTo(carry.AsSpan(_carryLength));
            _carryLength+=take; input=input[take..];
            if(_carryLength!=_packageSize)return;
            ref bool borrowed=ref (useSlot0?ref _borrowed0:ref _borrowed1);
            if(borrowed)throw new InvalidOperationException("DHMP carry slot is still borrowed.");
            borrowed=true;
            try{publishCrossBoundary(carry);}
            finally{borrowed=false;}
            _carryLength=0; _writeSlot^=1;
        }

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
        if((_writeSlot==0?_borrowed0:_borrowed1))throw new InvalidOperationException("No free DHMP carry slot.");
        byte[] target=_writeSlot==0?_carry0:_carry1;
        input.CopyTo(target);
        _carryLength=input.Length;
    }
}
