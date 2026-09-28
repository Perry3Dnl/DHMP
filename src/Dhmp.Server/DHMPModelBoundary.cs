using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Dhmp.Server;

/// <summary>DHMP .NET scope boundary: fixed package bytes -> zero-copy typed data view.</summary>
public static class DHMPModelBoundary
{
    public static void Validate<T>(int packageSize) where T : unmanaged
    {
        if(Unsafe.SizeOf<T>() != packageSize)
            throw new InvalidOperationException($"DHMP contract size {packageSize} does not match {typeof(T).Name} size {Unsafe.SizeOf<T>()}.");
    }

    public static void Consume<T>(ReadOnlySpan<byte> completePackages, int packageSize, Action<ReadOnlySpan<T>> consumer) where T : unmanaged
    {
        Validate<T>(packageSize);
        if(completePackages.Length % packageSize != 0) throw new ArgumentException("Span must contain complete DHMP packages.", nameof(completePackages));
        consumer(MemoryMarshal.Cast<byte,T>(completePackages));
    }
}
