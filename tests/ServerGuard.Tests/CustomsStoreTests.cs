using System;
using System.Collections.Generic;
using System.IO;
using ValheimServerGuard.Shared;
using static ValheimServerGuard.Tests.CustomsItemsTests;
using static ValheimServerGuard.Tests.CustomsSessionTests;
using static ValheimServerGuard.Tests.CustomsWireTests;

namespace ValheimServerGuard.Tests
{
    internal static class CustomsStoreTests
    {
        private static CustomsBaseline B(params CustomsItem[] items)
        {
            return new CustomsBaseline
            {
                SteamId = Steam, CharacterId = CharId, CharacterName = "Ragnar",
                EstablishedUtc = T0, UpdatedUtc = T0.AddMinutes(5), Origin = "change", Items = L(items),
            };
        }

        private static CustomsLookup Get(CustomsBaselineStore store, out CustomsBaseline b, bool repair = true)
        {
            string note;
            return store.TryGet(Steam, CharId, out b, out note, repair);
        }

        private static int Count(CustomsBaselineStore store, string pattern)
        {
            var dir = Path.Combine(store.Root, Steam);
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Length : 0;
        }

        public static void Run()
        {
            T.Section("Customs baseline store: durability and recovery");

            T.Run("a baseline survives the write and reads back identical", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                store.Put(B(I("Wood", 20), I("SwordIron", 1, 3, 1, 2, 42, "Ragnar", new string('c', 64))));
                T.Equal(1, store.Flush(T0, false), "one file written");
                CustomsBaseline back;
                T.Equal(CustomsLookup.Found, Get(new CustomsBaselineStore(store.Root), out back), "found by a fresh store");
                T.Equal(CustomsItems.Signature(B(I("Wood", 20), I("SwordIron", 1, 3, 1, 2, 42, "Ragnar", new string('c', 64))).Items), CustomsItems.Signature(back.Items), "same items");
                T.Equal("Ragnar", back.CharacterName, "name");
                T.Equal("change", back.Origin, "origin");
                T.Equal(T0, back.EstablishedUtc, "established");
                T.Equal(T0.AddMinutes(5), back.UpdatedUtc, "updated");
            });

            T.Run("each write keeps the previous version as .bak", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                store.Put(B(I("Wood", 1))); store.Flush(T0, true);
                store.Put(B(I("Wood", 2))); store.Flush(T0, true);
                CustomsBaseline bak;
                T.True(CustomsBaselineStore.TryParse(File.ReadAllText(store.PathFor(Steam, CharId) + ".bak"), Steam, CharId, out bak, out _), ".bak parses");
                T.Equal(1L, CustomsItems.Total(bak.Items), ".bak is the previous version");
                T.Equal(0, Count(store, "*.tmp"), "no temp file left behind");
            });

