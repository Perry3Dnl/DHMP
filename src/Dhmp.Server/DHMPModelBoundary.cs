using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace Dhmp.Server;
public sealed class DHMPModelBoundary<T> where T:unmanaged {
 public DHMPModelBoundary(int packageSize){int s=Unsafe.SizeOf<T>();if(s!=packageSize)throw new InvalidOperationException($"DHMP contract size {packageSize} does not match {typeof(T).Name} size {s}.");PackageSize=packageSize;}
 public int PackageSize{get;}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public ReadOnlySpan<T> Cast(ReadOnlySpan<byte> packages){if(packages.Length%PackageSize!=0)throw new ArgumentException("Span must contain complete DHMP packages.",nameof(packages));return MemoryMarshal.Cast<byte,T>(packages);}
 [MethodImpl(MethodImplOptions.AggressiveInlining)] public void Consume(ReadOnlySpan<byte> packages,Action<ReadOnlySpan<T>> consumer)=>consumer(Cast(packages));
}
public static class DHMPModelBoundary {
 public static void Validate<T>(int packageSize) where T:unmanaged {int s=Unsafe.SizeOf<T>();if(s!=packageSize)throw new InvalidOperationException($"DHMP contract size {packageSize} does not match {typeof(T).Name} size {s}.");}
 public static void Consume<T>(ReadOnlySpan<byte> packages,int packageSize,Action<ReadOnlySpan<T>> consumer) where T:unmanaged {Validate<T>(packageSize);if(packages.Length%packageSize!=0)throw new ArgumentException("Span must contain complete DHMP packages.",nameof(packages));consumer(MemoryMarshal.Cast<byte,T>(packages));}
}