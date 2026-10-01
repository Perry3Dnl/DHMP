using System.Security.Cryptography;
using System.Text.Json;
using DHMP.AspNetCore.Tests;

// Test fixture only: the ephemeral issuer is never exported or trusted by production packages.
if (args.Length != 3) throw new ArgumentException("Usage: OUTPUT_DIRECTORY FRONTEND_IPV6 BACKEND_IPV6");
Directory.CreateDirectory(args[0]);
byte[] psk = RandomNumberGenerator.GetBytes(32);
try
{
    foreach (bool frontend in new[] { true, false })
    {
        using var issuer = new TestLicenseIssuer();
        Guid applicationId = Guid.NewGuid();
        var configuration = new
        {
            Urls = "http://127.0.0.1:5080",
            Lab = new { Enabled = true },
            Backend = new { BaseAddress = "https://api.invalid/" },
            DHMP = new
            {
                ApplicationId = applicationId,
                LicenseKey = issuer.Issue(applicationId),
                PublicVerificationKeyBase64 = Convert.ToBase64String(issuer.PublicKey),
                Api = new
                {
                    LocalAddress = args[frontend ? 1 : 2], RemoteAddress = args[frontend ? 2 : 1],
                    ApiOrigin = "https://api.invalid/", Initiator = frontend, AcceptRequests = !frontend,
                    PreSharedKeyBase64 = Convert.ToBase64String(psk), KeyId = 1, PathMtu = 1280,
                    RequestTimeout = "00:00:03", HandshakeTimeout = "00:00:30", RecordsPerSecond = 500,
                    MaximumBodyBytes = 32768, MaximumMessageBytes = 65536, MaximumInFlight = 4, MaximumConcurrentRequests = 2
                }
            }
        };
        string path = Path.Combine(args[0], frontend ? "frontend.json" : "backend.json");
        // Private directory/file; fixture credentials are never uploaded with test evidence.
        if (OperatingSystem.IsLinux())
        {
            using var file = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            JsonSerializer.Serialize(file, configuration);
        }
        else File.WriteAllText(path, JsonSerializer.Serialize(configuration));
    }
}
finally { CryptographicOperations.ZeroMemory(psk); }
Console.WriteLine("Ephemeral test configurations created; signing private keys were not exported.");
