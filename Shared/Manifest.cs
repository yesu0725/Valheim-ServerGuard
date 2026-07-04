using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ValheimServerGuard.Shared
{
    // Stable, cross-process fingerprint of a "set of mods." Both the server (from its
    // allowlist) and the client (from its loaded BepInEx plugins) compute it the same
    // way, so the two values can be compared at attestation time and surfaced to
    // operators/players as a short string ("the server's modpack is a3f9b2c1").
    //
    // Two flavours:
    //   STRICT - keyed by (lowercase-key | sha256). Two parties match only if every mod
    //            is present at the exact same binary. Use for hardened deployments.
    //   LOOSE  - keyed by lowercase-key only. Two parties match if they have the SAME
    //            set of mod identifiers regardless of version/binary. Use for friendly
    //            "are we on the same modpack" comparison across version bumps.
    // NOTE on signatures: we deliberately use KeyValuePair<string,string> instead of
    // C# value tuples. Valheim's Mono runtime doesn't ship System.ValueTuple, so any
    // ValueTuple-bearing field in a compiler-generated closure throws TypeLoadException
    // at Awake (observed on the client). KeyValuePair lives in System.Collections.Generic
    // which is always loaded.
    public static class ModsetFingerprint
    {
        public static string ComputeStrict(IEnumerable<KeyValuePair<string, string>> entries)
            => Sha256Hex(Canonicalize(entries, withHash: true));

        public static string ComputeLoose(IEnumerable<KeyValuePair<string, string>> entries)
            => Sha256Hex(Canonicalize(entries, withHash: false));

        // Short, human-shareable prefix (8 hex chars = 32 bits of identifier).
        public static string Short(string fullHex)
            => string.IsNullOrEmpty(fullHex) || fullHex.Length < 8 ? (fullHex ?? "") : fullHex.Substring(0, 8);

        private static string Canonicalize(IEnumerable<KeyValuePair<string, string>> entries, bool withHash)
        {
            if (entries == null) return "";
            var lines = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Key))
                .Select(e => withHash
                    ? $"{(e.Key ?? "").ToLowerInvariant()}|{(e.Value ?? "").ToLowerInvariant()}"
                    : (e.Key ?? "").ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            return lines.Count == 0 ? "" : string.Join("\n", lines);
        }

        private static string Sha256Hex(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input ?? ""));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }
    }

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
