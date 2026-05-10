using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ValheimServerGuard.Shared
{
    [Serializable]
    public class ModManifestEntry
    {
        public string Guid;
        public string Name;
        public string Version;
        public string Sha256;
    }

    [Serializable]
    public class ModManifest
    {
        // Wire schema. Bumped only on incompatible changes.
        public string SchemaVersion = "1";

        // Challenge string echoed back from the server-issued RequestManifest RPC.
        // Server-side maps it to a specific peer + issuance time, defeating cross-peer replay.
        public string Challenge;

        // Client-side wall-clock at manifest build time. Used to bound replay window
        // independently of the challenge. Validated by the server against MaxClockSkewSeconds.
        public long TimestampUtc;

        public List<ModManifestEntry> Mods = new List<ModManifestEntry>();

        // HMAC-SHA256 base64. Computed over CanonicalForHmac() with shared secret.
        public string Hmac;

        public string CanonicalForHmac()
        {
            var sb = new StringBuilder();
            sb.Append(SchemaVersion ?? "").Append('|');
            sb.Append(Challenge ?? "").Append('|');
            sb.Append(TimestampUtc).Append('|');

            // Deterministic ordering — sort by Guid (or Name fallback).
            var sorted = new List<ModManifestEntry>(Mods ?? new List<ModManifestEntry>());
            sorted.Sort((a, b) =>
            {
                var ka = !string.IsNullOrEmpty(a?.Guid) ? a.Guid : (a?.Name ?? "");
                var kb = !string.IsNullOrEmpty(b?.Guid) ? b.Guid : (b?.Name ?? "");
                return string.CompareOrdinal(ka, kb);
            });
            foreach (var m in sorted)
            {
                sb.Append(m?.Guid ?? "").Append(':');
                sb.Append(m?.Name ?? "").Append(':');
                sb.Append(m?.Version ?? "").Append(':');
                sb.Append(m?.Sha256 ?? "").Append(';');
            }
            return sb.ToString();
        }

        public static string ComputeHmac(string canonical, string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "";
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
            {
                var hash = h.ComputeHash(Encoding.UTF8.GetBytes(canonical ?? ""));
                return Convert.ToBase64String(hash);
            }
        }

        public static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
