using System;
using System.Collections.Generic;
using System.IO;
using ValheimServerGuard.Shared;
using static ValheimServerGuard.Tests.CustomsItemsTests;
using static ValheimServerGuard.Tests.CustomsWireTests;

namespace ValheimServerGuard.Tests
{
    internal static class CustomsSessionTests
    {
        internal static readonly DateTime T0 = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        internal const string Steam = "76561198000000001";
        private static readonly HashSet<string> NoIgnores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static CustomsSession NewSession(bool judgesArrival = true)
        {
            return new CustomsSession(Steam, Nonce, judgesArrival, T0);
        }

        // Reports are spaced ten seconds apart by sequence, so the rate limit never
        // interferes unless a test is about the rate limit.
        private static CustomsScreenResult Send(CustomsSession s, CustomsReport r, string connectionName = "Ragnar")
        {
            return s.Screen(CustomsWire.WriteReport(r), new CustomsLimits(256), connectionName, T0.AddSeconds(r.Sequence * 10));
        }

        private static CustomsJudgement Declare(CustomsSession s, CustomsBaselineStore store, CustomsMode mode,
                                                CustomsReport declaration, CustomsNewCharacters nc = CustomsNewCharacters.Fresh,
                                                CustomsApprovals approvals = null)
        {
            var screen = Send(s, declaration);
            T.Equal(CustomsScreen.Accepted, screen.Outcome, "declaration accepted (" + screen.Detail + ")");
            return CustomsEngine.Declare(s, screen.Report, store, mode, nc, NoIgnores, approvals, T0.AddSeconds(declaration.Sequence * 10));
        }

        private static bool Update(CustomsSession s, CustomsBaselineStore store, CustomsReport r)
        {
            var screen = Send(s, r);
            return screen.Outcome == CustomsScreen.Accepted && CustomsEngine.Record(s, screen.Report, store, T0.AddSeconds(r.Sequence * 10));
        }

        // A store holding one flushed baseline for (Steam, CharId).
        private static CustomsBaselineStore StoreWith(params CustomsItem[] items)
        {
            var store = new CustomsBaselineStore(T.TempDir());
            store.Put(new CustomsBaseline { SteamId = Steam, CharacterId = CharId, CharacterName = "Ragnar", EstablishedUtc = T0, UpdatedUtc = T0, Origin = "logout", Items = L(items) });
            T.Equal(1, store.Flush(T0, true), "seeded baseline written");
            return store;
        }

        private static byte[] FileBytes(CustomsBaselineStore store)
        {
            var path = store.PathFor(Steam, CharId);
            return File.Exists(path) ? File.ReadAllBytes(path) : new byte[0];
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static List<CustomsItem> Stored(CustomsBaselineStore store)
        {
            CustomsBaseline b;
            string note;
            return store.TryGet(Steam, CharId, out b, out note, false) == CustomsLookup.Found ? b.Items : null;
        }

        public static void Run()
        {
            T.Section("Customs sessions: replay, order and binding");

            T.Run("the declaration answering the current request is accepted", () =>
            {
                var s = NewSession();
                var r = Send(s, Report(CustomsReportKind.Declare, 1, I("Wood", 1)));
                T.Equal(CustomsScreen.Accepted, r.Outcome, "accepted");
                T.Equal(1L, s.LastSequence, "sequence bound");
            });

            T.Run("a report answering a different request is dropped, not held against the player", () =>
            {
                var s = NewSession();
                var stale = Report(CustomsReportKind.Declare, 1, I("Wood", 1));
                stale.Nonce = "some-older-request";
                var r = Send(s, stale);
                T.Equal(CustomsScreen.Dropped, r.Outcome, "dropped");
                T.Equal(0, s.Rejected, "not a rejection");
                T.Equal(CustomsPhase.Declaring, s.Phase, "still waiting for the real declaration");
                T.Equal(0L, s.LastSequence, "sequence untouched");
            });

            T.Run("replayed and out-of-order reports are dropped", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var s = NewSession();
                Declare(s, store, CustomsMode.DryRun, Report(CustomsReportKind.Declare, 1));
                T.Equal(CustomsScreen.Accepted, Send(s, Report(CustomsReportKind.Change, 3, I("Wood", 1))).Outcome, "seq 3");
                T.Equal(CustomsScreen.Dropped, Send(s, Report(CustomsReportKind.Change, 3, I("Wood", 1))).Outcome, "seq 3 again");
                T.Equal(CustomsScreen.Dropped, Send(s, Report(CustomsReportKind.Change, 2, I("Wood", 1))).Outcome, "seq 2 after 3");
                T.Equal(3L, s.LastSequence, "high-water mark");
            });

            T.Run("an update before the declaration is a protocol violation", () =>
            {
                var s = NewSession();
                var r = Send(s, Report(CustomsReportKind.Change, 1, I("Wood", 1)));
                T.Equal(CustomsScreen.Rejected, r.Outcome, "rejected");
                T.Equal(CustomsProblem.WrongKind, r.Problem, "wrong kind");
            });

            T.Run("a second declaration on an admitted session is dropped", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var s = NewSession();
                Declare(s, store, CustomsMode.DryRun, Report(CustomsReportKind.Declare, 1));
                T.Equal(CustomsScreen.Dropped, Send(s, Report(CustomsReportKind.Declare, 2, I("BlackMetal", 50))).Outcome, "dropped");
            });

