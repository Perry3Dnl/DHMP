using Dhmp.Protocol;

namespace Dhmp.Server;

/// <summary>
/// Hot-path processor: turns the transport byte stream into complete fixed-size DHMP packages.
/// It MUST NOT construct developer models or invoke application code.
/// </summary>
public interface IDHMPStreamProcessor
{
    int PackageSize { get; }

    /// <summary>
    /// Processes available stream bytes and publishes complete packages to the package exchange.
    /// Returns the number of bytes consumed from <paramref name="input"/>.
    /// </summary>
    int Process(ReadOnlySpan<byte> input, IDHMPPackageExchange exchange);
}
