using System.Net;
using System.Net.Sockets;

namespace DHMP.AspNetCore;

/// <summary>One explicitly configured, authenticated server-to-server API peer.</summary>
public sealed class DhmpApiOptions
{
    /// <summary>Explicit native IPv6 address owned by this local DHMP endpoint.</summary>
    public string LocalAddress { get; set; } = "";
    /// <summary>Explicit native IPv6 address of the configured remote DHMP peer.</summary>
    public string RemoteAddress { get; set; } = "";
    /// <summary>HTTP or HTTPS origin whose client requests are redirected through the DHMP API transport.</summary>
    public string ApiOrigin { get; set; } = "";
    /// <summary>Base64-encoded 32-byte peer PSK. This is separate from the product license key.</summary>
    public string PreSharedKeyBase64 { get; set; } = "";
    /// <summary>Nonzero application-selected identifier for the configured peer PSK.</summary>
    public uint KeyId { get; set; } = 1;
    /// <summary>Whether this endpoint initiates the authenticated DHMP session setup.</summary>
    public bool Initiator { get; set; }
    /// <summary>Whether this host accepts incoming DAPI/1 requests from the configured peer.</summary>
    public bool AcceptRequests { get; set; }
    /// <summary>Configured IPv6 path MTU used to derive the initial raw payload budget.</summary>
    public int PathMtu { get; set; } = 1280;
    /// <summary>Reserved byte allowance for IPv6 extension headers beyond the fixed IPv6 header.</summary>
    public int AdditionalIpv6HeaderBytes { get; set; }
    /// <summary>
    /// Explicit opt-in for the current RFC 4727 experimental IPv6 Next Header 253/254 binding.
    /// This is false by default and is not a production-Internet compatibility guarantee.
    /// </summary>
    public bool EnableExperimentalProtocolNumbers { get; set; }
    /// <summary>Local smooth-pacing ceiling for DAPI/1 records per second.</summary>
    public int RecordsPerSecond { get; set; } = 1000;
    /// <summary>Maximum request or response body size accepted by the API profile.</summary>
    public int MaximumBodyBytes { get; set; } = 262144;
    /// <summary>Maximum encoded DAPI/1 message size, including metadata and body.</summary>
    public int MaximumMessageBytes { get; set; } = 524288;
    /// <summary>Maximum concurrently outstanding locally initiated API exchanges.</summary>
    public int MaximumInFlight { get; set; } = 32;
    /// <summary>Maximum concurrently executing inbound API requests.</summary>
    public int MaximumConcurrentRequests { get; set; } = 8;
    /// <summary>Total deadline for one DAPI/1 request/response exchange.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Total deadline for the authenticated raw-IPv6 session handshake.</summary>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal Uri Origin => new(ApiOrigin, UriKind.Absolute);
    internal void Validate()
    {
        foreach (string address in new[] { LocalAddress, RemoteAddress })
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.IsIPv4MappedToIPv6 || ip.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException("DHMP API requires explicitly configured native IPv6 addresses.");
        if (!Uri.TryCreate(ApiOrigin, UriKind.Absolute, out var origin) || origin.Scheme is not ("https" or "http") ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 || origin.UserInfo.Length != 0)
            throw new InvalidOperationException("DHMP ApiOrigin must be an absolute http/https origin without a path, query or credentials.");
        if (!EnableExperimentalProtocolNumbers)
            throw new InvalidOperationException(
                "DHMP API raw IPv6 currently requires explicit EnableExperimentalProtocolNumbers=true for the RFC 4727 253/254 research binding.");
        if (PathMtu < 1280 || RecordsPerSecond <= 0 || KeyId == 0 ||
            MaximumBodyBytes is < 1 or > 1048576 || MaximumMessageBytes < MaximumBodyBytes || MaximumMessageBytes > 2097152 ||
            MaximumInFlight is < 1 or > 128 || MaximumConcurrentRequests is < 1 or > 32 ||
            RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5) ||
            HandshakeTimeout <= TimeSpan.Zero || HandshakeTimeout > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("DHMP API limits and deadlines must be finite and within supported bounds.");
        if (AdditionalIpv6HeaderBytes < 0 || new DHMP.RawIpv6.DhmpIpv6PathBudget(PathMtu, AdditionalIpv6HeaderBytes).MaximumProtocolPayloadBytes < DhmpApiRecord.Size + 24)
            throw new InvalidOperationException("Configured IPv6 budget cannot fit the API profile and security envelope.");
        if ((long)MaximumInFlight * MaximumMessageBytes > 32 * 1024 * 1024)
            throw new InvalidOperationException("Combined DHMP API admission/message limits exceed the supported memory budget.");
        byte[] key = Convert.FromBase64String(PreSharedKeyBase64);
        try { if (key.Length != 32) throw new InvalidOperationException("DHMP API requires a 32-byte PSK, separate from the license key."); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }
    internal bool Matches(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Origin.Scheme &&
        uri.IdnHost.Equals(Origin.IdnHost, StringComparison.OrdinalIgnoreCase) && uri.Port == Origin.Port;
}
