using System.Net;
using System.Net.Sockets;

namespace DHMP.AspNetCore;

/// <summary>One explicitly configured, authenticated server-to-server API peer.</summary>
public sealed class DhmpApiOptions
{
    public string LocalAddress { get; set; } = "";
    public string RemoteAddress { get; set; } = "";
    public string ApiOrigin { get; set; } = "";
    public string PreSharedKeyBase64 { get; set; } = "";
    public uint KeyId { get; set; } = 1;
    public bool Initiator { get; set; }
    public bool AcceptRequests { get; set; }
    public int PathMtu { get; set; } = 1280;
    public int AdditionalIpv6HeaderBytes { get; set; }
    public int RecordsPerSecond { get; set; } = 1000;
    public int MaximumBodyBytes { get; set; } = 262144;
    public int MaximumMessageBytes { get; set; } = 524288;
    public int MaximumInFlight { get; set; } = 32;
    public int MaximumConcurrentRequests { get; set; } = 8;
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);
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
