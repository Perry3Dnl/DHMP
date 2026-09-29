using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;

namespace DHMP.Server;

/// <summary>Preallocated receive storage with region-level ownership.</summary>
public sealed class DHMPReceiveRegion
{
    private int _state;
    internal DHMPReceiveRegion(int capacity) => Buffer = GC.AllocateUninitializedArray<byte>(capacity);
    internal byte[] Buffer { get; }
    public int Capacity => Buffer.Length;
    internal bool TryAcquireForReceive() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;
    internal void Publish() => Volatile.Write(ref _state, 2);
    internal bool TryBorrow() => Interlocked.CompareExchange(ref _state, 3, 2) == 2;
    internal void Release() => Volatile.Write(ref _state, 0);
}

/// <summary>
/// Stack-bound zero-copy typed view. Valid only for the duration of the callback receiving it.
/// </summary>
public readonly ref struct DHMPTypedSpan<T> where T : unmanaged
{
    private readonly ReadOnlySpan<T> _models;
    internal DHMPTypedSpan(ReadOnlySpan<byte> bytes) => _models = MemoryMarshal.Cast<byte,T>(bytes);
    public ReadOnlySpan<T> Models => _models;
    public int Count => _models.Length;
}
