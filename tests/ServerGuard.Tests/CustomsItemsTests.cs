using System.Collections.Generic;
using System.Linq;
using ValheimServerGuard.Shared;

namespace ValheimServerGuard.Tests
{
    internal static class CustomsItemsTests
    {
        internal static CustomsItem I(string prefab, int qty, int quality = 1, int variant = 0, int worldLevel = 0,
                                      long crafterId = 0, string crafter = "", string hash = "")
        {
            return new CustomsItem(prefab, quality, variant, worldLevel, crafterId, crafter, hash, qty);
        }

        internal static List<CustomsItem> L(params CustomsItem[] items)
        {
            return new List<CustomsItem>(items);
        }

        private static int Qty(IEnumerable<CustomsItem> items, string prefab)
        {
            return items.Where(i => i.Prefab == prefab).Sum(i => i.Quantity);
        }

        public static void Run()
        {
            T.Section("Customs items: identity, normalisation, deltas");

            T.Run("stacks of one identity merge and their quantities add up", () =>
            {
                var n = CustomsItems.Normalize(L(I("Wood", 50), I("Stone", 3), I("Wood", 20)));
                T.Equal(2, n.Count, "distinct identities");
                T.Equal(70, Qty(n, "Wood"), "Wood");
            });

            T.Run("slot layout does not matter: shuffled inventories normalise and sign identically", () =>
            {
                var a = L(I("Wood", 10), I("SwordIron", 1, quality: 3), I("Wood", 5), I("Coins", 99));
                var b = L(I("Coins", 99), I("Wood", 5), I("SwordIron", 1, quality: 3), I("Wood", 10));
                var na = CustomsItems.Normalize(a);
                var nb = CustomsItems.Normalize(b);
                T.Equal(na.Count, nb.Count, "count");
                for (int i = 0; i < na.Count; i++) T.Equal(na[i].IdentityKey() + "#" + na[i].Quantity, nb[i].IdentityKey() + "#" + nb[i].Quantity, "row " + i);
                T.Equal(CustomsItems.Signature(a), CustomsItems.Signature(b), "signature");
            });

            T.Run("empty stacks and nulls are dropped", () =>
            {
                var n = CustomsItems.Normalize(new List<CustomsItem> { I("Wood", 0), null, I("Stone", -3), I("Resin", 1) });
                T.Equal(1, n.Count, "only Resin survives");
                T.Equal(0, CustomsItems.Normalize(null).Count, "null input");
            });

            T.Run("every identity field separates items", () =>
            {
                var baseItem = I("SwordIron", 1, quality: 2, variant: 1, worldLevel: 3, crafterId: 42, crafter: "Ragnar", hash: new string('a', 64));
                var variants = new[]
                {
                    I("SwordBronze", 1, 2, 1, 3, 42, "Ragnar", new string('a', 64)),
                    I("SwordIron", 1, 3, 1, 3, 42, "Ragnar", new string('a', 64)),
                    I("SwordIron", 1, 2, 2, 3, 42, "Ragnar", new string('a', 64)),
                    I("SwordIron", 1, 2, 1, 4, 42, "Ragnar", new string('a', 64)),
                    I("SwordIron", 1, 2, 1, 3, 43, "Ragnar", new string('a', 64)),
                    I("SwordIron", 1, 2, 1, 3, 42, "Bjorn", new string('a', 64)),
                    I("SwordIron", 1, 2, 1, 3, 42, "Ragnar", new string('b', 64)),
                };
                foreach (var v in variants)
                    T.True(v.IdentityKey() != baseItem.IdentityKey(), "distinct from base: " + v.IdentityKey());
                T.Equal(baseItem.IdentityKey(), I("SwordIron", 7, 2, 1, 3, 42, "Ragnar", new string('a', 64)).IdentityKey(), "quantity is not identity");
            });

            T.Run("a separator inside a name cannot forge another identity", () =>
            {
                T.True(I("A|1", 1).IdentityKey() != I("A", 1, quality: 1).IdentityKey(), "prefab containing a separator");
                T.True(I("X", 1, crafter: "ab|0|").IdentityKey() != I("X", 1, crafter: "ab").IdentityKey(), "crafter containing separators");
                T.True(I("ab", 1, crafter: "c").IdentityKey() != I("a", 1, crafter: "bc").IdentityKey(), "boundary shift between fields");
            });

            T.Run("positive delta: more of something, or something new", () =>
            {
                var left = L(I("Wood", 20), I("Stone", 5));
                var back = L(I("Wood", 35), I("Stone", 5), I("BlackMetal", 10));
                var d = CustomsItems.PositiveDelta(left, back);
                T.Equal(2, d.Count, "two rows");
                T.Equal(15, Qty(d, "Wood"), "extra wood");
                T.Equal(10, Qty(d, "BlackMetal"), "new black metal");
            });

            T.Run("positive delta ignores what was used up, dropped or lost", () =>
            {
                var left = L(I("Wood", 20), I("Stone", 5), I("MeadHealthMinor", 3));
                var back = L(I("Wood", 2));
                T.Equal(0, CustomsItems.PositiveDelta(left, back).Count, "arriving with less is fine");
                T.Equal(0, CustomsItems.PositiveDelta(left, new List<CustomsItem>()).Count, "empty arrival");
            });

            T.Run("an item upgraded somewhere else shows up as new", () =>
            {
                var left = L(I("SwordIron", 1, quality: 1));
                var back = L(I("SwordIron", 1, quality: 4));
                var d = CustomsItems.PositiveDelta(left, back);
                T.Equal(1, d.Count, "one row");
                T.Equal(4, d[0].Quality, "the quality-4 sword is the undeclared one");
            });

            T.Run("changed custom data (modded item state) shows up as new", () =>
            {
                var plain = new Dictionary<string, string>();
                var magic = new Dictionary<string, string> { { "mod.magic", "{\"effects\":[\"x\"]}" } };
                var left = L(I("AxeIron", 1, hash: CustomsItems.HashCustomData(plain)));
                var back = L(I("AxeIron", 1, hash: CustomsItems.HashCustomData(magic)));
                T.Equal(1, CustomsItems.PositiveDelta(left, back).Count, "different data, different item");
            });

            T.Run("max-merge takes the larger count per identity and never double counts", () =>
            {
                // As loaded: the out-of-bounds stack is still there. After spawn it has
                // been dropped, and a migrated quick-slot item has appeared.
                var loaded  = L(I("Wood", 30), I("BlackMetal", 10), I("Coins", 5));
                var spawned = L(I("Wood", 30), I("Coins", 5), I("Tankard", 1));
                var merged = CustomsItems.MaxMerge(loaded, spawned);
                T.Equal(30, Qty(merged, "Wood"), "wood not doubled");
                T.Equal(10, Qty(merged, "BlackMetal"), "dropped at spawn, still declared");
                T.Equal(1, Qty(merged, "Tankard"), "moved in at spawn, declared");
                T.Equal(5, Qty(merged, "Coins"), "coins not doubled");
                T.Equal(CustomsItems.Signature(merged), CustomsItems.Signature(CustomsItems.MaxMerge(spawned, loaded)), "symmetric");
            });

            T.Run("signature changes with any quantity change", () =>
            {
                T.True(CustomsItems.Signature(L(I("Wood", 10))) != CustomsItems.Signature(L(I("Wood", 11))), "10 vs 11");
                T.Equal(CustomsItems.Signature(L()), CustomsItems.Signature(null), "empty and null agree");
            });

            T.Run("custom-data hash is order-independent and length-prefixed", () =>
            {
                var a = new Dictionary<string, string> { { "x", "1" }, { "y", "2" } };
                var b = new Dictionary<string, string> { { "y", "2" }, { "x", "1" } };
                T.Equal(CustomsItems.HashCustomData(a), CustomsItems.HashCustomData(b), "insertion order");
                T.True(CustomsItems.HashCustomData(new Dictionary<string, string> { { "ab", "c" } })
                    != CustomsItems.HashCustomData(new Dictionary<string, string> { { "a", "bc" } }), "boundary shift");
                T.Equal("", CustomsItems.HashCustomData(new Dictionary<string, string>()), "empty");
                T.Equal("", CustomsItems.HashCustomData(null), "null");
                T.Equal(64, CustomsItems.HashCustomData(a).Length, "sha-256 hex");
            });

            T.Run("summaries show prefab names only, capped, and safe for a chat post", () =>
            {
                var items = L(I("Wood", 40), I("SwordIron", 1, quality: 4, crafter: "@everyone"), I("Stone", 2), I("Resin", 9));
                var s = CustomsItems.Summarize(items, 2);
                T.Equal("Resin x9, Stone x2, +2 more", s, "sorted, capped");
                T.False(s.Contains("everyone"), "no crafter names");
                T.Equal("(nothing)", CustomsItems.Summarize(L(), 5), "empty");
                T.Equal("Evil", CustomsItems.DisplayName("**@Evil`<>"), "markdown and mention characters removed");
                T.Equal("?", CustomsItems.DisplayName("☃"), "nothing printable left");
            });

            T.Run("client-side cleaning removes control characters and never splits a surrogate pair", () =>
            {
                T.Equal("Bob Jr", CustomsItems.Clean("  Bob\n\u0000 Jr\t ", 64), "control characters gone, trimmed");
                var face = "\U0001F600";
                var cut = CustomsItems.Clean("ab" + face, 3);
                T.Equal("ab", cut, "surrogate pair not split");
                T.Equal("", CustomsItems.Clean(null, 10), "null");
            });

            T.Run("ignored prefabs are removed", () =>
            {
                var ignored = new HashSet<string>(new[] { "Coins" }, System.StringComparer.OrdinalIgnoreCase);
                var kept = CustomsItems.Without(L(I("coins", 5), I("Wood", 1)), ignored);
                T.Equal(1, kept.Count, "only Wood kept");
                T.Equal("Wood", kept[0].Prefab, "Wood");
            });

            T.Run("aggregation cannot overflow an int", () =>
            {
                var n = CustomsItems.Normalize(L(I("Coins", int.MaxValue), I("Coins", int.MaxValue)));
                T.Equal(int.MaxValue, n[0].Quantity, "clamped");
                T.Equal(2L * int.MaxValue, CustomsItems.Total(L(I("A", int.MaxValue), I("B", int.MaxValue))), "total is a long");
            });

            T.Run("client preparation: stacks merge first, then each record is clamped to the server's bounds", () =>
            {
                // A stack-size mod: 40 stacks of 50,000 are 2,000,000 of one identity.
                var raw = new List<CustomsItem>();
                for (int i = 0; i < 40; i++) raw.Add(I("Wood", 50000));
                raw.Add(I("Sword\u0007Iron " + new string('x', 200), 1, quality: 5000, variant: -3, worldLevel: 99999, crafter: "Ra\ngnar"));
                raw.Add(I("\u0001\u0002", 3));
                raw.Add(I("Stone", 0));
                var prepared = CustomsItems.ForReport(raw);
                T.Equal(CustomsLimits.MaxQuantity, Qty(prepared, "Wood"), "merged total clamped to the per-record maximum");
                T.Equal(3, Qty(prepared, "UnknownItem"), "a name with nothing printable becomes a placeholder");
                T.Equal(0, Qty(prepared, "Stone"), "empty stacks dropped");
                var sword = prepared.First(x => x.Prefab.StartsWith("SwordIron"));
                T.Equal(CustomsLimits.MaxPrefabLength, sword.Prefab.Length, "prefab cut to length");
                T.Equal(CustomsLimits.MaxQuality, sword.Quality, "quality clamped");
                T.Equal(0, sword.Variant, "variant clamped");
                T.Equal(CustomsLimits.MaxWorldLevel, sword.WorldLevel, "world level clamped");
                T.Equal("Ragnar", sword.CrafterName, "crafter cleaned");
            });

            T.Run("player names are reduced to plain text before a log line or a Discord post", () =>
            {
                T.Equal("everyone", CustomsItems.SafeName("@everyone"), "no mention");
                T.Equal("Bob", CustomsItems.SafeName("`**Bob**`"), "no markdown");
                T.Equal("role 123", CustomsItems.SafeName("<@&role 123>"), "no role ping syntax");
                T.Equal("Björn Jr.", CustomsItems.SafeName("Björn Jr."), "letters of any script kept");
                T.Equal("?", CustomsItems.SafeName("@@@"), "nothing left");
                T.Equal(CustomsLimits.MaxNameLength, CustomsItems.SafeName(new string('n', 500)).Length, "bounded");
            });
        }
    }
}
