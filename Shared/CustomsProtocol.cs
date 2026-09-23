using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace ValheimServerGuard.Shared
{
    // ==========================================================================
    // Customs - the inventory declaration both halves agree on.
    //
    // A dedicated server has no read of a remote player's inventory: it lives in
    // the player's process and is saved in the character file the player owns. So
    // the attested client half declares it - a full snapshot sent over the player's
    // own connection - and the server compares it against the inventory that
    // character was last trusted to leave with (see CustomsLedger.cs).
    //
    // This file holds what travels on the wire: the item identity, the
    // normalisation and delta maths, and the request/report formats. It has no
    // Unity or Valheim types, so tests/ServerGuard.Tests compiles it unchanged.
    // No value tuples anywhere: Valheim's Mono runtime does not ship them.
    // ==========================================================================

    // One inventory line: an item identity plus how many of it. Quantity is the only
    // field that is not part of the identity, which is what makes deltas work.
    //
    // Identity is deliberately more than the prefab name. An upgraded, re-crafted or
    // modded copy of an item is a different object from the one that left: a
    // quality-4 sword crafted elsewhere must not pass as the quality-1 sword that
    // logged out, and modded items keep their state in m_customData (hashed here).
    public sealed class CustomsItem
    {
        public string Prefab;
        public int    Quality;
        public int    Variant;
        public int    WorldLevel;
        public long   CrafterId;
        public string CrafterName;
        public string DataHash;      // SHA-256 hex of ItemData.m_customData, "" when empty
        public int    Quantity;

        public CustomsItem() { }

        public CustomsItem(string prefab, int quality, int variant, int worldLevel,
                           long crafterId, string crafterName, string dataHash, int quantity)
        {
            Prefab      = prefab ?? "";
            Quality     = quality;
            Variant     = variant;
            WorldLevel  = worldLevel;
            CrafterId   = crafterId;
            CrafterName = crafterName ?? "";
            DataHash    = dataHash ?? "";
            Quantity    = quantity;
        }

        // Free-form parts are length-prefixed, so no value can forge another identity
        // by containing a separator ("A|1" as a prefab cannot collide with prefab "A").
        public string IdentityKey()
        {
            var prefab  = Prefab ?? "";
            var crafter = CrafterName ?? "";
            var sb = new StringBuilder(prefab.Length + crafter.Length + 96);
            sb.Append(prefab.Length).Append(':').Append(prefab).Append('|');
            sb.Append(Quality).Append('|').Append(Variant).Append('|').Append(WorldLevel).Append('|');
            sb.Append(CrafterId.ToString(CultureInfo.InvariantCulture)).Append('|');
            sb.Append(crafter.Length).Append(':').Append(crafter).Append('|');
            sb.Append(DataHash ?? "");
            return sb.ToString();
        }

        internal CustomsItem WithQuantity(int quantity)
        {
            return new CustomsItem(Prefab, Quality, Variant, WorldLevel, CrafterId, CrafterName, DataHash, quantity);
        }
    }

    // Aggregation, deltas and signatures. Pure functions.
    public static class CustomsItems
    {
        // Collapses stacks with the same identity and sorts them, so two inventories
        // that hold the same things produce the same list regardless of slot layout.
        public static List<CustomsItem> Normalize(IEnumerable<CustomsItem> items)
        {
            var byKey = new Dictionary<string, long>(StringComparer.Ordinal);
            var first = new Dictionary<string, CustomsItem>(StringComparer.Ordinal);
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item == null || item.Quantity <= 0) continue;
                    var key = item.IdentityKey();
                    long have;
                    byKey.TryGetValue(key, out have);
                    byKey[key] = have + item.Quantity;
                    if (!first.ContainsKey(key)) first[key] = item;
                }
            }

            var keys = new List<string>(byKey.Keys);
            keys.Sort(StringComparer.Ordinal);
            var result = new List<CustomsItem>(keys.Count);
            foreach (var key in keys)
            {
                var total = byKey[key];
                result.Add(first[key].WithQuantity(total > int.MaxValue ? int.MaxValue : (int)total));
            }
            return result;
        }

        // What `current` carries beyond `baseline`. Items that went missing or were
        // used up are not the concern of this check: arriving with less is fine.
        public static List<CustomsItem> PositiveDelta(IEnumerable<CustomsItem> baseline, IEnumerable<CustomsItem> current)
        {
            var had = Quantities(baseline);
            var delta = new List<CustomsItem>();
            foreach (var item in Normalize(current))
            {
                long before;
                had.TryGetValue(item.IdentityKey(), out before);
                long extra = item.Quantity - before;
                if (extra > 0) delta.Add(item.WithQuantity((int)extra));
            }
            return delta;
        }

        // Per-identity maximum of two snapshots of the same inventory, taken a moment
        // apart. Used for the arrival declaration, which merges the inventory as it was
        // loaded with the inventory once the character has fully spawned.
        public static List<CustomsItem> MaxMerge(IEnumerable<CustomsItem> a, IEnumerable<CustomsItem> b)
        {
            var qa = Quantities(a);
            var merged = new List<CustomsItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in Normalize(b))
            {
                var key = item.IdentityKey();
                seen.Add(key);
                long other;
                qa.TryGetValue(key, out other);
                merged.Add(other > item.Quantity ? item.WithQuantity((int)other) : item);
            }
            foreach (var item in Normalize(a))
                if (!seen.Contains(item.IdentityKey())) merged.Add(item);
            return Normalize(merged);
        }

        public static long Total(IEnumerable<CustomsItem> items)
        {
            long total = 0;
            if (items == null) return 0;
            foreach (var item in items)
                if (item != null && item.Quantity > 0) total += item.Quantity;
            return total;
        }

        // Drops operator-ignored prefabs (settings: customsIgnoredItems).
        public static List<CustomsItem> Without(IEnumerable<CustomsItem> items, ICollection<string> ignoredPrefabs)
        {
            var result = new List<CustomsItem>();
            if (items == null) return result;
            foreach (var item in items)
            {
                if (item == null) continue;
                if (ignoredPrefabs != null && ignoredPrefabs.Count > 0 && ignoredPrefabs.Contains(item.Prefab ?? "")) continue;
                result.Add(item);
            }
            return result;
        }

        // Stable digest of an inventory: equal multisets of items give equal digests.
        public static string Signature(IEnumerable<CustomsItem> items)
        {
            var normalized = Normalize(items);
            var sb = new StringBuilder();
            sb.Append(normalized.Count).Append('\n');
            foreach (var item in normalized)
                sb.Append(item.IdentityKey()).Append('#').Append(item.Quantity.ToString(CultureInfo.InvariantCulture)).Append('\n');
            return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // Deterministic digest of ItemData.m_customData: keys sorted ordinal, keys and
        // values length-prefixed so {"ab":"c"} and {"a":"bc"} differ. "" when empty.
        public static string HashCustomData(IEnumerable<KeyValuePair<string, string>> data)
        {
            if (data == null) return "";
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in data)
                if (kv.Key != null) map[kv.Key] = kv.Value ?? "";
            if (map.Count == 0) return "";

            var sb = new StringBuilder();
            foreach (var kv in map)
                sb.Append(kv.Key.Length).Append(':').Append(kv.Key).Append('=')
                  .Append(kv.Value.Length).Append(':').Append(kv.Value).Append(';');
            return Sha256Hex(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // "Wood x40, SwordIron, +3 more". Prefab names only - quality, crafter and
        // hashes stay out of log lines and Discord posts.
        public static string Summarize(IEnumerable<CustomsItem> items, int maxEntries)
        {
            var totals = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item == null || item.Quantity <= 0) continue;
                    var name = DisplayName(item.Prefab);
                    long have;
                    totals.TryGetValue(name, out have);
                    totals[name] = have + item.Quantity;
                }
            }
            if (totals.Count == 0) return "(nothing)";

            var sb = new StringBuilder();
            int shown = 0;
            foreach (var kv in totals)
            {
                if (maxEntries > 0 && shown == maxEntries) break;
                if (shown > 0) sb.Append(", ");
                sb.Append(kv.Key);
                if (kv.Value > 1) sb.Append(" x").Append(kv.Value.ToString(CultureInfo.InvariantCulture));
                shown++;
            }
            if (shown < totals.Count) sb.Append(", +").Append(totals.Count - shown).Append(" more");
            return sb.ToString();
        }

        // Prefab name made safe for a log line or a Discord post.
        public static string DisplayName(string prefab)
        {
            var sb = new StringBuilder();
            foreach (var c in prefab ?? "")
            {
                if (sb.Length >= 48) break;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.')
                    sb.Append(c);
            }
            return sb.Length == 0 ? "?" : sb.ToString();
        }

        // A player-supplied name made safe for a log line or a Discord post: letters
        // (any script), digits, spaces, '-' and '.' survive; mention, markdown and
        // markup characters do not.
        public static string SafeName(string name)
        {
            var sb = new StringBuilder();
            foreach (var c in name ?? "")
            {
                if (sb.Length >= CustomsLimits.MaxNameLength) break;
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '.') sb.Append(c);
            }
            var v = sb.ToString().Trim();
            return v.Length == 0 ? "?" : v;
        }

        // What the client sends: every free-form string cleaned, every number inside
        // the bounds the server enforces, stacks merged. Applied after merging, so a
        // stack-size mod cannot push one record past MaxQuantity and get an honest
        // report refused; past that point Customs sees MaxQuantity.
        public static List<CustomsItem> ForReport(IEnumerable<CustomsItem> items)
        {
            var cleaned = new List<CustomsItem>();
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item == null || item.Quantity <= 0) continue;
                    var prefab = Clean(item.Prefab, CustomsLimits.MaxPrefabLength);
                    cleaned.Add(new CustomsItem(
                        prefab.Length > 0 ? prefab : "UnknownItem",
                        Clamp(item.Quality, 0, CustomsLimits.MaxQuality),
                        Clamp(item.Variant, 0, CustomsLimits.MaxVariant),
                        Clamp(item.WorldLevel, 0, CustomsLimits.MaxWorldLevel),
                        item.CrafterId,
                        Clean(item.CrafterName, CustomsLimits.MaxNameLength),
                        item.DataHash,
                        item.Quantity));
                }
            }
            var result = Normalize(cleaned);
            for (int i = 0; i < result.Count; i++)
                if (result[i].Quantity > CustomsLimits.MaxQuantity) result[i] = result[i].WithQuantity(CustomsLimits.MaxQuantity);
            return result;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        // What the client does to every free-form string before sending it, so an
        // honest report always passes the server's validation: control characters
        // removed, trimmed, cut to length without splitting a surrogate pair.
        public static string Clean(string s, int maxLength)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                if (!char.IsControl(c)) sb.Append(c);
            var v = sb.ToString().Trim();
            if (v.Length > maxLength)
            {
                int cut = maxLength;
                if (cut > 0 && char.IsHighSurrogate(v[cut - 1])) cut--;
                v = v.Substring(0, cut);
            }
            return v;
        }

        internal static Dictionary<string, long> Quantities(IEnumerable<CustomsItem> items)
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var item in Normalize(items)) map[item.IdentityKey()] = item.Quantity;
            return map;
        }

        internal static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes ?? new byte[0]);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }

    // Bounds applied to every report the server reads, and to every baseline file it
    // loads. Only the record count is operator-tunable (customsMaxItemRecords); the
    // rest are sanity limits far above anything a real inventory produces, and the
    // client clamps to the same numbers before sending.
    public sealed class CustomsLimits
    {
        public const int DefaultRecords   = 256;
        public const int MinRecords       = 32;
        public const int MaxRecordsCap    = 4096;
        public const int MaxPrefabLength  = 96;
        public const int MaxNameLength    = 64;      // crafter and character names
        public const int MaxIdLength      = 24;      // character id: a decimal long
        public const int MaxNonceLength   = 64;
        public const int MaxQuantity      = 1000000; // per record
        public const long MaxTotal        = 100000000; // whole report; keeps every sum inside int
        public const int MaxQuality       = 1000;
        public const int MaxVariant       = 100000;
        public const int MaxWorldLevel    = 1000;
        public const int MaxDepth         = 8;

        public int MaxRecords { get; private set; }

        // Per-record budget covers the longest legal record with multi-byte names.
        public int MaxPayloadBytes { get { return 2048 + MaxRecords * 512; } }

        public CustomsLimits(int maxRecords)
        {
            if (maxRecords <= 0) maxRecords = DefaultRecords;
            MaxRecords = maxRecords < MinRecords ? MinRecords : (maxRecords > MaxRecordsCap ? MaxRecordsCap : maxRecords);
        }
    }

    public enum CustomsReportKind
    {
        Declare    = 0,   // the answer to a server request: what the character carries now
        Change     = 1,   // the inventory settled after a change
        Checkpoint = 2,   // periodic, changed or not
        Logout     = 3    // best-effort departure record
    }

    // One validated client report. Deliberately carries no SteamID: the server binds a
    // report to the connection it arrived on, never to anything the payload claims.
    public sealed class CustomsReport
    {
        public string Nonce = "";
        public long   Sequence;
        public CustomsReportKind Kind;
        public string CharacterId = "";
        public string CharacterName = "";
        public List<CustomsItem> Items = new List<CustomsItem>();
    }

    public enum CustomsProblem
    {
        None = 0,
        Oversized,
        Malformed,
        UnsupportedVersion,
        MissingField,
        BadField,
        TooManyItems,
        QuantityOutOfRange,
        // Session-level problems, raised by CustomsSession.Screen
        WrongKind,
        CharacterMismatch,
        CharacterSwitched
    }

    // The two messages Customs adds to ServerGuard's RPC set.
    //
    //   server -> client  ServerGuard_CustomsRequest  "version|nonce|checkpointSeconds|debounceSeconds|maxRecords"
    //                                                  An empty nonce means "stop reporting".
    //   client -> server  ServerGuard_CustomsReport   compact JSON, see WriteReport
    //
    // A client without Customs never registers the request handler, and a server
    // without it never registers the report handler, so either side can be older.
    public static class CustomsWire
    {
        public const int    Version    = 1;
        public const string RequestRpc = "ServerGuard_CustomsRequest";
        public const string ReportRpc  = "ServerGuard_CustomsReport";

        public sealed class Request
        {
            public string Nonce = "";
            public int CheckpointSeconds = 120;
            public int DebounceSeconds   = 5;
            public int MaxRecords        = CustomsLimits.DefaultRecords;
            public bool Stop { get { return string.IsNullOrEmpty(Nonce); } }
        }

        public static string BuildRequest(string nonce, int checkpointSeconds, int debounceSeconds, int maxRecords)
        {
            return string.Join("|", new[]
            {
                Version.ToString(CultureInfo.InvariantCulture),
                (nonce ?? "").Replace("|", ""),
                ClampCheckpoint(checkpointSeconds).ToString(CultureInfo.InvariantCulture),
                ClampDebounce(debounceSeconds).ToString(CultureInfo.InvariantCulture),
                new CustomsLimits(maxRecords).MaxRecords.ToString(CultureInfo.InvariantCulture),
            });
        }

        public static string BuildStop()
        {
            return Version.ToString(CultureInfo.InvariantCulture) + "|";
        }

        // Client side. Values are clamped again so a bad server value cannot turn the
        // client into a report storm.
        public static bool TryParseRequest(string payload, out Request request)
        {
            request = null;
            var parts = (payload ?? "").Split('|');
            int version;
            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out version)) return false;
            if (version != Version) return false;

            var r = new Request { Nonce = parts[1].Trim() };
            if (r.Nonce.Length > CustomsLimits.MaxNonceLength) return false;
            int n;
            if (parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) r.CheckpointSeconds = ClampCheckpoint(n);
            if (parts.Length > 3 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) r.DebounceSeconds = ClampDebounce(n);
            if (parts.Length > 4 && int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) r.MaxRecords = new CustomsLimits(n).MaxRecords;
            request = r;
            return true;
        }

        public static int ClampCheckpoint(int seconds) { return seconds < 30 ? 30 : (seconds > 3600 ? 3600 : seconds); }
        public static int ClampDebounce(int seconds)   { return seconds < 1 ? 1 : (seconds > 120 ? 120 : seconds); }

        public static string KindName(CustomsReportKind kind)
        {
            switch (kind)
            {
                case CustomsReportKind.Declare:    return "declare";
                case CustomsReportKind.Change:     return "change";
                case CustomsReportKind.Checkpoint: return "checkpoint";
                default:                           return "logout";
            }
        }

        public static bool TryParseKind(string name, out CustomsReportKind kind)
        {
            switch (name)
            {
                case "declare":    kind = CustomsReportKind.Declare;    return true;
                case "change":     kind = CustomsReportKind.Change;     return true;
                case "checkpoint": kind = CustomsReportKind.Checkpoint; return true;
                case "logout":     kind = CustomsReportKind.Logout;     return true;
                default:           kind = CustomsReportKind.Change;     return false;
            }
        }

        // {"v":1,"n":nonce,"q":seq,"k":kind,"c":characterId,"cn":characterName,
        //  "i":[[prefab,quality,variant,worldLevel,crafterId,crafterName,dataHash,qty],...]}
        //
        // Items are fixed-shape arrays rather than objects: a modded inventory is
        // hundreds of records and the key names would otherwise dominate the payload.
        public static string WriteReport(CustomsReport report)
        {
            if (report == null) throw new ArgumentNullException("report");
            var sw = new StringWriter(new StringBuilder(1024), CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(sw))
            {
                w.Formatting = Formatting.None;
                w.WriteStartObject();
                w.WritePropertyName("v");  w.WriteValue(Version);
                w.WritePropertyName("n");  w.WriteValue(report.Nonce ?? "");
                w.WritePropertyName("q");  w.WriteValue(report.Sequence);
                w.WritePropertyName("k");  w.WriteValue(KindName(report.Kind));
                w.WritePropertyName("c");  w.WriteValue(report.CharacterId ?? "");
                w.WritePropertyName("cn"); w.WriteValue(report.CharacterName ?? "");
                w.WritePropertyName("i");  WriteItems(w, CustomsItems.Normalize(report.Items));
                w.WriteEndObject();
            }
            return sw.ToString();
        }

        // Server side. Written as a validator that happens to parse: every field is
        // attacker-controlled, nothing is allocated per item before the count is known
        // to be in range, and the result is all-or-nothing - a report either passes
        // whole or is refused whole, so a half-read inventory can never be judged.
        public static bool TryReadReport(string payload, CustomsLimits limits, out CustomsReport report,
                                         out CustomsProblem problem, out string detail)
        {
            report = null;
            if (limits == null) limits = new CustomsLimits(0);
            if (payload == null) return Fail(CustomsProblem.Malformed, "empty payload", out problem, out detail);
            // A char is at least one UTF-8 byte, so this screens before encoding anything.
            if (payload.Length > limits.MaxPayloadBytes || Encoding.UTF8.GetByteCount(payload) > limits.MaxPayloadBytes)
                return Fail(CustomsProblem.Oversized, "over " + limits.MaxPayloadBytes + " bytes", out problem, out detail);

            var r = new CustomsReport();
            bool sawV = false, sawN = false, sawQ = false, sawK = false, sawC = false, sawCn = false, sawI = false;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(payload)))
                {
                    reader.MaxDepth = CustomsLimits.MaxDepth;
                    reader.DateParseHandling = DateParseHandling.None;

                    if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                        return Fail(CustomsProblem.Malformed, "not a JSON object", out problem, out detail);

                    while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                    {
                        var name = (string)reader.Value;
                        bool duplicate;
                        switch (name)
                        {
                            case "v":
                            {
                                duplicate = sawV; sawV = true;
                                long v;
                                if (!ReadLong(reader, out v)) return Fail(CustomsProblem.BadField, "v", out problem, out detail);
                                if (v != Version) return Fail(CustomsProblem.UnsupportedVersion, "version " + v, out problem, out detail);
                                break;
                            }
                            case "n":
                                duplicate = sawN; sawN = true;
                                if (!ReadString(reader, CustomsLimits.MaxNonceLength, out r.Nonce)) return Fail(CustomsProblem.BadField, "n", out problem, out detail);
                                break;
                            case "q":
                                duplicate = sawQ; sawQ = true;
                                if (!ReadLong(reader, out r.Sequence) || r.Sequence < 1) return Fail(CustomsProblem.BadField, "q", out problem, out detail);
                                break;
                            case "k":
                            {
                                duplicate = sawK; sawK = true;
                                string k;
                                if (!ReadString(reader, 16, out k) || !TryParseKind(k, out r.Kind)) return Fail(CustomsProblem.BadField, "k", out problem, out detail);
                                break;
                            }
                            case "c":
                                duplicate = sawC; sawC = true;
                                if (!ReadString(reader, CustomsLimits.MaxIdLength, out r.CharacterId) || !IsCanonicalCharacterId(r.CharacterId))
                                    return Fail(CustomsProblem.BadField, "c (character id)", out problem, out detail);
                                break;
                            case "cn":
                                duplicate = sawCn; sawCn = true;
                                if (!ReadString(reader, CustomsLimits.MaxNameLength, out r.CharacterName)) return Fail(CustomsProblem.BadField, "cn", out problem, out detail);
                                break;
                            case "i":
                                duplicate = sawI; sawI = true;
                                if (duplicate) break;
                                if (!TryReadItems(reader, limits, r.Items, out problem, out detail)) return false;
                                break;
                            default:
                                // A field from a newer client: skip it whole rather than guess.
                                duplicate = false;
                                if (!reader.Read()) return Fail(CustomsProblem.Malformed, "truncated", out problem, out detail);
                                reader.Skip();
                                break;
                        }
                        if (duplicate) return Fail(CustomsProblem.Malformed, "duplicate field '" + name + "'", out problem, out detail);
                    }

                    if (reader.TokenType != JsonToken.EndObject) return Fail(CustomsProblem.Malformed, "unterminated object", out problem, out detail);
                    if (reader.Read()) return Fail(CustomsProblem.Malformed, "content after the object", out problem, out detail);
                }
            }
            catch (Exception ex)
            {
                // Newtonsoft reports bad syntax, depth and numeric overflow as exceptions.
                // Its messages quote the payload (property paths), and this detail ends
                // up in logs and admin posts, so only the type and position are kept.
                var jre = ex as JsonReaderException;
                var position = jre != null ? " at position " + jre.LinePosition.ToString(CultureInfo.InvariantCulture) : "";
                return Fail(CustomsProblem.Malformed, ex.GetType().Name + position, out problem, out detail);
            }

            if (!sawV) return Fail(CustomsProblem.MissingField, "v", out problem, out detail);
            if (!sawN) return Fail(CustomsProblem.MissingField, "n", out problem, out detail);
            if (!sawQ) return Fail(CustomsProblem.MissingField, "q", out problem, out detail);
            if (!sawK) return Fail(CustomsProblem.MissingField, "k", out problem, out detail);
            if (!sawC) return Fail(CustomsProblem.MissingField, "c", out problem, out detail);
            if (!sawCn) return Fail(CustomsProblem.MissingField, "cn", out problem, out detail);
            if (!sawI) return Fail(CustomsProblem.MissingField, "i", out problem, out detail);

            r.Items = CustomsItems.Normalize(r.Items);
            report = r;
            problem = CustomsProblem.None;
            detail = "";
            return true;
        }

        // A character id is the decimal form of PlayerProfile's player id, in exactly one
        // spelling. "05", "+5" and "5" must not become three baselines for one character.
        public static bool IsCanonicalCharacterId(string s)
        {
            long id;
            if (string.IsNullOrEmpty(s) || s.Length > CustomsLimits.MaxIdLength) return false;
            if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out id) || id == 0) return false;
            return string.Equals(s, id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        internal static void WriteItems(JsonWriter w, IList<CustomsItem> items)
        {
            w.WriteStartArray();
            foreach (var it in items)
            {
                w.WriteStartArray();
                w.WriteValue(it.Prefab ?? "");
                w.WriteValue(it.Quality);
                w.WriteValue(it.Variant);
                w.WriteValue(it.WorldLevel);
                w.WriteValue(it.CrafterId);
                w.WriteValue(it.CrafterName ?? "");
                w.WriteValue(it.DataHash ?? "");
                w.WriteValue(it.Quantity);
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        // Reads an item array (the reader is positioned just before it). Shared by the
        // report parser and the baseline store, so a hand-edited baseline file is held
        // to exactly the bounds a report is.
        internal static bool TryReadItems(JsonReader reader, CustomsLimits limits, List<CustomsItem> into,
                                          out CustomsProblem problem, out string detail)
        {
            if (!reader.Read() || reader.TokenType != JsonToken.StartArray)
                return Fail(CustomsProblem.Malformed, "items is not an array", out problem, out detail);

            long total = 0;
            while (reader.Read() && reader.TokenType != JsonToken.EndArray)
            {
                if (reader.TokenType != JsonToken.StartArray)
                    return Fail(CustomsProblem.Malformed, "item is not an array", out problem, out detail);
                if (into.Count >= limits.MaxRecords)
                    return Fail(CustomsProblem.TooManyItems, "more than " + limits.MaxRecords + " item records", out problem, out detail);

                string prefab, crafterName, hash;
                long quality, variant, worldLevel, crafterId, quantity;
                if (!ReadString(reader, CustomsLimits.MaxPrefabLength, out prefab) || prefab.Length == 0)
                    return Fail(CustomsProblem.BadField, "item prefab", out problem, out detail);
                if (!ReadLong(reader, out quality) || quality < 0 || quality > CustomsLimits.MaxQuality)
                    return Fail(CustomsProblem.BadField, "item quality", out problem, out detail);
                if (!ReadLong(reader, out variant) || variant < 0 || variant > CustomsLimits.MaxVariant)
                    return Fail(CustomsProblem.BadField, "item variant", out problem, out detail);
                if (!ReadLong(reader, out worldLevel) || worldLevel < 0 || worldLevel > CustomsLimits.MaxWorldLevel)
                    return Fail(CustomsProblem.BadField, "item world level", out problem, out detail);
                if (!ReadLong(reader, out crafterId))
                    return Fail(CustomsProblem.BadField, "item crafter id", out problem, out detail);
                if (!ReadString(reader, CustomsLimits.MaxNameLength, out crafterName))
                    return Fail(CustomsProblem.BadField, "item crafter name", out problem, out detail);
                if (!ReadString(reader, 64, out hash) || (hash.Length != 0 && !IsLowerHex64(hash)))
                    return Fail(CustomsProblem.BadField, "item data hash", out problem, out detail);
                if (!ReadLong(reader, out quantity))
                    return Fail(CustomsProblem.BadField, "item quantity", out problem, out detail);
                if (quantity < 1 || quantity > CustomsLimits.MaxQuantity)
                    return Fail(CustomsProblem.QuantityOutOfRange, "quantity " + quantity, out problem, out detail);
                if (!reader.Read() || reader.TokenType != JsonToken.EndArray)
                    return Fail(CustomsProblem.Malformed, "item record is not 8 fields", out problem, out detail);

                total += quantity;
                if (total > CustomsLimits.MaxTotal)
                    return Fail(CustomsProblem.QuantityOutOfRange, "total quantity over " + CustomsLimits.MaxTotal, out problem, out detail);

                into.Add(new CustomsItem(prefab, (int)quality, (int)variant, (int)worldLevel, crafterId, crafterName, hash, (int)quantity));
            }
            if (reader.TokenType != JsonToken.EndArray)
                return Fail(CustomsProblem.Malformed, "unterminated item array", out problem, out detail);

            problem = CustomsProblem.None;
            detail = "";
            return true;
        }

        private static bool ReadLong(JsonReader reader, out long value)
        {
            value = 0;
            if (!reader.Read() || reader.TokenType != JsonToken.Integer) return false;
            // Anything past long arrives as BigInteger: out of range by definition.
            if (reader.Value is long) { value = (long)reader.Value; return true; }
            if (reader.Value is int)  { value = (int)reader.Value;  return true; }
            return false;
        }

        // Strings must be present, bounded and free of control characters (the client
        // strips those before sending, so only a forged report carries one).
        private static bool ReadString(JsonReader reader, int maxLength, out string value)
        {
            value = "";
            if (!reader.Read() || reader.TokenType != JsonToken.String) return false;
            var s = (string)reader.Value ?? "";
            if (s.Length > maxLength) return false;
            foreach (var c in s)
                if (char.IsControl(c)) return false;
            value = s;
            return true;
        }

        private static bool IsLowerHex64(string s)
        {
            if (s.Length != 64) return false;
            foreach (var c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static bool Fail(CustomsProblem p, string d, out CustomsProblem problem, out string detail)
        {
            problem = p;
            detail = d ?? "";
            return false;
        }
    }
}