            T.Run("reports must be about the character this connection logged in as", () =>
            {
                var s = NewSession();
                var r = Send(s, Report(CustomsReportKind.Declare, 1), "Bjorn");
                T.Equal(CustomsProblem.CharacterMismatch, r.Problem, "different name");
                T.Equal(CustomsScreen.Accepted, Send(NewSession(), Report(CustomsReportKind.Declare, 1), "RAGNAR").Outcome, "case-insensitive");
                T.Equal(CustomsScreen.Accepted, Send(NewSession(), Report(CustomsReportKind.Declare, 1), "").Outcome, "no name known yet");
                var hostile = Report(CustomsReportKind.Declare, 1);
                hostile.CharacterName = "@everyone";
                var h = Send(NewSession(), hostile);
                T.Equal(CustomsProblem.CharacterMismatch, h.Problem, "a name that is not the connection's");
                T.False(h.Detail.Contains("@"), "the declared name reaches the detail as plain text: " + h.Detail);
            });

            T.Run("switching character mid-session is refused", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var s = NewSession();
                Declare(s, store, CustomsMode.DryRun, Report(CustomsReportKind.Declare, 1));
                var other = Report(CustomsReportKind.Change, 2, I("BlackMetal", 99));
                other.CharacterId = "777";
                var r = Send(s, other);
                T.Equal(CustomsProblem.CharacterSwitched, r.Problem, "switched");
                CustomsBaseline b;
                string note;
                T.Equal(CustomsLookup.NotFound, store.TryGet(Steam, "777", out b, out note, false), "the other character got no baseline");
            });

