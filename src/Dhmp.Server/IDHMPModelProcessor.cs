using Dhmp.Protocol;

namespace Dhmp.Server;

/// <summary>
/// DHMP library processor that materializes the single negotiated connection contract.
/// Application processing starts only after this boundary.
/// </summary>
public interface IDHMPModelProcessor<T> where T : struct
{
    T Process(in DHMPPackage package);
}
