using System;
using System.Net;
using UnityEngine;

namespace DHMP.Unity
{
    public enum DhmpLicenseMode { Development, LicensedApplication }

    [CreateAssetMenu(menuName = "DHMP/Networking Settings", fileName = "DhmpSettings")]
    public sealed class DhmpUnitySettings : ScriptableObject
    {
        [Header("IPv6 endpoints")]
        [Tooltip("Explicit local IPv6 address. Override with DHMP_LOCAL_IPV6 for a server deployment.")]
        public string localIpv6 = "::1";
        [Tooltip("Publisher-operated arena endpoint. Empty until a real demo server is provisioned.")]
        public string demoServerIpv6 = "";
        [Range(1, 32)] public int maximumPlayers = 16;
        [Header("Experimental preview deployment")]
        public bool enableExperimentalIpv6;
        [Tooltip("DUNA/1 preview payloads are plaintext. Compatibility and session tokens are not authentication.")]
        public bool allowUnprotectedDemoPayloads;
        [Header("Offline application licensing")]
        public DhmpLicenseMode licenseMode = DhmpLicenseMode.Development;
        public string clientApplicationId = "";
        public string serverApplicationId = "";
        [TextArea] public string clientLicense = "";
        [TextArea] public string serverLicense = "";
        [Tooltip("Base64 SubjectPublicKeyInfo of the trusted DHMP license issuer; never a private key.")]
        [TextArea] public string licensePublicKey = "";

        public string DemoEndpoint => Environment.GetEnvironmentVariable("DHMP_DEMO_IPV6") ?? demoServerIpv6;
        public IPAddress LocalAddress => ParseAddress(Environment.GetEnvironmentVariable("DHMP_LOCAL_IPV6") ?? localIpv6);
        public static IPAddress ParseAddress(string text)
        {
            if (!IPAddress.TryParse(text, out IPAddress address)) throw new ArgumentException("Enter a native IPv6 address, such as ::1 for a local experiment.");
            DhmpRawIpv6Socket.ValidateAddress(address); return address;
        }
        public void ValidateStartup(bool server, bool developmentBuild)
        {
            if (!enableExperimentalIpv6 || !allowUnprotectedDemoPayloads)
                throw new InvalidOperationException("Enable the experimental IPv6 and unprotected demo-payload options in the DHMP settings asset before this lab test.");
            if (maximumPlayers < 1 || maximumPlayers > 32) throw new InvalidOperationException("Preview capacity must be between 1 and 32 players.");
            if (licenseMode == DhmpLicenseMode.Development)
            {
                if (!developmentBuild) throw new InvalidOperationException("Development licensing is limited to the Editor and Development Builds. Configure application licenses for a release build.");
                return;
            }
            string id = Environment.GetEnvironmentVariable("DHMP_APPLICATION_ID") ?? (server ? serverApplicationId : clientApplicationId);
            string key = Environment.GetEnvironmentVariable("DHMP_LICENSE_KEY") ?? (server ? serverLicense : clientLicense);
            if (!Guid.TryParse(id, out Guid applicationId)) throw new InvalidOperationException("A valid DHMP ApplicationId is required.");
            DhmpUnityLicense.Validate(key, applicationId, Convert.FromBase64String(licensePublicKey));
        }
    }
}
