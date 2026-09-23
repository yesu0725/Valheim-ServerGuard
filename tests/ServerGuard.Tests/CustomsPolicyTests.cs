using System;
using System.Collections.Generic;
using ValheimServerGuard.Shared;
using static ValheimServerGuard.Tests.CustomsItemsTests;

namespace ValheimServerGuard.Tests
{
    internal static class CustomsPolicyTests
    {
        private static readonly HashSet<string> NoIgnores = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static CustomsJudgement Judge(CustomsMode mode, CustomsNewCharacters nc, List<CustomsItem> baseline,
                                              List<CustomsItem> declared, bool approved = false, ICollection<string> ignored = null)
        {
            return CustomsPolicy.JudgeArrival(mode, nc, baseline != null, baseline, declared, ignored ?? NoIgnores, approved);
        }

        public static void Run()
        {
            T.Section("Customs policy: modes and scope");

            T.Run("enableCustoms: false means disabled, whatever the mode says", () =>
            {
                foreach (var m in new[] { "enforce", "dryrun", "", null, "garbage" })
                    T.Equal(CustomsMode.Disabled, CustomsPolicy.Resolve(false, m, true), "mode '" + m + "'");
            });

            T.Run("an unrecognised mode falls back to dryrun, never to enforce", () =>
            {
                foreach (var m in new[] { "", null, "enforced", "ENFORCE!", "strict", "kick", "yes" })
                    T.Equal(CustomsMode.DryRun, CustomsPolicy.Resolve(true, m, true), "mode '" + m + "'");
                T.Equal(CustomsMode.Enforce, CustomsPolicy.Resolve(true, " Enforce ", true), "case and spaces");
                T.Equal(CustomsMode.Disabled, CustomsPolicy.Resolve(true, "off", true), "off");
            });

            T.Run("the global log-only switch (enforce: false) downgrades enforce to dryrun", () =>
            {
                T.Equal(CustomsMode.DryRun, CustomsPolicy.Resolve(true, "enforce", false), "no kicks while ServerGuard is log-only");
            });

            T.Run("an unrecognised new-character policy falls back to fresh", () =>
            {
                T.Equal(CustomsNewCharacters.Fresh, CustomsPolicy.ParseNewCharacters("everyone"), "garbage");
                T.Equal(CustomsNewCharacters.Any, CustomsPolicy.ParseNewCharacters("ANY"), "any");
                T.Equal(CustomsNewCharacters.Approve, CustomsPolicy.ParseNewCharacters("approve"), "approve");
            });

            T.Run("who is inspected: players, moderators unless exempt, never owners, nobody when disabled", () =>
            {
                const string steam = "76561198000000001";
                T.True(CustomsPolicy.Inspects(CustomsMode.DryRun, steam, false, false, false), "player");
                T.True(CustomsPolicy.Inspects(CustomsMode.Enforce, steam, false, true, false), "moderator");
                T.False(CustomsPolicy.Inspects(CustomsMode.Enforce, steam, false, true, true), "exempt moderator");
                T.False(CustomsPolicy.Inspects(CustomsMode.Enforce, steam, true, false, false), "owner");
                T.False(CustomsPolicy.Inspects(CustomsMode.Enforce, steam, true, true, false), "owner who is also listed as a moderator");
                foreach (var owner in new[] { false, true })
                    foreach (var mod in new[] { false, true })
                        T.False(CustomsPolicy.Inspects(CustomsMode.Disabled, steam, owner, mod, false), "disabled: owner=" + owner + " moderator=" + mod);
            });

            T.Run("a peer without a SteamID64 is never inspected: there is no key for its baseline", () =>
            {
                foreach (var id in new[] { "UNKNOWN", "", null, "Xbox_2535400000000000", "7656119800000000" })
                    T.False(CustomsPolicy.Inspects(CustomsMode.Enforce, id, false, false, false), "'" + id + "'");
            });

            T.Run("hot reload: start, keep or release a session", () =>
            {
                T.Equal(CustomsReconcileAction.Enrol, CustomsPolicy.Reconcile(false, true), "newly in scope");
                T.Equal(CustomsReconcileAction.Keep, CustomsPolicy.Reconcile(true, true), "still in scope");
                T.Equal(CustomsReconcileAction.Release, CustomsPolicy.Reconcile(true, false), "disabled or exempted");
                T.Equal(CustomsReconcileAction.Keep, CustomsPolicy.Reconcile(false, false), "never inspected");
            });

            T.Run("a missing or unusable declaration: enforce refuses, dryrun logs, disabled ignores", () =>
            {
                T.Equal(CustomsFallback.Refuse, CustomsPolicy.ForUnusable(CustomsMode.Enforce), "enforce");
                T.Equal(CustomsFallback.Log, CustomsPolicy.ForUnusable(CustomsMode.DryRun), "dryrun");
                T.Equal(CustomsFallback.Ignore, CustomsPolicy.ForUnusable(CustomsMode.Disabled), "disabled");
            });

            T.Section("Customs policy: the arrival verdict table");

            var baseline = L(I("Wood", 20), I("SwordIron", 1, quality: 2));

            T.Run("nothing new is cleared in every mode", () =>
            {
                foreach (var mode in new[] { CustomsMode.DryRun, CustomsMode.Enforce })
                {
                    var j = Judge(mode, CustomsNewCharacters.Fresh, baseline, L(I("Wood", 5)));
                    T.Equal(CustomsVerdict.Cleared, j.Verdict, CustomsPolicy.Name(mode));
                    T.Equal(CustomsFinding.None, j.Finding, "no finding");
                }
            });

            T.Run("undeclared items: dryrun flags, enforce refuses", () =>
            {
                var arrival = L(I("Wood", 20), I("SwordIron", 1, quality: 2), I("BlackMetal", 30));
                var dry = Judge(CustomsMode.DryRun, CustomsNewCharacters.Fresh, baseline, arrival);
                T.Equal(CustomsVerdict.Flagged, dry.Verdict, "dryrun");
                T.True(dry.Admits, "dryrun admits");
                T.Equal(CustomsFinding.UndeclaredItems, dry.Finding, "finding");
                T.Equal(30L, CustomsItems.Total(dry.Delta), "delta is the black metal");
                var enforce = Judge(CustomsMode.Enforce, CustomsNewCharacters.Fresh, baseline, arrival);
                T.Equal(CustomsVerdict.Refused, enforce.Verdict, "enforce");
                T.False(enforce.Admits, "enforce refuses");
            });

            T.Run("an approval turns an enforce refusal into an admission", () =>
            {
                var arrival = L(I("BlackMetal", 30));
                T.Equal(CustomsVerdict.Approved, Judge(CustomsMode.Enforce, CustomsNewCharacters.Fresh, baseline, arrival, approved: true).Verdict, "approved");
                T.Equal(CustomsVerdict.Cleared, Judge(CustomsMode.Enforce, CustomsNewCharacters.Fresh, baseline, L(I("Wood", 1)), approved: true).Verdict,
                    "an approval is not needed - and so not spent - on a clean arrival");
                T.Equal(CustomsVerdict.Flagged, Judge(CustomsMode.DryRun, CustomsNewCharacters.Fresh, baseline, arrival, approved: true).Verdict,
                    "dryrun never needs one");
            });

            T.Run("new characters: fresh admits only an empty inventory in enforce", () =>
            {
                T.Equal(CustomsVerdict.Established, Judge(CustomsMode.Enforce, CustomsNewCharacters.Fresh, null, L()).Verdict, "empty");
                var carrying = Judge(CustomsMode.Enforce, CustomsNewCharacters.Fresh, null, L(I("Wood", 1)));
                T.Equal(CustomsVerdict.Refused, carrying.Verdict, "carrying anything");
                T.Equal(CustomsFinding.UnknownCharacter, carrying.Finding, "finding");
                T.Equal(CustomsVerdict.Approved, Judge(CustomsMode.Enforce, CustomsNewCharacters.Fresh, null, L(I("Wood", 1)), approved: true).Verdict, "approved");
            });

            T.Run("new characters: any admits whatever the first arrival carries", () =>
            {
                T.Equal(CustomsVerdict.Established, Judge(CustomsMode.Enforce, CustomsNewCharacters.Any, null, L(I("BlackMetal", 99))).Verdict, "any");
            });

            T.Run("new characters: approve admits nobody without an approval, not even an empty one", () =>
            {
                T.Equal(CustomsVerdict.Refused, Judge(CustomsMode.Enforce, CustomsNewCharacters.Approve, null, L()).Verdict, "empty");
                T.Equal(CustomsVerdict.Approved, Judge(CustomsMode.Enforce, CustomsNewCharacters.Approve, null, L(), approved: true).Verdict, "approved");
            });

            T.Run("dryrun establishes every new character, and flags the ones enforce would refuse", () =>
            {
                T.Equal(CustomsVerdict.Established, Judge(CustomsMode.DryRun, CustomsNewCharacters.Fresh, null, L()).Verdict, "fresh and empty");
                var flagged = Judge(CustomsMode.DryRun, CustomsNewCharacters.Fresh, null, L(I("Wood", 3)));
                T.Equal(CustomsVerdict.Flagged, flagged.Verdict, "carrying");
                T.True(flagged.Admits, "still admitted");
                T.Equal(3L, CustomsItems.Total(flagged.Delta), "reports what it carried");
            });

            T.Run("ignored items never count: not as a delta, not against a fresh character", () =>
            {
                var ignored = new HashSet<string>(new[] { "Coins" }, StringComparer.OrdinalIgnoreCase);
                T.Equal(CustomsVerdict.Cleared, Judge(CustomsMode.Enforce, CustomsNewCharacters.Fresh, baseline, L(I("Coins", 999)), ignored: ignored).Verdict, "delta");
                T.Equal(CustomsVerdict.Established, Judge(CustomsMode.Enforce, CustomsNewCharacters.Fresh, null, L(I("Coins", 999)), ignored: ignored).Verdict, "fresh");
            });

            T.Run("disabled mode never refuses, even if asked to judge", () =>
            {
                T.True(Judge(CustomsMode.Disabled, CustomsNewCharacters.Approve, baseline, L(I("BlackMetal", 1))).Admits, "with a baseline");
                T.True(Judge(CustomsMode.Disabled, CustomsNewCharacters.Approve, null, L(I("BlackMetal", 1))).Admits, "without one");
            });
        }
    }
}
