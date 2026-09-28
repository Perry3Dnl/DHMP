namespace Dhmp.Server;

/// <summary>
/// Fixed-contract framing only. Payload bytes remain opaque.
/// Complete runs are exposed as spans; only a package crossing input boundaries uses carry storage.
/// </summary>
public sealed class DHMPFixedStreamProcessor
{
    private readonly int _packageSize;
    private readonly byte[] _carry;
    private int _carryLength;
    public DHMPFixedStreamProcessor(int packageSize)
    {
        if(packageSize<=0) throw new ArgumentOutOfRangeException(nameof(packageSize));
        _packageSize=packageSize;_carry=new byte[packageSize];
    }
    public int PackageSize => _packageSize;

    public void Process(ReadOnlySpan<byte> input, Action<ReadOnlyMemory<byte>> publishOwnedCrossBoundary, Action<ReadOnlySpan<byte>> publishBorrowed)
    {
        // Crossing-package bytes need stable storage. This callback receives an owned copy;
        // complete runs remain borrowed and zero-copy.
        if(_carryLength!=0)
        {
            int take=Math.Min(_packageSize-_carryLength,input.Length);
            input[..take].CopyTo(_carry.AsSpan(_carryLength));_carryLength+=take;input=input[take..];
            if(_carryLength==_packageSize)
            {
                publishOwnedCrossBoundary(_carry.ToArray());
                _carryLength=0;
            }
            else return;
        }

        int completeBytes=input.Length-(input.Length%_packageSize);
        if(completeBytes!=0){publishBorrowed(input[..completeBytes]);input=input[completeBytes..];}
        if(!input.IsEmpty){input.CopyTo(_carry);_carryLength=input.Length;}
    }
}