            T.Run("rate limit: a burst, then one report per two seconds", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var s = NewSession();
                var limits = new CustomsLimits(256);
                Func<long, DateTime, CustomsScreen> send = (seq, at) =>
                    s.Screen(CustomsWire.WriteReport(Report(seq == 1 ? CustomsReportKind.Declare : CustomsReportKind.Change, seq)), limits, "Ragnar", at).Outcome;
                T.Equal(CustomsScreen.Accepted, send(1, T0), "1");
                CustomsEngine.Declare(s, Report(CustomsReportKind.Declare, 1), store, CustomsMode.DryRun, CustomsNewCharacters.Any, NoIgnores, null, T0);
                for (long seq = 2; seq <= 5; seq++) T.Equal(CustomsScreen.Accepted, send(seq, T0), "burst " + seq);
                T.Equal(CustomsScreen.Dropped, send(6, T0), "6th in the same instant");
                T.Equal(CustomsScreen.Dropped, send(6, T0.AddSeconds(1)), "one second later");
                T.Equal(CustomsScreen.Accepted, send(6, T0.AddSeconds(2.1)), "after the refill");
            });

            T.Run("a closed session drops everything", () =>
            {
                var s = NewSession();
                s.Close("test");
                T.Equal(CustomsScreen.Dropped, Send(s, Report(CustomsReportKind.Declare, 1)).Outcome, "dropped");
            });

            T.Run("the declaration deadline starts when the character is in the world", () =>
            {
                var s = NewSession();
                T.False(s.IsOverdue(T0.AddHours(1)), "no deadline while loading");
                s.ArmDeadline(T0.AddMinutes(2), 60, false);
                T.False(s.DeadlineUtc.HasValue, "not armed while the character is still loading");
                s.ArmDeadline(T0.AddMinutes(3), 60, true);
                s.ArmDeadline(T0.AddMinutes(10), 60, true);
                T.False(s.IsOverdue(T0.AddMinutes(3).AddSeconds(59)), "inside the window");
                T.True(s.IsOverdue(T0.AddMinutes(4)), "first arming wins; overdue after 60 s");
                T.False(s.DeadlineWithoutCharacter, "armed by the character");
            });

            T.Run("a client that never puts a character in the world still has to declare", () =>
            {
                var s = NewSession();
                s.ArmDeadline(T0.Add(CustomsSession.LoadingAllowance).AddSeconds(-1), 60, false);
                T.False(s.DeadlineUtc.HasValue, "still inside the loading allowance");
                s.ArmDeadline(T0.Add(CustomsSession.LoadingAllowance), 60, false);
                T.True(s.DeadlineUtc.HasValue && s.DeadlineWithoutCharacter, "armed without a character");
                T.False(s.IsOverdue(T0.Add(CustomsSession.LoadingAllowance).AddSeconds(59)), "the usual timeout still applies");
                T.True(s.IsOverdue(T0.Add(CustomsSession.LoadingAllowance).AddSeconds(60)), "then overdue");
                T.Equal(CustomsFallback.Refuse, CustomsEngine.Unusable(s, CustomsMode.Enforce, "timeout"), "enforce refuses");
            });

            T.Run("dryrun -> enforce: a declaration still missing is refused once enforce is on", () =>
            {
                var s = NewSession();
                s.ArmDeadline(T0, 60, true);
                T.Equal(CustomsFallback.Log, CustomsEngine.Unusable(s, CustomsMode.DryRun, "timeout"), "dryrun logs");
                T.True(s.IsOverdue(T0.AddSeconds(61)), "still overdue, still waiting");
                T.Equal(CustomsFallback.Refuse, CustomsEngine.Unusable(s, CustomsMode.Enforce, "timeout"), "enforce refuses");
                T.Equal(CustomsPhase.Closed, s.Phase, "closed");
            });

            T.Run("disabled: an unusable report or a missed deadline changes nothing", () =>
            {
                var waiting = NewSession();
                T.Equal(CustomsFallback.Ignore, CustomsEngine.Unusable(waiting, CustomsMode.Disabled, "timeout"), "ignored");
                T.Equal(CustomsPhase.Declaring, waiting.Phase, "a waiting session is untouched");
                var store = new CustomsBaselineStore(T.TempDir());
                var admitted = NewSession();
                Declare(admitted, store, CustomsMode.DryRun, Report(CustomsReportKind.Declare, 1, I("Wood", 1)), CustomsNewCharacters.Any);
                T.Equal(CustomsFallback.Ignore, CustomsEngine.Unusable(admitted, CustomsMode.Disabled, "garbage"), "ignored");
                T.Equal(CustomsPhase.Admitted, admitted.Phase, "an admitted session stays admitted");
            });

            T.Run("an overdue or unusable declaration: enforce refuses, dryrun logs and keeps waiting", () =>
            {
                var a = NewSession();
                T.Equal(CustomsFallback.Refuse, CustomsEngine.Unusable(a, CustomsMode.Enforce, "timeout"), "enforce");
                T.Equal(CustomsPhase.Closed, a.Phase, "closed");
                var b = NewSession();
                T.Equal(CustomsFallback.Log, CustomsEngine.Unusable(b, CustomsMode.DryRun, "timeout"), "dryrun");
                T.Equal(CustomsPhase.Declaring, b.Phase, "still waiting");
                T.Equal(CustomsScreen.Accepted, Send(b, Report(CustomsReportKind.Declare, 1)).Outcome, "a late declaration still counts");
            });

            T.Section("Customs engine: what reaches the baseline");

            T.Run("dryrun: a new character's first arrival becomes its baseline", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var s = NewSession();
                var j = Declare(s, store, CustomsMode.DryRun, Report(CustomsReportKind.Declare, 1, I("Wood", 12)));
                T.Equal(CustomsVerdict.Flagged, j.Verdict, "carrying items: enforce would have refused");
                T.Equal(1, store.Flush(T0, false), "written");
                T.Equal(12L, CustomsItems.Total(Stored(store)), "baseline holds the arrival");
            });

            T.Run("enforce: a refused arrival writes nothing, and nothing it sends afterwards does either", () =>
            {
                var store = StoreWith(I("Wood", 20));
                var before = FileBytes(store);
                var s = NewSession();
                var j = Declare(s, store, CustomsMode.Enforce, Report(CustomsReportKind.Declare, 1, I("Wood", 20), I("BlackMetal", 40)));
                T.Equal(CustomsVerdict.Refused, j.Verdict, "refused");
                T.Equal(CustomsPhase.Closed, s.Phase, "closed");
                T.Equal(40L, CustomsItems.Total(j.Delta), "delta");
                // The kick is on its way; the client's in-flight checkpoint and logout arrive.
                T.Equal(CustomsScreen.Dropped, Send(s, Report(CustomsReportKind.Checkpoint, 2, I("Wood", 20), I("BlackMetal", 40))).Outcome, "checkpoint dropped");
                T.Equal(CustomsScreen.Dropped, Send(s, Report(CustomsReportKind.Logout, 3, I("Wood", 20), I("BlackMetal", 40))).Outcome, "logout dropped");
                T.False(CustomsEngine.Record(s, Report(CustomsReportKind.Logout, 4, I("BlackMetal", 40)), store, T0), "even called directly");
                T.Equal(0, store.Flush(T0.AddMinutes(1), true), "nothing queued");
                T.True(Same(before, FileBytes(store)), "baseline file byte-identical");
            });

            T.Run("an unusable report stops a trusted session recording; the last good baseline stands", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var s = NewSession();
                Declare(s, store, CustomsMode.DryRun, Report(CustomsReportKind.Declare, 1, I("Wood", 5)), CustomsNewCharacters.Any);
                T.True(Update(s, store, Report(CustomsReportKind.Change, 2, I("Wood", 9))), "good change recorded");
                store.Flush(T0, true);
                var bad = s.Screen("{\"v\":1,\"n\":\"" + Nonce + "\",\"garbage", new CustomsLimits(256), "Ragnar", T0.AddSeconds(30));
                T.Equal(CustomsScreen.Rejected, bad.Outcome, "garbage rejected");
                T.Equal(CustomsFallback.Log, CustomsEngine.Unusable(s, CustomsMode.DryRun, bad.Problem.ToString()), "dryrun logs");
                T.Equal(CustomsPhase.Closed, s.Phase, "no longer trusted");
                T.False(Update(s, store, Report(CustomsReportKind.Change, 4, I("Wood", 9), I("BlackMetal", 99))), "later reports cannot write");
                store.Flush(T0.AddMinutes(1), true);
                T.Equal(9L, CustomsItems.Total(Stored(store)), "baseline is the last good report");
            });

            T.Run("enforce: an unusable declaration refuses the session and leaves the baseline alone", () =>
            {
                var store = StoreWith(I("Wood", 20));
                var before = FileBytes(store);
                var s = NewSession();
                var bad = s.Screen("[]", new CustomsLimits(256), "Ragnar", T0);
                T.Equal(CustomsScreen.Rejected, bad.Outcome, "rejected");
                T.Equal(CustomsFallback.Refuse, CustomsEngine.Unusable(s, CustomsMode.Enforce, "malformed"), "refuse");
                T.Equal(CustomsScreen.Dropped, Send(s, Report(CustomsReportKind.Declare, 1, I("Wood", 1))).Outcome, "closed for good");
                T.True(Same(before, FileBytes(store)), "baseline untouched");
            });

            T.Run("a replay cannot roll the baseline back to a richer inventory", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var s = NewSession();
                Declare(s, store, CustomsMode.Enforce, Report(CustomsReportKind.Declare, 1), CustomsNewCharacters.Fresh);
                var rich = Report(CustomsReportKind.Change, 2, I("Coins", 900));
                T.True(Update(s, store, rich), "rich state recorded (earned in-session)");
                T.True(Update(s, store, Report(CustomsReportKind.Change, 3, I("Coins", 10))), "coins spent");
                T.False(Update(s, store, rich), "replaying the rich report is dropped");
                store.Flush(T0, true);
                T.Equal(10L, CustomsItems.Total(Stored(store)), "baseline stays at the newest state");
            });

            T.Run("unchanged checkpoints do not rewrite the baseline", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var s = NewSession();
                Declare(s, store, CustomsMode.DryRun, Report(CustomsReportKind.Declare, 1, I("Wood", 5)), CustomsNewCharacters.Any);
                store.Flush(T0, true);
                T.False(Update(s, store, Report(CustomsReportKind.Checkpoint, 2, I("Wood", 5))), "same inventory");
                T.Equal(0, store.QueuedCount, "nothing queued");
            });

            T.Run("a newer connection for the same character supersedes the old one", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var old = NewSession();
                Declare(old, store, CustomsMode.DryRun, Report(CustomsReportKind.Declare, 1, I("Wood", 5)), CustomsNewCharacters.Any);
                var fresh = NewSession();
                Declare(fresh, store, CustomsMode.DryRun, Report(CustomsReportKind.Declare, 1, I("Wood", 5)), CustomsNewCharacters.Any);
                T.Equal(1, CustomsEngine.Supersede(new[] { old, fresh }, fresh), "one superseded");
                T.Equal(CustomsPhase.Closed, old.Phase, "old connection closed");
                T.Equal(CustomsPhase.Admitted, fresh.Phase, "new one kept");
                T.False(Update(old, store, Report(CustomsReportKind.Logout, 5, I("BlackMetal", 3))), "the ghost cannot write");
            });

            T.Run("enrolment mid-session records without judging, even in enforce", () =>
            {
                var store = StoreWith(I("Wood", 20));
                var s = NewSession(judgesArrival: false);
                var j = Declare(s, store, CustomsMode.Enforce, Report(CustomsReportKind.Declare, 1, I("Wood", 20), I("BlackMetal", 40)));
                T.Equal(CustomsVerdict.Enrolled, j.Verdict, "enrolled");
                T.Equal(40L, CustomsItems.Total(j.Delta), "delta reported for the log");
                T.Equal(CustomsPhase.Admitted, s.Phase, "admitted");
                store.Flush(T0, true);
                T.Equal(60L, CustomsItems.Total(Stored(store)), "current inventory is the new baseline");
            });

            T.Run("an unreadable baseline admits the player unjudged and records nothing over it", () =>
            {
                var store = StoreWith(I("Wood", 20));
                var before = FileBytes(store);
                store.ReadText = path => { throw new UnauthorizedAccessException("denied"); };
                var s = NewSession();
                var j = Declare(s, store, CustomsMode.Enforce, Report(CustomsReportKind.Declare, 1, I("BlackMetal", 40)));
                T.Equal(CustomsVerdict.Unjudged, j.Verdict, "unjudged");
                T.True(j.Admits, "admitted: a server disk problem is not the player's fault");
                T.False(s.MayRecord, "not trusted to write");
                T.False(Update(s, store, Report(CustomsReportKind.Logout, 2, I("BlackMetal", 40))), "logout not recorded");
                T.Equal(0, store.QueuedCount, "nothing queued");
                T.True(Same(before, FileBytes(store)), "file untouched");
            });

            T.Run("the mode in force when the declaration arrives is the one that judges it", () =>
            {
                var store = StoreWith(I("Wood", 20));
                var s = NewSession();   // requested while the server was in dryrun...
                var j = Declare(s, store, CustomsMode.Enforce, Report(CustomsReportKind.Declare, 1, I("BlackMetal", 1)));
                T.Equal(CustomsVerdict.Refused, j.Verdict, "...judged after a switch to enforce");
            });

            T.Run("a logout report can be written at once", () =>
            {
                var store = new CustomsBaselineStore(T.TempDir());
                var s = NewSession();
                Declare(s, store, CustomsMode.DryRun, Report(CustomsReportKind.Declare, 1), CustomsNewCharacters.Any);
                store.Flush(T0, true);
                T.True(Update(s, store, Report(CustomsReportKind.Logout, 2, I("Resin", 7))), "recorded");
                T.True(store.FlushOne(Steam, CharId, T0), "written");
                T.Equal(7L, CustomsItems.Total(Stored(store)), "on disk");
            });

            T.Section("Customs approvals");

            T.Run("an approval is spent once, on the arrival that needed it", () =>
            {
                var store = StoreWith(I("Wood", 20));
                var approvals = new CustomsApprovals(Path.Combine(T.TempDir(), "approvals.json"));
                approvals.Grant(Steam, "76561198000000009", T0);
                var clean = Declare(NewSession(), store, CustomsMode.Enforce, Report(CustomsReportKind.Declare, 1, I("Wood", 1)), approvals: approvals);
                T.Equal(CustomsVerdict.Cleared, clean.Verdict, "clean arrival");
                T.True(approvals.IsActive(Steam, T0), "approval not spent on it");
                var smuggled = Declare(NewSession(), store, CustomsMode.Enforce, Report(CustomsReportKind.Declare, 1, I("BlackMetal", 40)), approvals: approvals);
                T.Equal(CustomsVerdict.Approved, smuggled.Verdict, "approved");
                T.False(approvals.IsActive(Steam, T0), "spent");
                var again = Declare(NewSession(), StoreWith(I("Wood", 20)), CustomsMode.Enforce, Report(CustomsReportKind.Declare, 1, I("BlackMetal", 40)), approvals: approvals);
                T.Equal(CustomsVerdict.Refused, again.Verdict, "no standing bypass");
            });

            T.Run("an approved arrival re-baselines the character", () =>
            {
                var store = StoreWith(I("Wood", 20));
                var approvals = new CustomsApprovals(Path.Combine(T.TempDir(), "approvals.json"));
                approvals.Grant(Steam, "op", T0);
                Declare(NewSession(), store, CustomsMode.Enforce, Report(CustomsReportKind.Declare, 1, I("BlackMetal", 40)), approvals: approvals);
                store.Flush(T0, true);
                T.Equal(40L, CustomsItems.Total(Stored(store)), "baseline is the approved arrival");
            });

            T.Run("approvals persist, expire, and fail closed when unreadable", () =>
            {
                var path = Path.Combine(T.TempDir(), "approvals.json");
                var a = new CustomsApprovals(path);
                a.Grant(Steam, "op", T0);
                a.Grant("76561198000000002", "op", T0.AddHours(-30));
                a.Grant("not-a-steam-id", "op", T0);
                T.True(a.Save(), "saved");
                var b = new CustomsApprovals(path);
                b.Load(T0);
                T.True(b.IsActive(Steam, T0), "persisted");
                T.False(b.IsActive("76561198000000002", T0), "expired one dropped on load");
                T.Equal(1, b.Active(T0).Count, "invalid id never stored");
                T.False(b.IsActive(Steam, T0.Add(CustomsApprovals.Lifetime).AddSeconds(1)), "expires after its lifetime");
                File.WriteAllText(path, "{ this is not json");
                var c = new CustomsApprovals(path);
                c.Load(T0);
                T.Equal(0, c.Active(T0).Count, "unreadable file: no approvals");
                T.True(c.LastError.Length > 0, "and the error is reported");
            });
        }
    }
}