            T.Run("a corrupt file is kept aside as evidence and the .bak takes over", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                store.Put(B(I("Wood", 1))); store.Flush(T0, true);
                store.Put(B(I("Wood", 2))); store.Flush(T0, true);
                var path = store.PathFor(Steam, CharId);
                File.WriteAllText(path, "{\"schema\":1,\"items\":[[\"Wood\",1,0,");
                CustomsBaseline b;
                T.Equal(CustomsLookup.Found, Get(store, out b), "found via .bak");
                T.Equal(1L, CustomsItems.Total(b.Items), "the previous version");
                T.Equal(1, Count(store, "*.corrupt-*"), "corrupt file preserved");
                T.True(store.IsQueued(Steam, CharId), "queued to restore the primary");
                T.Equal(1, store.Flush(T0, true), "restored");
                T.Equal(CustomsLookup.Found, Get(new CustomsBaselineStore(store.Root), out b), "primary readable again");
            });

            T.Run("with no readable copy the character has no baseline, and both copies are kept", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                store.Put(B(I("Wood", 1))); store.Flush(T0, true);
                store.Put(B(I("Wood", 2))); store.Flush(T0, true);
                var path = store.PathFor(Steam, CharId);
                File.WriteAllText(path, "garbage");
                File.WriteAllText(path + ".bak", "more garbage");
                CustomsBaseline b;
                T.Equal(CustomsLookup.NotFound, Get(store, out b), "no baseline: the new-character rule applies");
                T.Equal(2, Count(store, "*.corrupt-*"), "both kept as evidence");
                T.Equal(0, store.QueuedCount, "nothing written in their place");
            });

            T.Run("a missing primary with a good .bak (crash inside the replace) is recovered", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                store.Put(B(I("Wood", 1))); store.Flush(T0, true);
                store.Put(B(I("Wood", 2))); store.Flush(T0, true);
                var path = store.PathFor(Steam, CharId);
                File.Delete(path);
                File.WriteAllText(path + ".tmp", "half-written");
                CustomsBaseline b;
                T.Equal(CustomsLookup.Found, Get(store, out b), "recovered");
                T.Equal(1L, CustomsItems.Total(b.Items), "from .bak");
                T.Equal(1, store.Flush(T0, true), "primary rewritten over the stale temp file");
                T.Equal(0, Count(store, "*.tmp"), "temp file gone");
            });

            T.Run("a file that cannot be read is reported unavailable, not treated as corrupt", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                store.Put(B(I("Wood", 1))); store.Flush(T0, true);
                store.ReadText = p => { throw new IOException("device error"); };
                CustomsBaseline b;
                T.Equal(CustomsLookup.Unavailable, Get(store, out b), "unavailable");
                T.Equal(0, Count(store, "*.corrupt-*"), "not moved aside");
                T.True(File.Exists(store.PathFor(Steam, CharId)), "still in place");
            });

            T.Run("a failing disk leaves the old file intact and retries with back-off", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                store.Put(B(I("Wood", 1))); store.Flush(T0, true);
                var before = File.ReadAllText(store.PathFor(Steam, CharId));
                bool diskFull = true;
                store.BeforeWrite = p => { if (diskFull) throw new IOException("disk full"); };
                store.Put(B(I("Wood", 99)));
                T.Equal(0, store.Flush(T0, false), "write failed");
                T.Equal(1, store.ConsecutiveFailures, "counted");
                T.True(store.LastError.Contains("disk full"), "error kept");
                T.True(store.IsQueued(Steam, CharId), "still queued");
                T.Equal(before, File.ReadAllText(store.PathFor(Steam, CharId)), "old file untouched");
                diskFull = false;
                T.Equal(0, store.Flush(T0.AddSeconds(1), false), "backing off");
                T.Equal(1, store.Flush(T0.AddSeconds(3), false), "retried after the back-off");
                T.Equal(0, store.ConsecutiveFailures, "reset");
                CustomsBaseline b;
                Get(store, out b);
                T.Equal(99L, CustomsItems.Total(b.Items), "new version on disk");
            });

            T.Run("a queued baseline is what a reconnecting character is judged against", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                store.Put(B(I("Wood", 1))); store.Flush(T0, true);
                store.BeforeWrite = p => { throw new IOException("slow disk"); };
                store.Put(B(I("Wood", 1), I("BlackMetal", 30)));   // departure record, not yet on disk
                store.Flush(T0, true);
                CustomsBaseline b;
                T.Equal(CustomsLookup.Found, Get(store, out b), "found");
                T.Equal(31L, CustomsItems.Total(b.Items), "the departure record, not the older file");
            });

            T.Run("a file that belongs to someone else, to no known schema, or repeats a field, is corrupt", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var other = B(I("Wood", 1));
                other.SteamId = "76561198000000002";
                var text = CustomsBaselineStore.Serialize(other);
                CustomsBaseline b;
                string error;
                T.False(CustomsBaselineStore.TryParse(text, Steam, CharId, out b, out error), "another account's file");
                T.False(CustomsBaselineStore.TryParse(CustomsBaselineStore.Serialize(B()).Replace("\"schema\":1", "\"schema\":0"), Steam, CharId, out b, out error), "schema 0");
                T.False(CustomsBaselineStore.TryParse(CustomsBaselineStore.Serialize(B(I("Wood", 1))).TrimEnd('}') + ",\"items\":[[\"BlackMetal\",1,0,0,0,\"\",\"\",99]]}",
                    Steam, CharId, out b, out error), "items given twice");
                T.False(CustomsBaselineStore.TryParse(CustomsBaselineStore.Serialize(B(I("Wood", 1))).Replace(",1]]", ",2000000]]"), Steam, CharId, out b, out error), "hand-edited quantity out of range");
                T.False(CustomsBaselineStore.TryParse(CustomsBaselineStore.Serialize(B()).Replace("\"steamId\":\"" + Steam + "\"", "\"steamId\":{\"x\":1}"), Steam, CharId, out b, out error), "structure where a value belongs");
                T.True(CustomsBaselineStore.TryParse(CustomsBaselineStore.Serialize(B(I("Wood", 1))), Steam, CharId, out b, out error), "control: the real file parses (" + error + ")");
            });

            T.Run("a file from a newer ServerGuard is left alone: unavailable, not corrupt", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var path = store.PathFor(Steam, CharId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Schema first, as the writer puts it, then a layout this build cannot read.
                var newer = "{\"schema\":2,\"items\":{\"layout\":\"new\"},\"steamId\":\"" + Steam + "\"}";
                File.WriteAllText(path, newer);
                CustomsBaseline b;
                T.Equal(CustomsLookup.Unavailable, Get(store, out b), "unavailable");
                T.Equal(0, Count(store, "*.corrupt-*"), "not moved aside");
                var session = new CustomsSession(Steam, Nonce, true, T0);
                var j = CustomsEngine.Declare(session, Report(CustomsReportKind.Declare, 1, I("BlackMetal", 40)), store,
                    CustomsMode.Enforce, CustomsNewCharacters.Fresh, new HashSet<string>(), null, T0);
                T.Equal(CustomsVerdict.Unjudged, j.Verdict, "admitted unjudged, as for any unreadable baseline");
                T.Equal(0, store.QueuedCount, "nothing queued over it");
                T.Equal(newer, File.ReadAllText(path), "file untouched");
            });

            T.Run("keys never escape the store directory", () =>
            {
                var root = T.TempDir();
                var store = new CustomsBaselineStore(Path.Combine(root, "customs"));
                foreach (var badId in new[] { "../../x", "1/../2", "..", "5.json", "" })
                {
                    CustomsBaseline b;
                    string note;
                    T.Equal(CustomsLookup.NotFound, store.TryGet(Steam, badId, out b, out note, true), "character '" + badId + "'");
                    var evil = B(I("Wood", 1));
                    evil.CharacterId = badId;
                    store.Put(evil);
                }
                foreach (var badSteam in new[] { "../76561198000000001", "7656119800000000", "00000000000000000" })
                {
                    var evil = B(I("Wood", 1));
                    evil.SteamId = badSteam;
                    store.Put(evil);
                }
                T.Equal(0, store.QueuedCount, "nothing accepted");
                T.Equal(0, store.Flush(T0, true), "nothing written");
                T.Equal(0, Directory.GetFileSystemEntries(root).Length, "nothing created at all");
            });

            T.Run("constructing a store and looking things up touches nothing on disk", () =>
            {
                var root = Path.Combine(T.TempDir(), "customs");
                var store = new CustomsBaselineStore(root);
                CustomsBaseline b;
                string note;
                T.Equal(CustomsLookup.NotFound, store.TryGet(Steam, CharId, out b, out note, true), "no baseline");
                T.Equal(0, store.List(Steam).Count, "nothing listed");
                T.Equal(0, store.Flush(T0, true), "nothing to flush");
                T.False(Directory.Exists(root), "no directory created before the first real write");
            });

            T.Run("listing is read-only and includes queued baselines", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                store.Put(B(I("Wood", 1))); store.Flush(T0, true);
                var second = B(I("Stone", 1));
                second.CharacterId = "99";
                store.Put(second);
                var corrupt = store.PathFor(Steam, "55");
                File.WriteAllText(corrupt, "garbage");
                var list = store.List(Steam);
                T.Equal(2, list.Count, "one on disk, one queued; the corrupt one skipped");
                T.True(File.Exists(corrupt), "listing does not move files");
            });

            T.Run("removing a baseline removes the file, its .bak and anything queued", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                store.Put(B(I("Wood", 1))); store.Flush(T0, true);
                store.Put(B(I("Wood", 2))); store.Flush(T0, true);
                store.Put(B(I("Wood", 3)));
                T.True(store.Remove(Steam, CharId), "removed");
                CustomsBaseline b;
                T.Equal(CustomsLookup.NotFound, Get(store, out b), "gone");
                T.Equal(0, Count(store, "*"), "no files left");
            });
        }
    }
}
