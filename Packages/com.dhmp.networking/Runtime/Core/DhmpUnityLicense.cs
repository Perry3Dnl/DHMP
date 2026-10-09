using System;
using System.Security.Cryptography;

namespace DHMP.Unity
{
    // Same DHMP1 signature and application identity as DHMP.Licensing; no wire fields or per-tick checks.
    public static class DhmpUnityLicense
    {
        public static void Validate(string license, Guid applicationId, byte[] publicKey)
        {
            if (applicationId == Guid.Empty || publicKey == null || publicKey.Length == 0)
                throw new InvalidOperationException("Configure the application's DHMP identity and trusted public verification key.");
            if (string.IsNullOrWhiteSpace(license) || license.Length > 8192)
                throw new InvalidOperationException("A DHMP application license is required at startup.");
            string[] parts = license.Split('.');
            if (parts.Length != 3 || parts[0] != "DHMP1") throw new InvalidOperationException("Unsupported DHMP license.");
            try
            {
                byte[] payload = Decode(parts[1]), signature = Decode(parts[2]);
                if (payload.Length != 41 || payload[0] != 1 || signature.Length == 0)
                    throw new InvalidOperationException("Malformed DHMP license.");
                using (ECDsa verifier = ECDsa.Create())
                {
                    verifier.ImportSubjectPublicKeyInfo(publicKey, out int read);
                    if (read != publicKey.Length || !verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256))
                        throw new InvalidOperationException("Invalid DHMP license signature.");
                }
                if (DhmpWire.ReadGuid(payload, 1) != applicationId)
                    throw new InvalidOperationException("The DHMP license belongs to another ApplicationId.");
            }
            catch (FormatException e) { throw new InvalidOperationException("Malformed DHMP license.", e); }
            catch (CryptographicException e) { throw new InvalidOperationException("DHMP license verification failed.", e); }
            // Unsupported cryptographic runtimes fail closed; no silent evaluation fallback.
        }

        private static byte[] Decode(string value)
        {
            string text = value.Replace('-', '+').Replace('_', '/');
            if (text.Length % 4 == 1) throw new FormatException();
            return Convert.FromBase64String(text.PadRight((text.Length + 3) / 4 * 4, '='));
        }
    }
}
