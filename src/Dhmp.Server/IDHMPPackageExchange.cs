using Dhmp.Protocol;

namespace Dhmp.Server;

/// <summary>
/// Bounded handoff boundary between the Stream Processor and Model Processor.
/// Implementations must not grow without bound.
/// </summary>
public interface IDHMPPackageExchange
{
    bool TryPublish(in DHMPPackage package);
    bool TryTake(out DHMPPackage package);
}
