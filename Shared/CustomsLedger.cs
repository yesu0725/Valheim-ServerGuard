using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace ValheimServerGuard.Shared
{
    // ==========================================================================
    // Customs - the server's decisions and its durable record.
    //
    //   CustomsPolicy        modes, who is inspected, the arrival verdict table
    //   CustomsSession       one connection: request nonce, sequence, rate limit,
    //                        character binding, and whether it may advance a baseline
    //   CustomsEngine        the only code that turns a report into a baseline write
    //   CustomsBaselineStore one file per character, atomic replace + .bak
    //   CustomsApprovals     one-shot operator approvals
    //
    // No Unity or Valheim types, so the rule that matters most - a refused, unusable
    // or unanswered session never writes a baseline - is exercised directly by
    // tests/ServerGuard.Tests against real files.
    // ==========================================================================

    public enum CustomsMode
    {
        Disabled = 0,
        DryRun   = 1,   // judge, log and learn; never refuse
        Enforce  = 2    // refuse (kick) what dry run would only log
    }

    // Which characters without a baseline enforce admits.
    public enum CustomsNewCharacters
    {
        Fresh   = 0,    // only one arriving with nothing (after customsIgnoredItems)
        Any     = 1,    // any: its first arrival becomes its baseline
        Approve = 2     // none, unless an operator approved it
    }

    public enum CustomsVerdict
    {
        Cleared,        // a baseline exists and the arrival carries nothing new
        Established,    // no baseline yet: this arrival becomes it
        Flagged,        // dry run: enforce would refuse this arrival; admitted and learned
        Approved,       // enforce would refuse; admitted on a one-shot operator approval
        Refused,        // enforce: disconnected, nothing recorded
        Enrolled,       // Customs started mid-session: recorded without a judgement
        Unjudged        // the stored baseline could not be read: admitted, nothing recorded
    }

    public enum CustomsFinding
    {
        None,
        UndeclaredItems,    // carries items its baseline does not account for
        UnknownCharacter    // has no baseline and is not admissible as a new character
    }

    // What a missing or unusable declaration leads to.
    public enum CustomsFallback { Ignore, Log, Refuse }

    public enum CustomsReconcileAction { Keep, Enrol, Release }

    public sealed class CustomsJudgement
    {
        public CustomsVerdict Verdict;
        public CustomsFinding Finding;
        public bool HadBaseline;
        // Anything the baseline store had to say (a corrupt file moved aside, a .bak
        // used instead). Empty when the lookup was clean.
        public string StoreNote = "";
        // Undeclared items (with a baseline) or everything carried (without one), with
        // operator-ignored prefabs removed. Empty when there is nothing to report.
        public List<CustomsItem> Delta = new List<CustomsItem>();

        public bool Admits { get { return Verdict != CustomsVerdict.Refused; } }
    }

    public static class CustomsPolicy
    {
        // The effective mode. An unrecognised value lands on dryrun - never on enforce -
        // and ServerGuard's global log-only switch (enforce: false) downgrades enforce,
        // because a customs refusal is a kick like any other.
        public static CustomsMode Resolve(bool enabled, string mode, bool serverEnforce)
        {
            if (!enabled) return CustomsMode.Disabled;
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "enforce":  return serverEnforce ? CustomsMode.Enforce : CustomsMode.DryRun;
                case "disabled":
                case "off":      return CustomsMode.Disabled;
                default:         return CustomsMode.DryRun;
            }
        }

        public static CustomsNewCharacters ParseNewCharacters(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant())
            {
                case "any":     return CustomsNewCharacters.Any;
                case "approve": return CustomsNewCharacters.Approve;
                default:        return CustomsNewCharacters.Fresh;
            }
        }

        public static string Name(CustomsMode mode)
        {
            return mode == CustomsMode.Enforce ? "enforce" : mode == CustomsMode.DryRun ? "dryrun" : "disabled";
        }

        public static string Name(CustomsNewCharacters policy)
        {
            return policy == CustomsNewCharacters.Any ? "any" : policy == CustomsNewCharacters.Approve ? "approve" : "fresh";
        }

        // Owners are exempt from every ServerGuard rule and never attest, so there is no
        // attested client to ask. Moderators attest like players and are inspected
        // unless customsExemptModerators says otherwise. A peer without a SteamID64
        // (a crossplay account ServerGuard cannot resolve) has nothing to key a
        // baseline to: inspecting it would judge every arrival as a new character.
        public static bool Inspects(CustomsMode mode, string steamId, bool isOwner, bool isModerator, bool exemptModerators)
        {
            if (mode == CustomsMode.Disabled || isOwner || !CustomsBaselineStore.IsValidSteamId(steamId)) return false;
            return !(isModerator && exemptModerators);
        }

        // Hot-reload: which online, attested player needs a session started or dropped.
        public static CustomsReconcileAction Reconcile(bool hasSession, bool inspects)
        {
            if (inspects) return hasSession ? CustomsReconcileAction.Keep : CustomsReconcileAction.Enrol;
            return hasSession ? CustomsReconcileAction.Release : CustomsReconcileAction.Keep;
        }

        // Silence and garbage must not be a way through: enforce refuses, dry run logs.
        public static CustomsFallback ForUnusable(CustomsMode mode)
        {
            if (mode == CustomsMode.Enforce) return CustomsFallback.Refuse;
            return mode == CustomsMode.DryRun ? CustomsFallback.Log : CustomsFallback.Ignore;
        }

        //                 | cleared (delta empty) | undeclared items   | no baseline, admissible | no baseline, not admissible
        //   dry run       | Cleared               | Flagged            | Established             | Flagged
        //   enforce       | Cleared               | Refused / Approved | Established             | Refused / Approved
        //
        // "Admissible" is customsNewCharacters: any, or fresh with nothing carried.
        // An approval only matters where the answer would otherwise be Refused, so it
        // is never spent on an arrival that did not need it.
        public static CustomsJudgement JudgeArrival(CustomsMode mode, CustomsNewCharacters newCharacters,
                                                    bool hasBaseline, IEnumerable<CustomsItem> baseline,
                                                    IEnumerable<CustomsItem> declared, ICollection<string> ignored,
                                                    bool approved)
        {
            var j = new CustomsJudgement { HadBaseline = hasBaseline };
            if (hasBaseline)
            {
                j.Delta = CustomsItems.Without(CustomsItems.PositiveDelta(baseline, declared), ignored);
                if (j.Delta.Count == 0) { j.Verdict = CustomsVerdict.Cleared; return j; }
                j.Finding = CustomsFinding.UndeclaredItems;
            }
            else
            {
                var carried = CustomsItems.Without(CustomsItems.Normalize(declared), ignored);
                bool admissible = newCharacters == CustomsNewCharacters.Any
                               || (newCharacters == CustomsNewCharacters.Fresh && carried.Count == 0);
                if (admissible) { j.Verdict = CustomsVerdict.Established; return j; }
                j.Delta = carried;
                j.Finding = CustomsFinding.UnknownCharacter;
            }

            if (mode != CustomsMode.Enforce) j.Verdict = CustomsVerdict.Flagged;
            else j.Verdict = approved ? CustomsVerdict.Approved : CustomsVerdict.Refused;
            return j;
        }
    }

    public enum CustomsPhase
    {
        Declaring,  // request sent, waiting for the declaration that answers it
        Admitted,   // declaration accepted; later reports may advance the baseline
        Closed      // refused, superseded or released: nothing it sends is looked at
    }

    public enum CustomsScreen { Accepted, Dropped, Rejected }

    public sealed class CustomsScreenResult
    {
        public CustomsScreen  Outcome;
        public CustomsReport  Report;
        public CustomsProblem Problem;
        public string         Detail = "";
    }

    // One connection's Customs state. The SteamID is taken from the authenticated
    // peer when the session is created; nothing in a report can change it. The
    // server keeps sessions keyed by the connection object itself: a peer's uid is
    // still 0 when attestation completes.
    public sealed class CustomsSession
    {
        // Rate limit: a burst of BurstReports, then one report per RefillSeconds. An
        // honest client polls every two seconds and sends at most one report per poll;
        // a logout right after a change still fits inside the burst.
        public const int    BurstReports  = 5;
        public const double RefillSeconds = 2.0;

        // How long a session waits for a character to appear in the world before the
        // declaration deadline starts anyway. Generous, because loading is slow; finite,
        // because otherwise a client that never reports its character would never
        // have to declare.
        public static readonly TimeSpan LoadingAllowance = TimeSpan.FromMinutes(15);

        public readonly string   SteamId;
        public readonly string   Nonce;
        public readonly bool     JudgesArrival;   // false: started mid-session (enrolment)
        public readonly DateTime StartedUtc;

        public CustomsPhase      Phase         { get; private set; }
        public bool              MayRecord     { get; private set; }
        public long              LastSequence  { get; private set; }
        public string            CharacterId   { get; private set; }
        public string            CharacterName { get; private set; }
        public CustomsJudgement  Judgement     { get; private set; }
        public List<CustomsItem> LastItems     { get; private set; }
        public string            LastSignature { get; private set; }
        public DateTime          EstablishedUtc { get; internal set; }
        public DateTime?         DeadlineUtc   { get; private set; }
        public bool              DeadlineWithoutCharacter { get; private set; }
        public string            ClosedReason  { get; private set; }

        public bool   Warned;          // a dry-run warning was already posted for this session
        public int    Accepted, Dropped, Rejected;
        public string LastProblem = "";

        private double   _tokens = BurstReports;
        private DateTime _refilledUtc;

        public CustomsSession(string steamId, string nonce, bool judgesArrival, DateTime nowUtc)
        {
            SteamId       = steamId ?? "";
            Nonce         = nonce ?? "";
            JudgesArrival = judgesArrival;
            StartedUtc    = nowUtc;
            _refilledUtc  = nowUtc;
            Phase         = CustomsPhase.Declaring;
            CharacterId   = "";
            CharacterName = "";
            LastItems     = new List<CustomsItem>();
            LastSignature = "";
            ClosedReason  = "";
        }

        // Called on every tick while the declaration is awaited. The deadline runs from
        // the moment the character is in the world, not from the request: loading a
        // large modpack can take minutes, and nothing can be carried in before the
        // character exists. After LoadingAllowance it starts regardless.
        public void ArmDeadline(DateTime nowUtc, int timeoutSeconds, bool characterInWorld)
        {
            if (Phase != CustomsPhase.Declaring || DeadlineUtc.HasValue) return;
            if (!characterInWorld && nowUtc - StartedUtc < LoadingAllowance) return;
            DeadlineUtc = nowUtc.AddSeconds(Math.Max(5, timeoutSeconds));
            DeadlineWithoutCharacter = !characterInWorld;
        }

        public bool IsOverdue(DateTime nowUtc)
        {
            return Phase == CustomsPhase.Declaring && DeadlineUtc.HasValue && nowUtc >= DeadlineUtc.Value;
        }

        // Every report passes through here before anything looks at its contents.
        //
        //   Dropped  - stale, replayed, for another request, or over the rate limit.
        //              Harmless: it cannot be the answer being waited for and it is
        //              never recorded. Counted, otherwise ignored.
        //   Rejected - unusable: malformed, out of bounds, the wrong kind for the
        //              phase, or about a different character. The caller applies
        //              CustomsEngine.Unusable.
        //   Accepted - binds the sequence; the caller hands it to CustomsEngine.
        //
        // `expectedName` is the character name the server saw at PeerInfo.
        public CustomsScreenResult Screen(string payload, CustomsLimits limits, string expectedName, DateTime nowUtc)
        {
            if (Phase == CustomsPhase.Closed) return Drop("session closed");
            if (!TakeToken(nowUtc)) return Drop("rate limited");

            CustomsReport report;
            CustomsProblem problem;
            string detail;
            if (!CustomsWire.TryReadReport(payload, limits, out report, out problem, out detail))
                return Reject(problem, detail);

            if (!string.Equals(report.Nonce, Nonce, StringComparison.Ordinal)) return Drop("answers a different request");
            if (report.Sequence <= LastSequence) return Drop("sequence " + report.Sequence + " is not after " + LastSequence);

            if (Phase == CustomsPhase.Declaring && report.Kind != CustomsReportKind.Declare)
                return Reject(CustomsProblem.WrongKind, CustomsWire.KindName(report.Kind) + " sent before the declaration");
            if (Phase == CustomsPhase.Admitted && report.Kind == CustomsReportKind.Declare)
                return Drop("repeated declaration");

            var expected = CustomsItems.Clean(expectedName, CustomsLimits.MaxNameLength);
            if (expected.Length > 0 && !string.Equals(report.CharacterName, expected, StringComparison.OrdinalIgnoreCase))
                return Reject(CustomsProblem.CharacterMismatch, "report is for '" + CustomsItems.SafeName(report.CharacterName)
                    + "', the connection is '" + CustomsItems.SafeName(expected) + "'");
            if (CharacterId.Length > 0 && !string.Equals(report.CharacterId, CharacterId, StringComparison.Ordinal))
                return Reject(CustomsProblem.CharacterSwitched, "character id changed mid-session");

            LastSequence = report.Sequence;
            Accepted++;
            return new CustomsScreenResult { Outcome = CustomsScreen.Accepted, Report = report };
        }

        public void Close(string reason)
        {
            Phase = CustomsPhase.Closed;
            MayRecord = false;
            if (string.IsNullOrEmpty(ClosedReason)) ClosedReason = reason ?? "";
        }

        internal void BindCharacter(string characterId, string characterName)
        {
            CharacterId   = characterId ?? "";
            CharacterName = characterName ?? "";
        }

        internal void Admit(CustomsJudgement judgement, bool mayRecord)
        {
            Judgement = judgement;
            Phase     = CustomsPhase.Admitted;
            MayRecord = mayRecord;
        }

        internal void Refuse(CustomsJudgement judgement, string reason)
        {
            Judgement = judgement;
            Close(reason);
        }

        internal void Remember(List<CustomsItem> items, string signature)
        {
            LastItems     = items ?? new List<CustomsItem>();
            LastSignature = signature ?? "";
        }

        private bool TakeToken(DateTime nowUtc)
        {
            var elapsed = (nowUtc - _refilledUtc).TotalSeconds;
            if (elapsed > 0)
            {
                _tokens = Math.Min(BurstReports, _tokens + elapsed / RefillSeconds);
                _refilledUtc = nowUtc;
            }
            if (_tokens < 1.0) return false;
            _tokens -= 1.0;
            return true;
        }

        private CustomsScreenResult Drop(string why)
        {
            Dropped++;
            return new CustomsScreenResult { Outcome = CustomsScreen.Dropped, Detail = why };
        }

        private CustomsScreenResult Reject(CustomsProblem problem, string detail)
        {
            Rejected++;
            LastProblem = problem + (string.IsNullOrEmpty(detail) ? "" : ": " + detail);
            return new CustomsScreenResult { Outcome = CustomsScreen.Rejected, Problem = problem, Detail = detail ?? "" };
        }
    }

    // The transitions that touch the record. Kept apart from the Unity-facing server
    // code so each guarantee has one implementation and a test:
    //
    //   * only an admitted session with MayRecord writes, and only for its own character
    //   * a refusal writes nothing, and closes the session so later reports are inert
    //   * an unusable report never writes; the last trusted baseline stays as it was
    public static class CustomsEngine
    {
        // The first accepted report of a session: the declaration.
        public static CustomsJudgement Declare(CustomsSession session, CustomsReport report, CustomsBaselineStore store,
                                               CustomsMode mode, CustomsNewCharacters newCharacters,
                                               ICollection<string> ignored, CustomsApprovals approvals, DateTime nowUtc)
        {
            session.BindCharacter(report.CharacterId, report.CharacterName);

            CustomsBaseline baseline;
            string note;
            var lookup = store.TryGet(session.SteamId, report.CharacterId, out baseline, out note, true);
            session.EstablishedUtc = lookup == CustomsLookup.Found ? baseline.EstablishedUtc : nowUtc;

            CustomsJudgement j;
            if (lookup == CustomsLookup.Unavailable)
            {
                // The disk failed us, not the player. Admit, but record nothing that
                // could overwrite a baseline we were unable to read.
                j = new CustomsJudgement { Verdict = CustomsVerdict.Unjudged };
            }
            else if (!session.JudgesArrival)
            {
                // Customs was switched on (or the player came into scope) while they were
                // online: what they carry now was partly earned this session, so there is
                // no arrival to judge. The delta is kept for the log only.
                j = new CustomsJudgement { Verdict = CustomsVerdict.Enrolled, HadBaseline = lookup == CustomsLookup.Found };
                if (j.HadBaseline) j.Delta = CustomsItems.Without(CustomsItems.PositiveDelta(baseline.Items, report.Items), ignored);
            }
            else
            {
                bool approved = approvals != null && approvals.IsActive(session.SteamId, nowUtc);
                j = CustomsPolicy.JudgeArrival(mode, newCharacters, lookup == CustomsLookup.Found,
                    lookup == CustomsLookup.Found ? baseline.Items : null, report.Items, ignored, approved);
            }
            j.StoreNote = note ?? "";

            if (!j.Admits)
            {
                session.Remember(report.Items, CustomsItems.Signature(report.Items));
                session.Refuse(j, "refused: " + (j.Finding == CustomsFinding.UnknownCharacter ? "character has no baseline" : "undeclared items"));
                return j;
            }

            if (j.Verdict == CustomsVerdict.Approved && approvals != null)
            {
                approvals.Consume(session.SteamId, nowUtc);
                approvals.Save();
            }

            bool mayRecord = j.Verdict != CustomsVerdict.Unjudged;
            session.Admit(j, mayRecord);
            if (mayRecord)
                Write(session, report, store, j.Verdict == CustomsVerdict.Enrolled ? "enrolled" : j.Verdict == CustomsVerdict.Approved ? "approved" : "arrival", nowUtc, true);
            else
                session.Remember(report.Items, CustomsItems.Signature(report.Items));
            return j;
        }

        // A change / checkpoint / logout report. Returns true when the baseline moved.
        public static bool Record(CustomsSession session, CustomsReport report, CustomsBaselineStore store, DateTime nowUtc)
        {
            if (session == null || report == null || store == null) return false;
            if (session.Phase != CustomsPhase.Admitted || !session.MayRecord) return false;
            if (!string.Equals(report.CharacterId, session.CharacterId, StringComparison.Ordinal)) return false;
            return Write(session, report, store, CustomsWire.KindName(report.Kind), nowUtc, false);
        }

        // An unusable report or an overdue declaration. Returns what the caller must do.
        public static CustomsFallback Unusable(CustomsSession session, CustomsMode mode, string reason)
        {
            var fallback = CustomsPolicy.ForUnusable(mode);
            if (fallback == CustomsFallback.Refuse)
                session.Close(reason);
            else if (fallback == CustomsFallback.Log && session.Phase == CustomsPhase.Admitted)
                // A trusted session that starts sending garbage stops being trusted: its
                // baseline stays at the last good report.
                session.Close(reason);
            return fallback;
        }

        // A newly admitted session supersedes any other live session for the same
        // character - typically a reconnect while the server still holds the dropped
        // connection - so two connections never interleave writes to one baseline.
        public static int Supersede(IEnumerable<CustomsSession> sessions, CustomsSession current)
        {
            int closed = 0;
            if (current == null || current.CharacterId.Length == 0) return 0;
            foreach (var s in sessions)
            {
                if (s == null || ReferenceEquals(s, current) || s.Phase == CustomsPhase.Closed) continue;
                if (!string.Equals(s.SteamId, current.SteamId, StringComparison.Ordinal)) continue;
                if (!string.Equals(s.CharacterId, current.CharacterId, StringComparison.Ordinal)) continue;
                s.Close("superseded by a newer connection");
                closed++;
            }
            return closed;
        }

        private static bool Write(CustomsSession session, CustomsReport report, CustomsBaselineStore store,
                                  string origin, DateTime nowUtc, bool always)
        {
            var signature = CustomsItems.Signature(report.Items);
            bool changed = !string.Equals(signature, session.LastSignature, StringComparison.Ordinal);
            session.Remember(report.Items, signature);
            if (!changed && !always) return false;

            store.Put(new CustomsBaseline
            {
                SteamId        = session.SteamId,
                CharacterId    = session.CharacterId,
                CharacterName  = session.CharacterName,
                EstablishedUtc = session.EstablishedUtc,
                UpdatedUtc     = nowUtc,
                Origin         = origin,
                Items          = report.Items,
            });
            return true;
        }
    }

    public enum CustomsLookup { Found, NotFound, Unavailable }

    public sealed class CustomsBaseline
    {
        public string   SteamId       = "";
        public string   CharacterId   = "";
        public string   CharacterName = "";
        public DateTime EstablishedUtc;
        public DateTime UpdatedUtc;
        public string   Origin        = "";
        public List<CustomsItem> Items = new List<CustomsItem>();
    }

    // One JSON file per character, <root>/<steamId>/<characterId>.json, so a write
    // touches only the character that changed and damage stays with one file.
    //
    //   * Writes go to <file>.tmp, are flushed to disk, then swapped in with
    //     File.Replace, which keeps the previous version as <file>.bak. A crash
    //     leaves the old file or the new one, never half of one.
    //   * A file that cannot be parsed is moved aside as <file>.corrupt-<utc> - kept as
    //     evidence, never overwritten - and the .bak is tried. With no readable copy
    //     the character simply has no baseline, so the new-character rule applies.
    //   * A file that cannot be READ (permissions, I/O), or that a newer ServerGuard
    //     wrote, is different: that is a server problem, reported as Unavailable, and
    //     nothing is written over it.
    //   * Writes are queued and flushed by the server's tick, with exponential
    //     back-off while the disk refuses them. Reads check the queue first, so a
    //     character that reconnects before its departure record reached the disk is
    //     judged against that record, not an older file.
    public sealed class CustomsBaselineStore
    {
        public const int Schema = 1;

        private readonly string _root;
        private readonly Dictionary<string, CustomsBaseline> _queued = new Dictionary<string, CustomsBaseline>(StringComparer.Ordinal);
        private DateTime _retryAfterUtc = DateTime.MinValue;

        public int    ConsecutiveFailures { get; private set; }
        public string LastError { get; private set; }
        public int    QueuedCount { get { return _queued.Count; } }
        public string Root { get { return _root; } }

        // Test seams: throw from BeforeWrite to simulate a disk that refuses writes;
        // replace ReadText to simulate one that refuses reads.
        internal Action<string> BeforeWrite { get; set; }
        internal Func<string, string> ReadText { get; set; } = path => File.ReadAllText(path, Encoding.UTF8);

        public CustomsBaselineStore(string root)
        {
            _root = root ?? "";
            LastError = "";
        }

        public static bool IsValidSteamId(string s)
        {
            if (s == null || s.Length != 17 || s == "00000000000000000") return false;
            foreach (var c in s)
                if (c < '0' || c > '9') return false;
            return true;
        }

        public string PathFor(string steamId, string characterId)
        {
            return Path.Combine(Path.Combine(_root, steamId), characterId + ".json");
        }

        public CustomsLookup TryGet(string steamId, string characterId, out CustomsBaseline baseline, out string note, bool repair)
        {
            baseline = null;
            note = "";
            // Both parts of the key become path segments; anything but digits never gets near the disk.
            if (!IsValidSteamId(steamId) || !CustomsWire.IsCanonicalCharacterId(characterId)) return CustomsLookup.NotFound;

            if (_queued.TryGetValue(Key(steamId, characterId), out baseline)) return CustomsLookup.Found;

            var path = PathFor(steamId, characterId);
            string error;
            if (File.Exists(path))
            {
                var read = Read(path, steamId, characterId, out baseline, out error);
                if (read == CustomsLookup.Found) return CustomsLookup.Found;
                if (read == CustomsLookup.Unavailable) { note = "baseline could not be read: " + error; return CustomsLookup.Unavailable; }
                note = "baseline file was corrupt (" + error + ")";
                if (repair)
                {
                    var kept = Quarantine(path);
                    note += kept != null ? "; kept as " + Path.GetFileName(kept) : "; could not be moved aside";
                }
            }

            var bak = path + ".bak";
            if (File.Exists(bak))
            {
                var read = Read(bak, steamId, characterId, out baseline, out error);
                if (read == CustomsLookup.Found)
                {
                    note = (note.Length > 0 ? note + "; " : "") + "using the previous version (.bak)";
                    // Queue it, so the next flush restores the primary file.
                    if (repair) _queued[Key(steamId, characterId)] = baseline;
                    return CustomsLookup.Found;
                }
                if (read == CustomsLookup.Unavailable) { note = (note.Length > 0 ? note + "; " : "") + ".bak could not be read: " + error; return CustomsLookup.Unavailable; }
                note = (note.Length > 0 ? note + "; " : "") + ".bak is corrupt too (" + error + ")";
                if (repair) Quarantine(bak);
            }
            return CustomsLookup.NotFound;
        }

        public void Put(CustomsBaseline baseline)
        {
            if (baseline == null || !IsValidSteamId(baseline.SteamId) || !CustomsWire.IsCanonicalCharacterId(baseline.CharacterId)) return;
            _queued[Key(baseline.SteamId, baseline.CharacterId)] = baseline;
        }

        public bool IsQueued(string steamId, string characterId)
        {
            return _queued.ContainsKey(Key(steamId, characterId));
        }

        // Writes queued baselines. `force` ignores the back-off (shutdown, departures).
        // Returns the number written; a failure leaves the rest queued for later.
        public int Flush(DateTime nowUtc, bool force)
        {
            if (_queued.Count == 0 || (!force && nowUtc < _retryAfterUtc)) return 0;
            int written = 0;
            foreach (var key in new List<string>(_queued.Keys))
            {
                if (!WriteQueued(key, nowUtc)) return written;
                written++;
            }
            return written;
        }

        // Writes one character's queued baseline now (a departure). False on failure;
        // the baseline stays queued for the next flush.
        public bool FlushOne(string steamId, string characterId, DateTime nowUtc)
        {
            var key = Key(steamId, characterId);
            return !_queued.ContainsKey(key) || WriteQueued(key, nowUtc);
        }

        public bool Remove(string steamId, string characterId)
        {
            if (!IsValidSteamId(steamId) || !CustomsWire.IsCanonicalCharacterId(characterId)) return false;
            bool removed = _queued.Remove(Key(steamId, characterId));
            var path = PathFor(steamId, characterId);
            if (File.Exists(path)) { File.Delete(path); removed = true; }
            if (File.Exists(path + ".bak")) { File.Delete(path + ".bak"); removed = true; }
            return removed;
        }

        // Every stored baseline of one account (queued ones included). Read-only: a
        // corrupt file is reported, not moved.
        public List<CustomsBaseline> List(string steamId)
        {
            var result = new List<CustomsBaseline>();
            if (!IsValidSteamId(steamId)) return result;
            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var key in _queued.Keys)
                if (key.StartsWith(steamId + ":", StringComparison.Ordinal)) ids.Add(key.Substring(steamId.Length + 1));
            var dir = Path.Combine(_root, steamId);
            if (Directory.Exists(dir))
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                    ids.Add(Path.GetFileNameWithoutExtension(file));
            foreach (var id in ids)
            {
                CustomsBaseline b;
                string note;
                if (TryGet(steamId, id, out b, out note, false) == CustomsLookup.Found) result.Add(b);
            }
            return result;
        }

        public static string Serialize(CustomsBaseline b)
        {
            var sw = new StringWriter(new StringBuilder(1024), CultureInfo.InvariantCulture);
            using (var w = new JsonTextWriter(sw))
            {
                w.Formatting = Formatting.None;
                w.WriteStartObject();
                w.WritePropertyName("schema");        w.WriteValue(Schema);
                w.WritePropertyName("steamId");       w.WriteValue(b.SteamId ?? "");
                w.WritePropertyName("characterId");   w.WriteValue(b.CharacterId ?? "");
                w.WritePropertyName("characterName"); w.WriteValue(b.CharacterName ?? "");
                w.WritePropertyName("established");   w.WriteValue(Stamp(b.EstablishedUtc));
                w.WritePropertyName("updated");       w.WriteValue(Stamp(b.UpdatedUtc));
                w.WritePropertyName("origin");        w.WriteValue(b.Origin ?? "");
                w.WritePropertyName("items");         CustomsWire.WriteItems(w, CustomsItems.Normalize(b.Items));
                w.WriteEndObject();
            }
            return sw.ToString();
        }

        // Parses a baseline file and checks it belongs where it was found. Held to the
        // same item bounds as a report, so a hand edit cannot plant absurd values.
        public static bool TryParse(string text, string steamId, string characterId, out CustomsBaseline baseline, out string error)
        {
            bool newerSchema;
            return TryParse(text, steamId, characterId, out baseline, out error, out newerSchema);
        }

        // newerSchema: a later ServerGuard wrote this file (a downgrade). It is not
        // corrupt, only unreadable by this build, so it must not be moved aside.
        internal static bool TryParse(string text, string steamId, string characterId, out CustomsBaseline baseline,
                                      out string error, out bool newerSchema)
        {
            baseline = null;
            error = "";
            newerSchema = false;
            var b = new CustomsBaseline();
            int schema = -1;
            bool sawItems = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text ?? "")))
                {
                    reader.MaxDepth = CustomsLimits.MaxDepth;
                    reader.DateParseHandling = DateParseHandling.None;
                    if (!reader.Read() || reader.TokenType != JsonToken.StartObject) { error = "not a JSON object"; return false; }
                    while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
                    {
                        var name = (string)reader.Value;
                        bool known = name == "items" || name == "schema" || name == "steamId" || name == "characterId"
                                  || name == "characterName" || name == "established" || name == "updated" || name == "origin";
                        if (known && !seen.Add(name)) { error = "'" + name + "' appears twice"; return false; }
                        if (name == "items")
                        {
                            CustomsProblem problem;
                            if (!CustomsWire.TryReadItems(reader, new CustomsLimits(CustomsLimits.MaxRecordsCap), b.Items, out problem, out error)) return false;
                            sawItems = true;
                            continue;
                        }
                        if (!reader.Read()) { error = "truncated"; return false; }
                        if (reader.TokenType == JsonToken.StartObject || reader.TokenType == JsonToken.StartArray)
                        {
                            reader.Skip();
                            if (known) { error = "'" + name + "' is not a plain value"; return false; }
                            continue;
                        }
                        var value = reader.Value == null ? "" : Convert.ToString(reader.Value, CultureInfo.InvariantCulture);
                        switch (name)
                        {
                            case "schema":
                                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out schema);
                                // Checked at once: a newer layout may not parse as this one.
                                if (schema > Schema)
                                {
                                    newerSchema = true;
                                    error = "written by a newer ServerGuard (schema " + schema + ", this build reads " + Schema + ")";
                                    return false;
                                }
                                break;
                            case "steamId":       b.SteamId = value; break;
                            case "characterId":   b.CharacterId = value; break;
                            case "characterName": b.CharacterName = CustomsItems.Clean(value, CustomsLimits.MaxNameLength); break;
                            case "established":   if (!ParseStamp(value, out b.EstablishedUtc)) { error = "bad 'established'"; return false; } break;
                            case "updated":       if (!ParseStamp(value, out b.UpdatedUtc)) { error = "bad 'updated'"; return false; } break;
                            case "origin":        b.Origin = CustomsItems.Clean(value, 16); break;
                        }
                    }
                    if (reader.TokenType != JsonToken.EndObject) { error = "unterminated object"; return false; }
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }

            if (schema != Schema) { error = "schema " + schema + " (this build reads " + Schema + ")"; return false; }
            if (!sawItems) { error = "no items"; return false; }
            if (b.SteamId != steamId || b.CharacterId != characterId) { error = "file does not belong to " + steamId + "/" + characterId; return false; }
            b.Items = CustomsItems.Normalize(b.Items);
            baseline = b;
            return true;
        }

        // tmp + fsync + replace. Also used for approvals.json.
        internal static void WriteAtomic(string path, string text)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(text ?? "");
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
                else File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        private bool WriteQueued(string key, DateTime nowUtc)
        {
            var b = _queued[key];
            var path = PathFor(b.SteamId, b.CharacterId);
            try
            {
                if (BeforeWrite != null) BeforeWrite(path);
                WriteAtomic(path, Serialize(b));
                _queued.Remove(key);
                ConsecutiveFailures = 0;
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                ConsecutiveFailures++;
                LastError = ex.Message;
                _retryAfterUtc = nowUtc.AddSeconds(Math.Min(60.0, Math.Pow(2, Math.Min(ConsecutiveFailures, 6))));
                return false;
            }
        }

        private CustomsLookup Read(string path, string steamId, string characterId, out CustomsBaseline baseline, out string error)
        {
            baseline = null;
            string text;
            try
            {
                text = ReadText(path);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return CustomsLookup.Unavailable;
            }
            bool newerSchema;
            if (TryParse(text, steamId, characterId, out baseline, out error, out newerSchema)) return CustomsLookup.Found;
            return newerSchema ? CustomsLookup.Unavailable : CustomsLookup.NotFound;
        }

        private static string Quarantine(string path)
        {
            try
            {
                var target = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                for (int i = 1; File.Exists(target) && i < 100; i++)
                    target = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + i.ToString(CultureInfo.InvariantCulture);
                File.Move(path, target);
                return target;
            }
            catch
            {
                return null;
            }
        }

        private static string Key(string steamId, string characterId)
        {
            return steamId + ":" + characterId;
        }

        private static string Stamp(DateTime utc)
        {
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture);
        }

        private static bool ParseStamp(string s, out DateTime utc)
        {
            return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
        }
    }

    // One-shot operator approvals (sg customs approve). An approval lets the next
    // arrival of that account through whatever enforce would have said, re-baselines
    // the character from that arrival, and is spent in the process - it can never
    // become a standing bypass. Unused approvals expire after Lifetime.
    //
    // Kept in customs/approvals.json. An unreadable file means no approvals: failing
    // closed here only costs an operator a retyped command.
    public sealed class CustomsApprovals
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

        public sealed class Entry
        {
            public string   SteamId = "";
            public DateTime ExpiresUtc;
            public string   By = "";
        }

        private readonly string _path;
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public string LastError { get; private set; }

        public CustomsApprovals(string path)
        {
            _path = path ?? "";
            LastError = "";
        }

        public void Load(DateTime nowUtc)
        {
            _entries.Clear();
            LastError = "";
            try
            {
                if (!File.Exists(_path)) return;
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(_path, Encoding.UTF8))))
                {
                    reader.MaxDepth = CustomsLimits.MaxDepth;
                    reader.DateParseHandling = DateParseHandling.None;
                    var doc = new JsonSerializer().Deserialize<Dictionary<string, string>[]>(reader);
                    foreach (var row in doc ?? new Dictionary<string, string>[0])
                    {
                        string id, expires, by;
                        DateTime when;
                        if (row == null || !row.TryGetValue("steamId", out id) || !CustomsBaselineStore.IsValidSteamId(id)) continue;
                        if (!row.TryGetValue("expires", out expires) || !DateTime.TryParse(expires, CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when)) continue;
                        if (when <= nowUtc) continue;
                        row.TryGetValue("by", out by);
                        _entries[id] = new Entry { SteamId = id, ExpiresUtc = when, By = CustomsItems.Clean(by, 32) };
                    }
                }
            }
            catch (Exception ex)
            {
                _entries.Clear();
                LastError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        public bool Save()
        {
            try
            {
                var rows = new List<Dictionary<string, string>>();
                foreach (var e in _entries.Values)
                {
                    rows.Add(new Dictionary<string, string>
                    {
                        { "steamId", e.SteamId },
                        { "expires", e.ExpiresUtc.ToString("o", CultureInfo.InvariantCulture) },
                        { "by", e.By },
                    });
                }
                CustomsBaselineStore.WriteAtomic(_path, JsonConvert.SerializeObject(rows, Formatting.Indented));
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public void Grant(string steamId, string by, DateTime nowUtc)
        {
            if (!CustomsBaselineStore.IsValidSteamId(steamId)) return;
            _entries[steamId] = new Entry { SteamId = steamId, ExpiresUtc = nowUtc + Lifetime, By = CustomsItems.Clean(by, 32) };
        }

        public bool Revoke(string steamId)
        {
            return steamId != null && _entries.Remove(steamId);
        }

        public bool IsActive(string steamId, DateTime nowUtc)
        {
            Entry e;
            return steamId != null && _entries.TryGetValue(steamId, out e) && e.ExpiresUtc > nowUtc;
        }

        public bool Consume(string steamId, DateTime nowUtc)
        {
            if (!IsActive(steamId, nowUtc)) return false;
            _entries.Remove(steamId);
            return true;
        }

        public List<Entry> Active(DateTime nowUtc)
        {
            var list = new List<Entry>();
            foreach (var e in _entries.Values)
                if (e.ExpiresUtc > nowUtc) list.Add(e);
            list.Sort((a, b) => string.CompareOrdinal(a.SteamId, b.SteamId));
            return list;
        }
    }
}
