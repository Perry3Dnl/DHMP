using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DHMP.Server;

/// <summary>Validated native-layout view of complete records; performs no decoding or endian conversion.</summary>
public sealed class DhmpModelBoundary<T> where T : unmanaged
{
    private readonly int _size = Unsafe.SizeOf<T>();

    public DhmpModelBoundary(int packageSize)
    {
        if (packageSize != _size)
            throw new ArgumentException("The configured record size must match the unmanaged model size.", nameof(packageSize));
    }

    public ReadOnlySpan<T> Cast(ReadOnlySpan<byte> records)
    {
        if (records.Length % _size != 0)
            throw new ArgumentException("Typed input must contain complete records.", nameof(records));
        return MemoryMarshal.Cast<byte, T>(records);
    }
}
