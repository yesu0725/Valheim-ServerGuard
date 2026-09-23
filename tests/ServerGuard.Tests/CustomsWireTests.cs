using System.Collections.Generic;
using System.Text;
using ValheimServerGuard.Shared;
using static ValheimServerGuard.Tests.CustomsItemsTests;

namespace ValheimServerGuard.Tests
{
    internal static class CustomsWireTests
    {
        internal const string Nonce = "bm9uY2Utb25l";
        internal const string CharId = "1234567890123";

        internal static CustomsReport Report(CustomsReportKind kind, long seq, params CustomsItem[] items)
        {
            return new CustomsReport
            {
                Nonce = Nonce, Sequence = seq, Kind = kind,
                CharacterId = CharId, CharacterName = "Ragnar",
                Items = new List<CustomsItem>(items),
            };
        }

        private static CustomsProblem Problem(string payload, int maxRecords = 256)
        {
            CustomsReport r;
            CustomsProblem p;
            string d;
            CustomsWire.TryReadReport(payload, new CustomsLimits(maxRecords), out r, out p, out d);
            return p;
        }

        // A valid report with one field replaced by raw JSON.
        private static string With(string field, string rawValue)
        {
            var fields = new Dictionary<string, string>
            {
                { "v", "1" }, { "n", "\"" + Nonce + "\"" }, { "q", "1" }, { "k", "\"declare\"" },
                { "c", "\"" + CharId + "\"" }, { "cn", "\"Ragnar\"" }, { "i", "[[\"Wood\",1,0,0,0,\"\",\"\",5]]" },
            };
            if (rawValue == null) fields.Remove(field); else fields[field] = rawValue;
            var sb = new StringBuilder("{");
            foreach (var kv in fields)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append('"').Append(kv.Key).Append("\":").Append(kv.Value);
            }
            return sb.Append('}').ToString();
        }

        private static string Item(string raw) { return With("i", "[" + raw + "]"); }

