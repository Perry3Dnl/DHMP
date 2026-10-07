using System.Net;
using System.Net.Sockets;
using DHMP.RawIpv6;

namespace DHMP.Connector;

/// <summary>
/// Evaluates the transport paths the current host can attempt for a DHMP peer.
/// Local capability is only the first stage: <see cref="DhmpConnector"/> confirms
/// actual path reachability with the normal DHMP compatibility handshake.
/// </summary>
public static class DhmpTransportResolver
{
    public static IReadOnlyList<DhmpTransportCandidate> Probe(
        DhmpConnectorOptions options,
        IPAddress remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(remoteAddress);

        var candidates = new List<DhmpTransportCandidate>(2);

        if (options.TransportPreference !=
            DhmpTransportPreference.UdpCompatibilityOnly)
        {
            bool addressEligible =
                options.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6 &&
                remoteAddress.AddressFamily == AddressFamily.InterNetworkV6 &&
                !options.LocalAddress.IsIPv4MappedToIPv6 &&
                !remoteAddress.IsIPv4MappedToIPv6;

            if (!addressEligible)
            {
                candidates.Add(
                    new DhmpTransportCandidate(
                        DhmpTransportKind.RawIpv6,
                        false,
                        "Native Raw IPv6 requires native IPv6 addresses at both endpoints."));
            }
            else
            {
                DhmpRawIpv6HostProbeResult probe =
                    DhmpRawIpv6HostProbe.Probe(
                        options.EnableExperimentalProtocolNumbers);

                candidates.Add(
                    new DhmpTransportCandidate(
                        DhmpTransportKind.RawIpv6,
                        probe.IsReady,
                        probe.Message));
            }
        }

        if (options.TransportPreference !=
            DhmpTransportPreference.RawIpv6Only)
        {
            bool familyMatches =
                options.LocalAddress.AddressFamily ==
                remoteAddress.AddressFamily;

            candidates.Add(
                new DhmpTransportCandidate(
                    DhmpTransportKind.UdpCompatibility,
                    familyMatches,
                    familyMatches
                        ? "UDP compatibility is supported by the managed socket backend; the handshake still has to prove remote reachability."
                        : "UDP compatibility currently requires matching local and remote address families."));
        }

        return candidates;
    }
}