        public static void Run()
        {
            T.Section("Customs wire format: bounds and malformed input");

            T.Run("a report survives the round trip, normalised", () =>
            {
                var report = Report(CustomsReportKind.Change, 7, I("Wood", 3), I("SwordIron", 1, 3, 1, 2, -99, "Ragnar", new string('f', 64)), I("Wood", 2));
                var json = CustomsWire.WriteReport(report);
                CustomsReport back;
                CustomsProblem p;
                string d;
                T.True(CustomsWire.TryReadReport(json, new CustomsLimits(256), out back, out p, out d), "parses: " + d);
                T.Equal(CustomsReportKind.Change, back.Kind, "kind");
                T.Equal(7L, back.Sequence, "sequence");
                T.Equal(CharId, back.CharacterId, "character id");
                T.Equal(CustomsItems.Signature(report.Items), CustomsItems.Signature(back.Items), "same inventory");
                T.Equal(2, back.Items.Count, "wood merged");
            });

            T.Run("whatever the client cleans, the server accepts", () =>
            {
                var messy = new CustomsItem(CustomsItems.Clean("Sword\u0007Iron " + new string('x', 200), CustomsLimits.MaxPrefabLength),
                    1, 0, 0, 0, CustomsItems.Clean("Ra\ngnar\u0001" + new string('y', 100), CustomsLimits.MaxNameLength), "", 1);
                var report = Report(CustomsReportKind.Declare, 1, messy);
                report.CharacterName = CustomsItems.Clean("Rag\tnar", CustomsLimits.MaxNameLength);
                T.Equal(CustomsProblem.None, Problem(CustomsWire.WriteReport(report)), "cleaned report is valid");
            });

            T.Run("whatever the client prepares, the server accepts - a stack-size-modded inventory too", () =>
            {
                var raw = new List<CustomsItem>();
                for (int i = 0; i < 40; i++) raw.Add(I("Wood", 50000));
                raw.Add(I("Sword\u0007Iron " + new string('x', 200), 1, quality: 5000, variant: -3, worldLevel: 99999,
                          crafter: "Ra\ngnar\u0001" + new string('y', 100)));
                var report = Report(CustomsReportKind.Declare, 1);
                report.Items = CustomsItems.ForReport(raw);
                T.Equal(CustomsProblem.None, Problem(CustomsWire.WriteReport(report)), "prepared report is valid");
            });

            T.Run("a parse error never echoes the payload into the detail that is logged and posted", () =>
            {
                CustomsReport r;
                CustomsProblem p;
                string d;
                T.False(CustomsWire.TryReadReport("{\"@everyone <@&1> **x**\":}", new CustomsLimits(256), out r, out p, out d), "refused");
                T.Equal(CustomsProblem.Malformed, p, "malformed");
                T.True(d.StartsWith("JsonReaderException"), "the error type is kept: " + d);
                T.False(d.Contains("@") || d.Contains("everyone") || d.Contains("*"), "no payload text: " + d);
            });

            T.Run("oversized payloads are refused before parsing, counted in UTF-8 bytes", () =>
            {
                var limits = new CustomsLimits(32);
                var big = With("cn", "\"" + new string('x', limits.MaxPayloadBytes) + "\"");
                T.Equal(CustomsProblem.Oversized, Problem(big, 32), "ASCII over the budget");
                var multi = With("zz", "\"" + new string('é', limits.MaxPayloadBytes / 2 + 16) + "\"");
                T.True(multi.Length < limits.MaxPayloadBytes, "fewer chars than the budget");
                T.Equal(CustomsProblem.Oversized, Problem(multi, 32), "but more bytes");
            });

            T.Run("more item records than the limit are refused", () =>
            {
                var items = new StringBuilder();
                for (int i = 0; i < 33; i++)
                {
                    if (i > 0) items.Append(',');
                    items.Append("[\"Item").Append(i).Append("\",1,0,0,0,\"\",\"\",1]");
                }
                T.Equal(CustomsProblem.TooManyItems, Problem(With("i", "[" + items + "]"), 32), "33 > 32");
            });

            T.Run("quantities outside 1..max are refused, and so is an absurd total", () =>
            {
                T.Equal(CustomsProblem.QuantityOutOfRange, Problem(Item("[\"Wood\",1,0,0,0,\"\",\"\",0]")), "zero");
                T.Equal(CustomsProblem.QuantityOutOfRange, Problem(Item("[\"Wood\",1,0,0,0,\"\",\"\",-5]")), "negative");
                T.Equal(CustomsProblem.QuantityOutOfRange, Problem(Item("[\"Wood\",1,0,0,0,\"\",\"\",1000001]")), "per record");
                var many = new StringBuilder();
                for (int i = 0; i < 101; i++)
                {
                    if (i > 0) many.Append(',');
                    many.Append("[\"I").Append(i).Append("\",1,0,0,0,\"\",\"\",1000000]");
                }
                T.Equal(CustomsProblem.QuantityOutOfRange, Problem(With("i", "[" + many + "]")), "total over 100,000,000");
            });

            T.Run("identity numbers outside their ranges are refused", () =>
            {
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",-1,0,0,0,\"\",\"\",1]")), "quality < 0");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",1001,0,0,0,\"\",\"\",1]")), "quality too high");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",1,-1,0,0,\"\",\"\",1]")), "variant < 0");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",1,0,1001,0,\"\",\"\",1]")), "world level too high");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",1.5,0,0,0,\"\",\"\",1]")), "float quality");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",1,0,0,99999999999999999999999,\"\",\"\",1]")), "crafter id past long");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",\"1\",0,0,0,\"\",\"\",1]")), "string where a number belongs");
            });

            T.Run("item records must have exactly eight fields", () =>
            {
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",1,0,0,0,\"\",\"\"]")), "seven");
                T.Equal(CustomsProblem.Malformed, Problem(Item("[\"Wood\",1,0,0,0,\"\",\"\",1,9]")), "nine");
                T.Equal(CustomsProblem.Malformed, Problem(Item("{\"p\":\"Wood\"}")), "an object instead of an array");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"\",1,0,0,0,\"\",\"\",1]")), "empty prefab");
            });

            T.Run("the custom-data hash must be empty or 64 lowercase hex digits", () =>
            {
                T.Equal(CustomsProblem.None, Problem(Item("[\"Wood\",1,0,0,0,\"\",\"" + new string('0', 64) + "\",1]")), "valid");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",1,0,0,0,\"\",\"" + new string('A', 64) + "\",1]")), "upper case");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",1,0,0,0,\"\",\"abc\",1]")), "short");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wood\",1,0,0,0,\"\",\"" + new string('g', 64) + "\",1]")), "not hex");
            });

            T.Run("strings must be bounded and free of control characters", () =>
            {
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"Wo\\u0000od\",1,0,0,0,\"\",\"\",1]")), "NUL in a prefab");
                T.Equal(CustomsProblem.BadField, Problem(With("cn", "\"Rag\\nnar\"")), "newline in a name");
                T.Equal(CustomsProblem.BadField, Problem(Item("[\"" + new string('P', 97) + "\",1,0,0,0,\"\",\"\",1]")), "prefab too long");
                T.Equal(CustomsProblem.BadField, Problem(With("cn", "\"" + new string('n', 65) + "\"")), "name too long");
                T.Equal(CustomsProblem.BadField, Problem(With("n", "\"" + new string('n', 65) + "\"")), "nonce too long");
                T.Equal(CustomsProblem.BadField, Problem(With("cn", "null")), "null name");
            });

            T.Run("every required field must be present", () =>
            {
                foreach (var f in new[] { "v", "n", "q", "k", "c", "cn", "i" })
                    T.Equal(CustomsProblem.MissingField, Problem(With(f, null)), "without '" + f + "'");
            });

            T.Run("a field given twice is refused", () =>
            {
                T.Equal(CustomsProblem.Malformed, Problem(With("i", "[]").TrimEnd('}') + ",\"i\":[[\"Gold\",1,0,0,0,\"\",\"\",9]]}"), "second item array");
                T.Equal(CustomsProblem.Malformed, Problem(With("c", "\"" + CharId + "\"").TrimEnd('}') + ",\"c\":\"42\"}"), "second character id");
            });

            T.Run("unsupported versions, kinds and sequences are refused", () =>
            {
                T.Equal(CustomsProblem.UnsupportedVersion, Problem(With("v", "2")), "version 2");
                T.Equal(CustomsProblem.BadField, Problem(With("k", "\"teleport\"")), "unknown kind");
                T.Equal(CustomsProblem.BadField, Problem(With("q", "0")), "sequence 0");
                T.Equal(CustomsProblem.BadField, Problem(With("q", "-3")), "negative sequence");
            });

            T.Run("character ids have exactly one spelling", () =>
            {
                T.Equal(CustomsProblem.None, Problem(With("c", "\"-4242\"")), "negative ids are legal");
                foreach (var bad in new[] { "05", "+5", "0", " 5", "5 ", "abc", "", "1e3", "99999999999999999999" })
                    T.Equal(CustomsProblem.BadField, Problem(With("c", "\"" + bad + "\"")), "'" + bad + "'");
            });

            T.Run("broken JSON of every shape is refused as malformed", () =>
            {
                T.Equal(CustomsProblem.Malformed, Problem(null), "null");
                T.Equal(CustomsProblem.Malformed, Problem(""), "empty");
                T.Equal(CustomsProblem.Malformed, Problem("not json"), "text");
                T.Equal(CustomsProblem.Malformed, Problem("[1,2,3]"), "array root");
                T.Equal(CustomsProblem.Malformed, Problem(With("k", "\"declare\"").Substring(0, 40)), "truncated");
                T.Equal(CustomsProblem.Malformed, Problem(With("k", "\"declare\"") + "{}"), "trailing content");
                T.Equal(CustomsProblem.Malformed, Problem(With("zz", "[[[[[[[[[[[[1]]]]]]]]]]]]")), "nested past the depth limit");
            });

            T.Run("an unknown field from a newer client is skipped, not guessed at", () =>
            {
                T.Equal(CustomsProblem.None, Problem(With("future", "{\"a\":[1,2,{\"b\":3}]}")), "skipped");
            });

            T.Section("Customs wire format: the server request");

            T.Run("a request round-trips and is clamped on the client", () =>
            {
                CustomsWire.Request r;
                T.True(CustomsWire.TryParseRequest(CustomsWire.BuildRequest("abc+/=", 90, 7, 300), out r), "parses");
                T.Equal("abc+/=", r.Nonce, "nonce");
                T.Equal(90, r.CheckpointSeconds, "checkpoint");
                T.Equal(7, r.DebounceSeconds, "debounce");
                T.Equal(300, r.MaxRecords, "records");
                T.True(CustomsWire.TryParseRequest("1|n|1|0|1", out r), "tiny values parse");
                T.Equal(30, r.CheckpointSeconds, "checkpoint floor");
                T.Equal(1, r.DebounceSeconds, "debounce floor");
                T.Equal(CustomsLimits.MinRecords, r.MaxRecords, "record floor");
                T.True(CustomsWire.TryParseRequest("1|n|999999|999|999999", out r), "huge values parse");
                T.Equal(3600, r.CheckpointSeconds, "checkpoint cap");
                T.Equal(120, r.DebounceSeconds, "debounce cap");
                T.Equal(CustomsLimits.MaxRecordsCap, r.MaxRecords, "record cap");
            });

            T.Run("an empty nonce means stop; bad requests are ignored", () =>
            {
                CustomsWire.Request r;
                T.True(CustomsWire.TryParseRequest(CustomsWire.BuildStop(), out r) && r.Stop, "stop");
                T.False(CustomsWire.TryParseRequest("2|abc|60|5|256", out r), "unknown version");
                T.False(CustomsWire.TryParseRequest("garbage", out r), "garbage");
                T.False(CustomsWire.TryParseRequest(null, out r), "null");
                T.False(CustomsWire.TryParseRequest("1|" + new string('n', 65), out r), "nonce too long");
                T.False(CustomsWire.BuildRequest("a|b", 60, 5, 256).Contains("a|b"), "separator stripped from the nonce");
            });
        }
    }
}
