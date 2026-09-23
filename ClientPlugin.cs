using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ValheimServerGuard.Shared;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ValheimServerGuard
{
    // The CLIENT half of ServerGuard. Attached by ServerGuardPlugin.Awake on a
    // player's game (never on a dedicated server). Not a BepInEx plugin itself: it is
    // a plain MonoBehaviour on the entry plugin's GameObject, so Awake / coroutines /
    // OnDestroy behave exactly as they did when this was the standalone companion.
    internal class ClientPlugin : MonoBehaviour
    {
        // Same GUID/name/version as the server half - there is one mod now. Kept as
        // aliases so the manifest export and log lines read naturally.
        public const string GUID    = ServerGuardPlugin.GUID;
        public const string NAME    = ServerGuardPlugin.NAME;
        public const string VERSION = ServerGuardPlugin.VERSION;

        internal static ClientPlugin Instance;
        internal static ManualLogSource LogS;
        private Harmony _harmony;

        private string _sharedSecret = "";
        private List<ModManifestEntry> _cachedManifest;

        // Quick Login (title-screen panel)
        private ClientSettings _clientSettings = new ClientSettings();
        private GameObject     _quickLoginPanel;
        // TMP_Text or UnityEngine.UI.Text — updated via SetAnyText.
        private Component      _playerCountText;
        // One-shot quick-join state. Armed when Connect is clicked; re-asserted in the
        // OnCharacterStart prefix so the game connects directly. Cleared on back-out.
        private bool           _quickJoinArmed;
        private object         _armedJoinData;
        private string         _armedPassword;

        // Reference to the server peer's ZRpc, captured when we connect. Used to send
        // the ServerGuard_DevcommandAttempt RPC back to the server when the gate fires.
        // null when not connected (single-player, main menu, between connections).
        internal ZRpc _serverRpc;

        // ====================== Console command classification ======================
        //
        // Three tiers, because they carry different consequences and deserve different
        // reporting on the server.
        //
        // CHEAT: `devcommands` and everything it unlocks. Valheim's own position is that
        // these "do not work on a dedicated server", but that guarantee is enforced on
        // the CLIENT (Terminal.m_cheat), so a patched client can flip it. Many of these
        // commands then take effect for real, because the client owns the ZDOs for the
        // objects around it - a client-side `spawn` produces a genuine server-side item.
        private static readonly HashSet<string> CheatCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Master switches
            "devcommands", "debugmode", "imacheater",
            // Sets the `bypasscheatchecks` unique key on the character, after which the
            // game stops marking anything as cheated (1.0). Not IsCheat in vanilla, so
            // it has to be listed by name. The server also reports the key itself.
            "yesiuseddevcommandsbutiwantmyachievementsanyway",
            // Self state / invulnerability
            "god", "ghost", "heal", "puke", "damage", "addstatus", "clearstatus",
            "resetcharacter", "setpower", "model", "beard", "hair",
            // Movement / position
            "fly", "freefly", "goto", "findtp", "pos", "ffsmooth",
            // Teleports OTHER players to the caller
            "recall",
            // Entity + item creation
            "spawn", "itemset", "location", "nextseed", "genloc", "setfuel",
            // Free building / crafting
            "nocost", "noplacementcost",
            // Mass destruction and object removal
            "forcedelete", "killall", "killenemies", "killtame",
            "removedrops", "removebirds", "removefish",
            // Taming / aggro of creatures near other players
            "tame", "aggravate",
            // World-wide progression state (global keys)
            "setkey", "removekey", "resetkeys", "listkeys",
            // World-wide events
            "event", "randomevent", "stopevent",
            // World-wide time, weather and difficulty
            "tod", "skiptime", "sleep", "timescale", "env", "resetenv",
            "wind", "resetwind", "players",
            // Skills
            "raiseskill", "resetskill",
            // Map / world intel
            "exploremap", "resetmap", "find", "printcreatures", "printlocations",
            // Diagnostics that are cheap to spam
            "dpsdebug", "gc", "test",
        };

        // RISKY: not flagged as cheats by Valheim and usable without `devcommands`, but
        // each one either mutates shared server state, leaks information, or (in the
        // case of bind) provides a way to run other commands outside the normal
        // dispatch path. See claude/console-guard.md for the reasoning per command.
        private static readonly HashSet<string> RiskyCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // NOTE: the bind family (bind/unbind/resetbinds/printbinds) is deliberately
            // NOT here. It is gated by consoleGuardBindPolicy instead, so a server that
            // sets bindPolicy: allow keeps those commands usable rather than having
            // them blocked by a tier the operator can't opt out of.

            // Toggles a GLOBAL key when run on a server
            "nomap", "noportals",
            // World modifier / preset mutation (combat, resources, raids, portals)
            "setworldmodifier", "setworldpreset", "resetworldkeys",
            // Shared/served state
            "resetsharedmap", "resetspawn",
            // Mass terrain-modification rewrite in the loaded area
            "optterrain",
            // Reveals dungeon seeds and positions
            "printseeds",
            // Local data loss + wipes the persisted bind list out from under us
            "resetknownitems", "resetplayerprefs",
            // Cheap to spam, causes a hitch each time
            "cr", "restartparty",
        };

        // The bind family. Gated by consoleGuardBindPolicy rather than by a tier, so
        // `bindPolicy: allow` leaves all four usable even under restricted mode.
        private static readonly HashSet<string> BindCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bind", "unbind", "resetbinds", "printbinds",
        };

        // Commands Valheim already gates on the SERVER's admin list (ZNet checks the
        // caller is an admin before acting). Deliberately NOT blocked client-side:
        // doing so would add no security - a non-admin's attempt is refused server-side
        // regardless - while breaking legitimate moderation for real admins.
        // Listed here for documentation and for operators who want to add them to
        // consoleBlockedCommands themselves.
        private static readonly HashSet<string> VanillaAdminCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ban", "unban", "banned", "kick", "save",
        };

        // Always permitted, even under whitelist mode: local-only cosmetics, chat,
        // and the ServerGuard admin interface itself.
        private static readonly HashSet<string> AlwaysAllowedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sg", "help", "clear", "info", "ping", "fov", "maxfps",
            "exclusivefullscreen", "hidebetatext", "sortcraft", "filtercraft",
            "tutorialtoggle", "tutorialreset", "xb:version",
            "s", "say", "w", "die", "respawn",
            // Emotes (Chat handles these, but keep them explicit so whitelist mode
            // never eats one).
            "wave", "sit", "challenge", "cheer", "nonono", "thumbsup", "point",
            "blowkiss", "bow", "cower", "cry", "despair", "flex", "comehere",
            "headbang", "kneel", "laugh", "roar", "shrug", "dance", "relax",
            "toast", "rest", "vibe", "loveyou", "count",
        };

        // ====================== Console policy (server-driven) ======================
        //
        // Defaults apply until the server sends ServerGuard_ConsolePolicy. They match
        // the pre-1.7 behaviour so an older server (or a single-player session) is
        // unaffected: restricted mode, binds left alone.
        private static string _consoleMode       = "restricted"; // open|restricted|whitelist|disabled
        private static string _consoleBindPolicy = "allow";      // allow|block|purge|wipe
        private static string _consoleRole       = "player";     // owner|moderator|player (log only)
        private static HashSet<string> _consoleExtraBlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _consoleAllowed      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // True when the policy should be ignored for this player. The server decides
        // this (owner always; moderator when the server's consoleGuardExemptModerators
        // is on) and sends the answer, so the client never has to model the tiers.
        private static bool _consoleExempt = false;
        private static bool ConsoleGuardExempt => _consoleExempt;

        // The owner tier is exempt from every rule, including the ones enforced purely
        // client-side (the emote/animation-cancel gate), which the server has no
        // opportunity to wave through. `role` from the console-policy push is the only
        // channel carrying the tier, so it does double duty here.
        internal static bool IsOwnerClient => _consoleRole == "owner";
        internal static bool IsStaffClient => _consoleRole == "owner" || _consoleRole == "moderator";

        // 2.0: cheat-taint detection state on the server ("0" = off, else the policy
        // name: log / strip / violation). Drives the one-time notice panel.
        private static string _cheatTaintServerPolicy = "0";
        // Once per game process - the user asked for the notice on a fresh launch, not
        // on every relog. Static and never reset.
        private static bool _cheatTaintNoticeShown = false;
        // Once per connection - reset with the rest of the policy on ZNet.Shutdown.
        private static bool _moderatorWelcomeShown = false;

        // ---- Staff dev commands (2.0) ----
        //
        // The server's grant for THIS player, from the last two policy fields:
        //   "none" - vanilla behaviour (cheat commands refused on a dedicated-server client)
        //   "list" - only _devCommands may run (moderators)
        //   "all"  - every dev command may run (owners)
        // Enforced by Patch_Terminal_IsCheatsEnabled + Patch_ConsoleCommand_IsValid
        // (unlock) and by ShouldBlockConsoleCommand (moderator list). See the "Staff
        // dev commands" section below for how the pieces fit.
        private static string _devMode = "none";
        private static HashSet<string> _devCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Last grant we told the player about, so hot-reload re-pushes don't repeat it.
        private static string _devModeAnnounced = "";

        internal static bool DevAccessGranted =>
            _devMode == "all" || (_devMode == "list" && _devCommands.Count > 0);

        // May this player run `cmd` under the current grant? `devcommands` is always
        // permitted once anything is granted: it only flips the local m_cheat toggle,
        // and every other dev command is dead without it.
        internal static bool IsDevCommandPermitted(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            if (_devMode == "all") return true;
            if (_devMode == "list" && _devCommands.Count > 0)
                return _devCommands.Contains(cmd) || string.Equals(cmd, "devcommands", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        // Payload: mode|exempt|role|bindPolicy|blockedCsv|allowedCsv[|devMode|devCsv]
        internal static void OnConsolePolicyReceived(string payload)
        {
            try
            {
                var parts = (payload ?? "").Split('|');
                if (parts.Length < 4)
                {
                    LogS?.LogWarning($"[ServerGuard.Client] Malformed console policy payload ({parts.Length} fields) - keeping current policy.");
                    return;
                }

                _consoleMode       = parts[0].Trim().ToLowerInvariant();
                _consoleExempt     = parts[1].Trim() == "1";
                _consoleRole       = parts[2].Trim().ToLowerInvariant();
                _consoleBindPolicy = parts[3].Trim().ToLowerInvariant();

                _consoleExtraBlocked = ToSet(parts.Length > 4 ? parts[4] : "");
                _consoleAllowed      = ToSet(parts.Length > 5 ? parts[5] : "");

                // 2.0 fields. A pre-2.0 server sends six fields: no grant.
                var devMode = parts.Length > 6 ? parts[6].Trim().ToLowerInvariant() : "none";
                _devMode     = devMode == "all" || devMode == "list" ? devMode : "none";
                _devCommands = ToSet(parts.Length > 7 ? parts[7] : "");
                if (_devMode == "list" && _devCommands.Count == 0) _devMode = "none";
                _cheatTaintServerPolicy = parts.Length > 8 ? parts[8].Trim().ToLowerInvariant() : "0";
                if (_cheatTaintServerPolicy.Length == 0) _cheatTaintServerPolicy = "0";

                LogS?.LogInfo($"[ServerGuard.Client] Console policy: mode={_consoleMode} binds={_consoleBindPolicy} "
                    + $"role={_consoleRole} exempt={_consoleExempt} "
                    + $"extraBlocked={_consoleExtraBlocked.Count} allowed={_consoleAllowed.Count} "
                    + $"devcommands={_devMode}{(_devMode == "list" ? $"({_devCommands.Count})" : "")}");

                ApplyBindPolicy();
                AnnounceDevGrant();
                Instance?.QueueLoginMessages();
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Console policy parse failed: {ex.Message}");
            }
        }

        private static HashSet<string> ToSet(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in (csv ?? "").Split(','))
            {
                var v = s.Trim();
                if (v.Length > 0) set.Add(v);
            }
            return set;
        }

        // Reset to defaults when the connection ends, so leaving a locked-down server
        // doesn't leave the local console crippled in single-player.
        internal static void ResetConsolePolicy()
        {
            _consoleMode         = "restricted";
            _consoleBindPolicy   = "allow";
            _consoleRole         = "player";
            _consoleExempt       = false;
            _consoleExtraBlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _consoleAllowed      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _devMode             = "none";
            _devCommands         = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _devModeAnnounced    = "";
            _cheatTaintServerPolicy = "0";
            _moderatorWelcomeShown  = false;   // _cheatTaintNoticeShown deliberately NOT reset
        }

        // ====================== Login messages (2.0) ======================
        //
        // Two things shown after the player has actually spawned (the policy push
        // arrives during the handshake, long before there is a chat window or a
        // character to greet):
        //
        //   * Moderator welcome - every login, in the local chat window: greeting, the
        //     live dev-command list from the server, where the sg tools are, and a
        //     reminder to moderate responsibly. Owners don't get it.
        //   * Cheat-taint notice - a native warning popup, ONCE per game launch, when
        //     the server has cheat-taint detection on. Relogging without restarting
        //     the game does not show it again.
        //
        // Both are driven by the policy push, so a hot-reload re-push can re-trigger
        // the coroutine; the two flags make each message fire at most once.

        private Coroutine _loginMessagesCo;

        private void QueueLoginMessages()
        {
            try
            {
                if (!IsActiveMultiplayerClient()) return;
                bool wantWelcome = _consoleRole == "moderator" && !_moderatorWelcomeShown;
                bool wantNotice  = _cheatTaintServerPolicy != "0" && !_cheatTaintNoticeShown;
                if (!wantWelcome && !wantNotice) return;
                if (_loginMessagesCo != null) return;
                _loginMessagesCo = StartCoroutine(LoginMessagesCoroutine());
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] QueueLoginMessages error: {ex.Message}");
            }
        }

        private IEnumerator LoginMessagesCoroutine()
        {
            // Wait for the character to be in the world. Up to 3 minutes - a slow modded
            // load can take a while, and the policy push arrives before any of it.
            float waited = 0f;
            while (waited < 180f && (Player.m_localPlayer == null || Chat.instance == null))
            {
                yield return new WaitForSeconds(0.5f);
                waited += 0.5f;
            }
            _loginMessagesCo = null;
            if (Player.m_localPlayer == null || !IsActiveMultiplayerClient()) yield break;

            // A beat after spawn so the messages land after the vanilla arrival noise.
            yield return new WaitForSeconds(2f);

            try
            {
                if (_consoleRole == "moderator" && !_moderatorWelcomeShown)
                {
                    _moderatorWelcomeShown = true;
                    ShowModeratorWelcome();
                }
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Moderator welcome error: {ex.Message}");
            }

            try
            {
                if (_cheatTaintServerPolicy != "0" && !_cheatTaintNoticeShown)
                {
                    _cheatTaintNoticeShown = true;
                    ShowCheatTaintNotice(_cheatTaintServerPolicy);
                }
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Cheat-taint notice error: {ex.Message}");
            }
        }

        private static void ShowModeratorWelcome()
        {
            var chat = Chat.instance;
            if (chat == null) return;

            string name = "Viking";
            try { name = Player.m_localPlayer?.GetPlayerName() ?? name; } catch { }

            var cmds = _devMode == "list" && _devCommands.Count > 0
                ? string.Join(", ", _devCommands.OrderBy(c => c))
                : "(none granted right now)";

            var lines = new[]
            {
                $"<color=#ffd700>[ServerGuard]</color> Welcome back, <color=#ffd700>{name}</color>. You are a <color=#ffd700>moderator</color> on this server.",
                $"Dev commands available to you: <color=#a0e0ff>{cmds}</color>",
                "Type <color=#a0e0ff>devcommands</color> in the F5 console to enable them. Anything not on this list is refused and reported to the owner.",
                "Moderator tools: <color=#a0e0ff>sg help</color> in the console (kick, ban, whois, build log).",
                "Please moderate responsibly. Your actions are logged and posted to the admin channel, and the same cheat-detection rules that apply to players apply to you. Use your commands to help players, never to gain an advantage.",
            };

            foreach (var line in lines)
            {
                try { chat.AddString(line); }
                catch (Exception ex) { LogS?.LogInfo($"[ServerGuard.Client] {line} ({ex.Message})"); }
            }
            // Make sure the chat window is actually visible for a moment.
            try
            {
                var hide = typeof(Chat).GetField("m_hideTimer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                hide?.SetValue(chat, 0f);
            }
            catch { }
        }

        private const float CheatTaintNoticeScale = 1.75f;

        // UnifiedPopup keeps its whole dialog under a private `popupUIParent`; scaling
        // that RectTransform enlarges panel, header, body and button together.
        //
        // The header and the OK button don't need the full enlargement (they are sized
        // fine in vanilla), so they are counter-scaled: the header ends up ~1.15x, the
        // button at its original size. Everything is put back to 1 on OK.
        private const float CheatTaintHeaderNet = 1.15f;
        private const float CheatTaintButtonNet = 1.0f;

        private static void ScaleUnifiedPopup(float scale)
        {
            try
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var instField   = typeof(UnifiedPopup).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                var inst        = instField?.GetValue(null);
                if (inst == null) return;

                var parent = typeof(UnifiedPopup).GetField("popupUIParent", F)?.GetValue(inst) as GameObject;
                if (parent == null) return;
                parent.transform.localScale = new Vector3(scale, scale, 1f);

                bool reset = Math.Abs(scale - 1f) < 0.001f;
                float headerScale = reset ? 1f : CheatTaintHeaderNet / scale;
                float buttonScale = reset ? 1f : CheatTaintButtonNet / scale;

                if (typeof(UnifiedPopup).GetField("headerText", F)?.GetValue(inst) is Component header)
                    header.transform.localScale = new Vector3(headerScale, headerScale, 1f);
                if (typeof(UnifiedPopup).GetField("buttonCenter", F)?.GetValue(inst) is Component button)
                    button.transform.localScale = new Vector3(buttonScale, buttonScale, 1f);
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Popup scale failed: {ex.Message}");
            }
        }

        private static void ShowCheatTaintNotice(string policy)
        {
            string consequence = policy == "strip"
                ? "Flagged items and builds are reported to the server staff automatically, and flagged items are removed from your inventory."
                : policy == "violation"
                    ? "Flagged items and builds are reported to the server staff automatically, flagged items are removed from your inventory, and each occurrence counts as a strike toward a ban."
                    : "Flagged items and builds are reported to the server staff automatically, and depending on server policy flagged items may be removed from your inventory or count as a strike toward a ban.";

            var body =
                "Valheim marks every item, build and creature that comes from a cheat command — spawned items, anything crafted from them, pieces built for free, kills made in god or fly mode. The mark is saved with your character, so it follows gear brought in from single-player.\n\n" +
                "This server reads those marks. " + consequence + "\n\n" +
                "If you have used cheats on this character elsewhere, don't bring that gear here. Play fair and none of this will ever concern you.";

            try
            {
                if (UnifiedPopup.IsAvailable())
                {
                    // The vanilla warning popup is sized for two-line messages; at this
                    // length the text is unreadable. Scale the whole popup up while ours
                    // is showing (font scales with it) and put it back on OK so every
                    // vanilla popup that follows looks normal.
                    UnifiedPopup.Push(new WarningPopup("Cheat detection is active on this server", body,
                        () => { ScaleUnifiedPopup(1f); try { UnifiedPopup.Pop(); } catch { } }, localizeText: false));
                    ScaleUnifiedPopup(CheatTaintNoticeScale);
                    return;
                }
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Notice popup unavailable ({ex.Message}); falling back to chat.");
            }

            // Fallback: the same text in the chat window.
            try
            {
                Chat.instance?.AddString("<color=#ffd700>[ServerGuard]</color> Cheat detection is active on this server. " + body.Replace("\n\n", " "));
            }
            catch { }
        }

        // One console line telling staff what they just got, printed when the grant
        // changes (connect, or a hot-reload that promoted / demoted them).
        private static void AnnounceDevGrant()
        {
            try
            {
                var key = _devMode == "list" ? "list:" + string.Join(",", _devCommands.OrderBy(c => c)) : _devMode;
                if (key == _devModeAnnounced) return;
                _devModeAnnounced = key;

                if (_devMode == "all")
                    Instance?.DisplayAdminReply("[ServerGuard] Dev commands unlocked for you (owner). Type `devcommands` to enable them.");
                else if (_devMode == "list")
                    Instance?.DisplayAdminReply("[ServerGuard] Dev commands available to you (moderator): "
                        + string.Join(", ", _devCommands.OrderBy(c => c)) + ". Type `devcommands` to enable them.");
            }
            catch { }
        }

        private static readonly string ConfDir    = Path.Combine(Paths.ConfigPath, "ServerGuard");
        private static readonly string ClientYaml = Path.Combine(ConfDir, "client.yaml");
        // Drop-in YAML snippet listing every plugin currently loaded on this client.
        // Generated on first run (and any time the file is missing). The user pastes
        // its contents into the server's allowed_mods.yaml.
        private static readonly string ExportYaml = Path.Combine(ConfDir, "mods_for_allowed_mods.yaml");

        private class ClientSettings
        {
            public string SharedSecret { get; set; } = "";

            // Quick Login panel (shown on the game's title screen)
            public bool   QuickLoginEnabled   { get; set; } = false;
            public string ServerAddress       { get; set; } = "";
            public int    ServerPort          { get; set; } = 2456;
            public string ServerPassword      { get; set; } = "";
            public string ServerName          { get; set; } = "";
            public string ServerDescription   { get; set; } = "";
            // PNG/JPG filename relative to BepInEx/config/ServerGuard/. Empty = no logo.
            public string ServerLogoPath      { get; set; } = "";
            // Free-form multi-line text shown in the scrollable "Announcements" box
            // below the description. Supports [label](https://url) links and TMP rich
            // text. Empty = the whole Announcements section is omitted.
            public string ServerAnnouncements { get; set; } = "";
        }

        private void Awake()
        {
            Instance = this;
            LogS = ServerGuardPlugin.Log;

            EnsureConfig();

            // Only the patch classes nested in THIS type - the server half's patches
            // must never be applied on a client (see ServerGuardPlugin.PatchNested).
            _harmony = new Harmony(GUID + ".client");
            var patched = ServerGuardPlugin.PatchNested(_harmony, typeof(ClientPlugin));
            LogS.LogInfo($"[ServerGuard.Client] Applied {patched} client-side Harmony patch class(es).");

            // Don't enumerate Chainloader.PluginInfos yet - we may have loaded earlier
            // than other plugins in this BepInEx session (alphabetical order, dependencies),
            // so PluginInfos is incomplete *during* Awake. Defer to a coroutine that runs
            // after the chainloader has had time to finish its work.
            StartCoroutine(DeferredInit());
        }

        // Waits until BepInEx has loaded every other plugin, then builds the manifest
        // cache and writes the first-run allowed_mods export.
        //
        // BepInEx 5.x calls every plugin's Awake() back-to-back on the main thread before
        // returning control to Unity, so a single `yield return null` (one frame) is
        // already past the point where PluginInfos is complete. We add a small WaitForSeconds
        // safety margin in case a plugin's Awake itself yielded.
        private IEnumerator DeferredInit()
        {
            yield return null;
            yield return new WaitForSeconds(2f);

            BuildManifestCache();
            ExportAllowedModsSnippet();

            // Skill-level cap reporter (#10). Background coroutine that periodically
            // packages the local player's skill levels and sends them to the server.
            StartCoroutine(SkillReportLoop());

            // Cheat-taint reporter (2.0): what the game itself has marked as cheated.
            StartCoroutine(CheatStateLoop());

            // Compute and log this client's modset fingerprint (#2). Players can compare
            // the short value against the one the server admin publishes (or against
            // modset_fingerprint.txt in their server's ServerGuard config folder).
            string shortLoose = "", shortStrict = "";
            try
            {
                var pairs = (_cachedManifest ?? new List<ModManifestEntry>())
                    .Select(m => new KeyValuePair<string, string>(
                        !string.IsNullOrEmpty(m.Guid) ? m.Guid : (m.Name ?? ""),
                        m.Sha256 ?? ""))
                    .ToList();
                shortLoose  = ModsetFingerprint.Short(ModsetFingerprint.ComputeLoose(pairs));
                shortStrict = ModsetFingerprint.Short(ModsetFingerprint.ComputeStrict(pairs));
            }
            catch (Exception ex)
            {
                LogS.LogWarning($"[ServerGuard.Client] Fingerprint compute failed: {ex.Message}");
            }

            LogS.LogInfo($"[ServerGuard.Client] Loaded v{VERSION}. Manifest entries: {_cachedManifest?.Count ?? 0}. HMAC: {(string.IsNullOrEmpty(_sharedSecret) ? "OFF (no shared_secret configured)" : "ON")}");
            LogS.LogInfo($"[ServerGuard.Client] Modset fingerprint  loose={shortLoose}  strict={shortStrict}");
        }

        // Writes a YAML snippet listing every loaded plugin in the exact format the
        // server's allowed_mods.yaml expects. Idempotent: only writes when the export
        // file is missing, so the user can delete it to refresh after adding/removing mods.
        private void ExportAllowedModsSnippet()
        {
            try
            {
                if (File.Exists(ExportYaml))
                {
                    LogS.LogInfo($"[ServerGuard.Client] Allowed-mods export already present at {ExportYaml}. Delete the file to regenerate.");
                    return;
                }

                var entries = _cachedManifest ?? new List<ModManifestEntry>();
                var sb = new StringBuilder();
                sb.AppendLine($"# ServerGuard - allowed_mods snippet generated by ServerGuard.Client v{VERSION}");
                sb.AppendLine($"# Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z   Mods on this client: {entries.Count}");
                sb.AppendLine("#");
                sb.AppendLine("# How to use:");
                sb.AppendLine("#   1. Open <server>/BepInEx/config/ServerGuard/conf/allowed_mods.yaml");
                sb.AppendLine("#   2. Replace the `allowed_mods:` block with the one below");
                sb.AppendLine("#      (or merge if you already have entries you want to keep).");
                sb.AppendLine("#   3. Save. The server hot-reloads within ~1 second.");
                sb.AppendLine("#");
                sb.AppendLine("# Each entry is `<GUID>|<sha256>` (GUID-keyed, hash-pinned).");
                sb.AppendLine("# To loosen, drop the `|<sha256>` suffix - the entry will then accept any hash.");
                sb.AppendLine("# To tighten further, leave it as-is - the server will require an exact DLL match.");
                sb.AppendLine("#");
                sb.AppendLine("# ServerGuard itself (this DLL, the same mod the server runs) is intentionally listed");
                sb.AppendLine("# under required_mods, NOT allowed_mods - the server demands its presence.");
                sb.AppendLine();

                // Required: just the companion itself, hash-pinned to this client's build.
                var companion = entries.FirstOrDefault(m => string.Equals(m.Guid, GUID, StringComparison.OrdinalIgnoreCase));
                sb.AppendLine("required_mods:");
                if (companion != null && !string.IsNullOrEmpty(companion.Sha256))
                {
                    sb.AppendLine($"  - {companion.Guid}|{companion.Sha256}    # {companion.Name} v{companion.Version}");
                }
                else
                {
                    sb.AppendLine($"  - {GUID}                                                # {NAME} v{VERSION}");
                }
                sb.AppendLine();

                // Allowed: every other plugin currently loaded.
                sb.AppendLine("allowed_mods:");
                var others = entries
                    .Where(m => !string.Equals(m.Guid, GUID, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m.Name ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (others.Count == 0)
                {
                    sb.AppendLine("  []");
                }
                else
                {
                    // Compute padding so the trailing comments line up nicely.
                    int maxKeyLen = 0;
                    foreach (var m in others)
                    {
                        var key = !string.IsNullOrEmpty(m.Guid) ? m.Guid : m.Name;
                        var entryWidth = (key ?? "").Length + (string.IsNullOrEmpty(m.Sha256) ? 0 : 1 + m.Sha256.Length);
                        if (entryWidth > maxKeyLen) maxKeyLen = entryWidth;
                    }

                    foreach (var m in others)
                    {
                        var keyOnly = !string.IsNullOrEmpty(m.Guid) ? m.Guid : (m.Name ?? "");
                        var entry   = string.IsNullOrEmpty(m.Sha256) ? keyOnly : $"{keyOnly}|{m.Sha256}";
                        var pad     = new string(' ', Math.Max(1, maxKeyLen - entry.Length + 2));
                        var label   = string.IsNullOrEmpty(m.Name) ? "" : $"{m.Name} v{m.Version}";
                        if (string.IsNullOrEmpty(m.Guid))
                        {
                            // Fall back to display-name match - flag it so the user can replace later.
                            sb.AppendLine($"  - {entry}{pad}# {label} (no GUID; consider replacing the key with the mod's BepInPlugin GUID)");
                        }
                        else
                        {
                            sb.AppendLine($"  - {entry}{pad}# {label}");
                        }
                    }
                }
                sb.AppendLine();

                sb.AppendLine("banned_mods: []");
                sb.AppendLine();

                Directory.CreateDirectory(ConfDir);
                File.WriteAllText(ExportYaml, sb.ToString());

                LogS.LogWarning("[ServerGuard.Client] First-run mod export written:");
                LogS.LogWarning($"[ServerGuard.Client]   {ExportYaml}");
                LogS.LogWarning($"[ServerGuard.Client]   ({entries.Count} plugins). Paste its contents into the server's allowed_mods.yaml.");
            }
            catch (Exception ex)
            {
                LogS.LogError($"[ServerGuard.Client] ExportAllowedModsSnippet failed: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
        }

        private void EnsureConfig()
        {
            try
            {
                Directory.CreateDirectory(ConfDir);

                if (!File.Exists(ClientYaml))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("# Valheim ServerGuard - Client config");
                    sb.AppendLine("");
                    sb.AppendLine("# sharedSecret MUST match the server's settings.yaml `sharedSecret` value");
                    sb.AppendLine("# verbatim. The server will reject manifests whose HMAC does not match.");
                    sb.AppendLine("# Leave empty only if the server has `requireHmac: false` (insecure).");
                    sb.AppendLine("sharedSecret: \"\"");
                    sb.AppendLine("");
                    sb.AppendLine("# ---------------------------------------------------------------");
                    sb.AppendLine("# Quick Login panel (title screen)");
                    sb.AppendLine("# When enabled, a panel is shown on the main menu so players can");
                    sb.AppendLine("# connect to your server with one click - no IP/password dialog.");
                    sb.AppendLine("# ---------------------------------------------------------------");
                    sb.AppendLine("quickLoginEnabled: false");
                    sb.AppendLine("serverAddress: \"\"       # e.g. 192.168.1.1 or my.server.com");
                    sb.AppendLine("serverPort: 2456");
                    sb.AppendLine("serverPassword: \"\"     # stored in plain text; leave empty for public servers");
                    sb.AppendLine("serverName: \"\"         # displayed as the panel heading");
                    sb.AppendLine("serverDescription: \"\" # shown below the name");
                    sb.AppendLine("serverLogoPath: \"\"    # PNG/JPG filename in BepInEx/config/ServerGuard/");
                    sb.Append(AnnouncementsYamlBlock());
                    File.WriteAllText(ClientYaml, sb.ToString());
                }
                else
                {
                    // Migration: client.yaml is only written when missing, so an existing
                    // file from an older version has no announcements block. Append the
                    // commented template once so the key is discoverable in the editor
                    // rather than only in the docs.
                    var existing = File.ReadAllText(ClientYaml);
                    if (existing.IndexOf("serverAnnouncements", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        if (!existing.EndsWith("\n")) existing += Environment.NewLine;
                        File.WriteAllText(ClientYaml, existing + AnnouncementsYamlBlock());
                        LogS.LogInfo("[ServerGuard.Client] Added the serverAnnouncements block to client.yaml.");
                    }
                }

                var deser = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var doc = deser.Deserialize<ClientSettings>(File.ReadAllText(ClientYaml)) ?? new ClientSettings();
                _sharedSecret   = doc.SharedSecret ?? "";
                _clientSettings = doc;
            }
            catch (Exception ex)
            {
                LogS.LogWarning($"[ServerGuard.Client] EnsureConfig failed: {ex.Message}");
            }
        }

        // The announcements editor: a YAML block scalar in client.yaml, so the text is
        // edited in the same file as the rest of the Quick Login panel settings.
        //
        // `|` (literal block) keeps every line break as typed - that's what makes this
        // usable as a plain text box. Every line of the value must be indented by two
        // spaces; YAML strips that indent back off when reading.
        private static string AnnouncementsYamlBlock()
        {
            var sb = new StringBuilder();
            sb.AppendLine("");
            sb.AppendLine("# ---------------------------------------------------------------");
            sb.AppendLine("# Announcements (scrollable box under the description)");
            sb.AppendLine("#");
            sb.AppendLine("# Write as many lines as you like - the box scrolls (mouse wheel or");
            sb.AppendLine("# the scrollbar on its right edge). Leave empty to hide the section.");
            sb.AppendLine("#");
            sb.AppendLine("# Links:   [label](https://example.com)  -> clickable, opens a browser.");
            sb.AppendLine("#          Only http:// and https:// links are opened.");
            sb.AppendLine("# Styling: <b>bold</b>, <i>italic</i>, <color=#ffcc00>colour</color>.");
            sb.AppendLine("#");
            sb.AppendLine("#");
            sb.AppendLine("# To use it, replace the \"\" below with a `|` block and indent EVERY");
            sb.AppendLine("# line of text by two spaces:");
            sb.AppendLine("#");
            sb.AppendLine("#   serverAnnouncements: |");
            sb.AppendLine("#     <b>Welcome!</b>");
            sb.AppendLine("#     Server wipe: never. Raids: on.");
            sb.AppendLine("#");
            sb.AppendLine("#     Join our [Discord](https://discord.gg/example) for events.");
            sb.AppendLine("# ---------------------------------------------------------------");
            sb.AppendLine("serverAnnouncements: \"\"");
            return sb.ToString();
        }

        private void BuildManifestCache()
        {
            _cachedManifest = new List<ModManifestEntry>();
            try
            {
                foreach (var kv in Chainloader.PluginInfos)
                {
                    var info = kv.Value;
                    var meta = info?.Metadata;
                    string sha = "";

                    try
                    {
                        var path = info?.Location;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                        {
                            using (var sha256 = SHA256.Create())
                            using (var stream = File.OpenRead(path))
                            {
                                var hash = sha256.ComputeHash(stream);
                                sha = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                            }
                        }
                    }
                    catch { /* hash optional */ }

                    _cachedManifest.Add(new ModManifestEntry
                    {
                        Guid    = meta?.GUID ?? "",
                        Name    = meta?.Name ?? "",
                        Version = meta?.Version?.ToString() ?? "",
                        Sha256  = sha
                    });
                }
            }
            catch (Exception ex)
            {
                LogS.LogError($"[ServerGuard.Client] BuildManifestCache failed: {ex.Message}");
            }
        }

        public string BuildManifestJson(string challenge)
        {
            // Always rebuild from Chainloader.PluginInfos at request time. By the time
            // the server has asked for a manifest the player is past the main menu, so
            // every plugin is loaded - we don't want to ship a stale 10-of-29 list that
            // happened to be visible when our Awake ran.
            BuildManifestCache();

            var manifest = new ModManifest
            {
                SchemaVersion = "1",
                Challenge     = challenge ?? "",
                TimestampUtc  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Mods          = _cachedManifest ?? new List<ModManifestEntry>()
            };
            manifest.Hmac = ModManifest.ComputeHmac(manifest.CanonicalForHmac(), _sharedSecret);
            return JsonConvert.SerializeObject(manifest);
        }

        // -------------- Admin console commands (#16) --------------
        //
        // The local player types `sg ...` (or `/sg ...`) in Valheim's CONSOLE (F5).
        // We intercept inside the existing Terminal.TryRunCommand patch and forward
        // the text via ServerGuard_AdminCommand. Reply is displayed in the console.
        //
        // Why console, not chat: console is admin-oriented by convention, doesn't risk
        // leaking to other players if our intercept ever fails, and has its own scroll
        // history separate from chat.
        //
        // Trust: anyone can TYPE the command. The server checks IsAdmin(steamId)
        // before executing anything; non-admins get a single "not an admin" reply.

        // Forwards an admin command to the server. Called by the Terminal patch below.
        internal void SendAdminCommand(string command)
        {
            if (!IsActiveMultiplayerClient())
            {
                DisplayAdminReply("[ServerGuard] sg commands only work while connected to a multiplayer server.");
                return;
            }
            if (_serverRpc == null)
            {
                DisplayAdminReply("[ServerGuard] Not connected to a server peer yet.");
                return;
            }
            try
            {
                _serverRpc.Invoke("ServerGuard_AdminCommand", command ?? "");
                LogS?.LogInfo($"[ServerGuard.Client] Sent admin command: {command}");
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Admin command send failed: {ex.Message}");
                DisplayAdminReply($"[ServerGuard] Send failed: {ex.Message}");
            }
        }

        // Reflection handle for whichever AddString/Print method Valheim's Console
        // exposes. Resolved on first use. We go through reflection to dodge the
        // PlatformUserID overload-resolution issue we hit with Chat.AddString.
        private static System.Reflection.MethodInfo _consoleWriteMethod;
        private static int _consoleWriteArity;
        private static object _consoleInstance;

        // Displays text in the LOCAL player's CONSOLE only - no network broadcast.
        // Server-sent admin replies arrive as one big \n-separated string; split and
        // display each line.
        internal void DisplayAdminReply(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var lines = text.Split('\n');
            try
            {
                var console = ResolveConsoleInstance();
                if (console == null || _consoleWriteMethod == null)
                {
                    foreach (var line in lines) LogS?.LogInfo($"[ServerGuard] {line}");
                    return;
                }

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        object[] args = _consoleWriteArity == 1
                            ? new object[] { line }
                            : new object[] { line, /* timestamp */ false };
                        _consoleWriteMethod.Invoke(console, args);
                    }
                    catch
                    {
                        LogS?.LogInfo($"[ServerGuard] {line}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] DisplayAdminReply error: {ex.Message}");
            }
        }

        // Finds Valheim's player Console instance and caches a method handle for
        // writing a string to it. Tries Console.instance first, falls back to any
        // active Terminal in the scene.
        private object ResolveConsoleInstance()
        {
            if (_consoleInstance != null && _consoleWriteMethod != null) return _consoleInstance;

            // Find the Console type via Terminal's assembly (avoids clashing with
            // System.Console under `using System;`).
            var consoleType = typeof(Terminal).Assembly.GetType("Console");
            object inst = null;
            if (consoleType != null)
            {
                try
                {
                    var prop = consoleType.GetProperty("instance",
                        System.Reflection.BindingFlags.Static
                      | System.Reflection.BindingFlags.Public
                      | System.Reflection.BindingFlags.NonPublic);
                    if (prop != null) inst = prop.GetValue(null);
                }
                catch { }

                if (inst == null)
                {
                    try
                    {
                        var fld = consoleType.GetField("m_instance",
                            System.Reflection.BindingFlags.Static
                          | System.Reflection.BindingFlags.Public
                          | System.Reflection.BindingFlags.NonPublic);
                        if (fld != null) inst = fld.GetValue(null);
                    }
                    catch { }
                }
            }

            // Fallback: any active Terminal in the scene (chat or console).
            if (inst == null)
            {
                try { inst = UnityEngine.Object.FindObjectOfType<Terminal>(); }
                catch { }
            }
            if (inst == null) return null;

            // Find a callable write method. Prefer single-string overloads.
            System.Reflection.MethodInfo chosen = null;
            int arity = 0;
            foreach (var name in new[] { "Print", "AddString" })
            {
                foreach (var m in inst.GetType().GetMethods(System.Reflection.BindingFlags.Instance
                                                           | System.Reflection.BindingFlags.Public
                                                           | System.Reflection.BindingFlags.NonPublic))
                {
                    if (m.Name != name) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    {
                        chosen = m;
                        arity = 1;
                        break;
                    }
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(bool))
                    {
                        if (chosen == null) { chosen = m; arity = 2; }
                    }
                }
                if (chosen != null && arity == 1) break;
            }

            if (chosen == null) return null;

            _consoleInstance    = inst;
            _consoleWriteMethod = chosen;
            _consoleWriteArity  = arity;
            return _consoleInstance;
        }

        // -------------- Build-log: place reporter (#14) --------------
        //
        // Patch Player.PlacePiece Postfix. When Valheim says placement succeeded, we
        // send the prefab name + world position to the server via ServerGuard_BuildPlace.
        // Server logs it to its daily CSV.

        // Player.PlacePiece in current Valheim is a VOID method that performs the
        // actual placement (4-arg overload taking Piece, Vector3, Quaternion, bool).
        // The "should we place?" gating happens upstream. By the time we run our
        // Postfix the piece has been placed, so we don't need a __result check.
        //
        // IMPORTANT: the `piece` parameter is the PREFAB TEMPLATE - its transform is
        // at the prefab's origin (0,0,0). The real world position is the `pos`
        // argument. We pull that by Harmony parameter-name binding.
        //
        // We MUST NOT declare a `bool __result` parameter - HarmonyX rejects that
        // signature against a void method ("Cannot get result from void method")
        // and aborts patching of this whole class.
        //
        // Valheim 1.0 added a trailing `bool cheated` (nocost, or cheated materials) and
        // stamps it onto the new piece's ZDO. It is read from `__args` rather than bound
        // by name so the patch still attaches on a build without it.
        [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
        public static class Patch_PlacePiece_Report
        {
            public static void Postfix(Player __instance, Piece piece, Vector3 pos, object[] __args)
            {
                try
                {
                    if (!IsActiveMultiplayerClient()) return;
                    if (__instance == null) return;
                    if (__instance != Player.m_localPlayer) return;
                    if (piece == null) return;

                    var pieceName = piece.gameObject?.name ?? "unknown";
                    var cloneIdx = pieceName.IndexOf("(Clone)", StringComparison.Ordinal);
                    if (cloneIdx > 0) pieceName = pieceName.Substring(0, cloneIdx).Trim();

                    bool cheated = false;
                    try
                    {
                        // The last parameter is `cheated` on 1.0+; on older builds the
                        // last one is `doAttack`, so require it to be the 5th argument.
                        if (__args != null && __args.Length >= 5 && __args[4] is bool c) cheated = c;
                    }
                    catch { }

                    ClientPlugin.Instance?.SendBuildPlace(pieceName, pos, cheated);
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] PlacePiece hook error: {ex.Message}");
                }
            }
        }

        internal void SendBuildPlace(string pieceName, Vector3 pos, bool cheated = false)
        {
            if (_serverRpc == null) return;
            try
            {
                var name = SanitiseShort(pieceName, 64);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                // Trailing `cheated` field is new in 2.0; older servers ignore it.
                var payload = string.Format(inv,
                    "{0}|{1:F1}|{2:F1}|{3:F1}|{4}",
                    name, pos.x, pos.y, pos.z, cheated ? "1" : "0");
                _serverRpc.Invoke("ServerGuard_BuildPlace", payload);
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] BuildPlace RPC failed: {ex.Message}");
            }
        }

        internal void SendBuildDestroy(string pieceName, Vector3 pos, string attackerKind, string attackerLabel)
        {
            if (_serverRpc == null) return;
            try
            {
                var name  = SanitiseShort(pieceName, 64);
                var kind  = SanitiseShort(attackerKind ?? "unknown", 16);
                var label = SanitiseShort(attackerLabel ?? "", 48);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var payload = string.Format(inv,
                    "{0}|{1:F1}|{2:F1}|{3:F1}|{4}|{5}",
                    name, pos.x, pos.y, pos.z, kind, label);
                _serverRpc.Invoke("ServerGuard_BuildDestroy", payload);
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] BuildDestroy RPC failed: {ex.Message}");
            }
        }

        private static string SanitiseShort(string s, int max)
        {
            var v = (s ?? "").Replace('|', ' ').Replace('\n', ' ').Trim();
            if (v.Length > max) v = v.Substring(0, max);
            return v;
        }

        // -------------- Build-log: destroy reporter (#14) --------------
        //
        // Patch WearNTear.Destroy on the CLIENT side. The patch fires whenever a
        // piece is destroyed on this machine - which is whenever the local client
        // is the ZDO owner of that piece (the common case for pieces near a player).
        //
        // This single hook covers:
        //   - Weapon-destroyed (HP -> 0 from local-player damage)
        //   - Hammer-removed (WearNTear.Remove() routes through Destroy() on the owner)
        //   - Creature-destroyed (a Troll smashes your wall - the creature is the
        //     attacker, you're the ZDO owner, so Destroy fires on YOUR machine)
        //
        // To distinguish player vs creature attribution, we keep a last-hit table
        // populated by a Damage Prefix. On hammer-remove (no Damage call) we fall
        // back to "self" attribution. The kind + label travel over the RPC so the
        // server can write the correct row in the CSV.

        private sealed class LastHitInfo
        {
            public Character Attacker;
            public DateTime At;
        }

        // ConditionalWeakTable keys on the WearNTear instance and auto-clears when
        // the GameObject is destroyed (which always happens shortly after Destroy()).
        // No manual cleanup, no instance-id collisions.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WearNTear, LastHitInfo> _clientLastHitOnPiece
            = new System.Runtime.CompilerServices.ConditionalWeakTable<WearNTear, LastHitInfo>();

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Damage))]
        public static class Patch_WearNTear_Damage_TrackClient
        {
            public static void Prefix(WearNTear __instance, HitData hit)
            {
                try
                {
                    if (!IsActiveMultiplayerClient()) return;
                    if (__instance == null || hit == null) return;

                    Character attacker = null;
                    try { attacker = hit.GetAttacker(); }
                    catch { }

                    var info = new LastHitInfo { Attacker = attacker, At = DateTime.UtcNow };
                    _clientLastHitOnPiece.Remove(__instance);
                    _clientLastHitOnPiece.Add(__instance, info);
                }
                catch { /* never let the hook throw */ }
            }
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
        public static class Patch_WearNTear_Destroy_ClientReport
        {
            public static void Prefix(WearNTear __instance)
            {
                try
                {
                    if (!IsActiveMultiplayerClient()) return; // skip on host / single-player
                    if (__instance == null) return;

                    var pieceName = __instance.gameObject?.name ?? "unknown";
                    var cloneIdx = pieceName.IndexOf("(Clone)", StringComparison.Ordinal);
                    if (cloneIdx > 0) pieceName = pieceName.Substring(0, cloneIdx).Trim();

                    Vector3 pos;
                    try { pos = __instance.transform.position; }
                    catch { return; }

                    // Figure out who killed this piece.
                    string kind  = "unknown";
                    string label = "";

                    if (_clientLastHitOnPiece.TryGetValue(__instance, out var info) && info != null)
                    {
                        _clientLastHitOnPiece.Remove(__instance);
                        var ch = info.Attacker;
                        if (ch != null)
                        {
                            if (ch is Player ap)
                            {
                                if (ap == Player.m_localPlayer)
                                {
                                    kind  = "self";
                                    label = "";   // server fills in from RPC sender
                                }
                                else
                                {
                                    kind  = "player";
                                    try { label = ap.GetPlayerName() ?? ""; } catch { label = ""; }
                                }
                            }
                            else
                            {
                                kind = "creature";
                                try { label = ch.GetHoverName() ?? ch.name ?? ""; }
                                catch { label = ch.name ?? ""; }
                                // Strip "(Clone)" if the hover name fell back to GO name.
                                var idx = label.IndexOf("(Clone)", StringComparison.Ordinal);
                                if (idx > 0) label = label.Substring(0, idx).Trim();
                            }
                        }
                    }

                    // No Damage record means hammer-remove (or some other non-damage
                    // path). Attribute to the local player.
                    if (kind == "unknown")
                    {
                        kind  = "self";
                        label = "";
                    }

                    ClientPlugin.Instance?.SendBuildDestroy(pieceName, pos, kind, label);
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] WearNTear.Destroy hook error: {ex.Message}");
                }
            }
        }

        // -------------- Player death report --------------
        //
        // When the LOCAL player dies on a multiplayer client, send a death report to
        // the server. The server formats and posts to public Discord.
        //
        // Payload format (pipe-separated, invariant-culture floats):
        //   posX|posY|posZ|attackerKind|attackerLabel|causeHint
        //
        // We use reflection to read m_lastHit (Player) and its fields, because field
        // visibility on these types varies across Valheim builds.

        // Cached reflection handles for the death report path.
        private static System.Reflection.FieldInfo _playerLastHitField;
        private static System.Reflection.MethodInfo _hitGetAttackerMethod;
        private static System.Reflection.FieldInfo _hitDamageField;

        // Player.OnDeath is `protected`, so nameof can't see it. String literal works
        // because Harmony resolves the target by reflection at patch-attach time.
        [HarmonyPatch(typeof(Player), "OnDeath")]
        public static class Patch_Player_OnDeath_Report
        {
            // Prefix so we read m_lastHit BEFORE the death sequence clears it.
            public static void Prefix(Player __instance)
            {
                try
                {
                    if (__instance == null) return;
                    if (__instance != Player.m_localPlayer) return;
                    if (!IsActiveMultiplayerClient()) return;

                    ClientPlugin.Instance?.SendDeathReport(__instance);
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Death hook error: {ex.Message}");
                }
            }
        }

        internal void SendDeathReport(Player p)
        {
            if (_serverRpc == null) return;

            try
            {
                var pos = p.transform.position;

                string attackerKind  = "environment";
                string attackerLabel = "";
                string causeHint     = "";

                // m_lastHit lookup. Cached after first resolution.
                if (_playerLastHitField == null)
                {
                    foreach (var f in typeof(Player).GetFields(System.Reflection.BindingFlags.Instance
                                                              | System.Reflection.BindingFlags.Public
                                                              | System.Reflection.BindingFlags.NonPublic))
                    {
                        if (f.FieldType == typeof(HitData))
                        {
                            _playerLastHitField = f;
                            break;
                        }
                    }
                }

                object lastHit = _playerLastHitField?.GetValue(p);
                if (lastHit is HitData hit && hit != null)
                {
                    // Cause hint = dominant damage type (best-effort).
                    causeHint = DominantDamageType(hit);

                    // Resolve attacker.
                    if (_hitGetAttackerMethod == null)
                    {
                        _hitGetAttackerMethod = typeof(HitData).GetMethod("GetAttacker",
                            System.Reflection.BindingFlags.Instance
                          | System.Reflection.BindingFlags.Public
                          | System.Reflection.BindingFlags.NonPublic);
                    }

                    Character attacker = null;
                    if (_hitGetAttackerMethod != null)
                    {
                        try { attacker = _hitGetAttackerMethod.Invoke(hit, null) as Character; }
                        catch { /* attacker may be unresolvable (left zone, despawned) */ }
                    }

                    if (attacker != null)
                    {
                        if (attacker is Player ap)
                        {
                            if (ap == p)
                            {
                                attackerKind  = "self";
                                attackerLabel = "";
                            }
                            else
                            {
                                attackerKind  = "player";
                                attackerLabel = ap.GetPlayerName() ?? "";
                            }
                        }
                        else
                        {
                            attackerKind = "creature";
                            // Hover name returns the localized display name like "Skeleton".
                            try { attackerLabel = attacker.GetHoverName() ?? attacker.name ?? ""; }
                            catch { attackerLabel = attacker.name ?? ""; }
                        }
                    }
                }

                // Strip our delimiter chars from any client-supplied string.
                attackerLabel = (attackerLabel ?? "").Replace('|', ' ').Replace('\n', ' ').Trim();
                causeHint     = (causeHint     ?? "").Replace('|', ' ').Replace('\n', ' ').Trim();

                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var payload = string.Format(inv,
                    "{0:F1}|{1:F1}|{2:F1}|{3}|{4}|{5}",
                    pos.x, pos.y, pos.z,
                    attackerKind, attackerLabel, causeHint);

                try
                {
                    _serverRpc.Invoke("ServerGuard_PlayerDeath", payload);
                    LogS.LogInfo($"[ServerGuard.Client] Death report sent ({attackerKind} / {attackerLabel} / {causeHint}).");
                }
                catch (Exception ex)
                {
                    LogS?.LogWarning($"[ServerGuard.Client] Death report RPC failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] SendDeathReport error: {ex.Message}");
            }
        }

        // Reads HitData.m_damage and returns the name of the damage type with the
        // highest amount. Uses reflection because the struct layout name changes.
        private static string DominantDamageType(HitData hit)
        {
            if (hit == null) return "";

            // Cache the m_damage field once.
            if (_hitDamageField == null)
            {
                foreach (var f in typeof(HitData).GetFields(System.Reflection.BindingFlags.Instance
                                                           | System.Reflection.BindingFlags.Public
                                                           | System.Reflection.BindingFlags.NonPublic))
                {
                    if (f.Name.Equals("m_damage", StringComparison.OrdinalIgnoreCase))
                    {
                        _hitDamageField = f;
                        break;
                    }
                }
            }
            if (_hitDamageField == null) return "";

            object dmg;
            try { dmg = _hitDamageField.GetValue(hit); }
            catch { return ""; }
            if (dmg == null) return "";

            // Iterate float fields on the damage struct: m_blunt, m_slash, m_pierce,
            // m_chop, m_pickaxe, m_fire, m_frost, m_lightning, m_poison, m_spirit.
            string topName = "";
            float topVal = 0f;
            foreach (var f in dmg.GetType().GetFields(System.Reflection.BindingFlags.Instance
                                                     | System.Reflection.BindingFlags.Public
                                                     | System.Reflection.BindingFlags.NonPublic))
            {
                if (f.FieldType != typeof(float)) continue;
                float v;
                try { v = (float)f.GetValue(dmg); }
                catch { continue; }
                if (v > topVal)
                {
                    topVal  = v;
                    topName = f.Name;
                }
            }

            // Strip leading "m_" if present and TitleCase.
            if (topName.StartsWith("m_", StringComparison.Ordinal)) topName = topName.Substring(2);
            if (topName.Length > 0) topName = char.ToUpperInvariant(topName[0]) + topName.Substring(1);
            return topName;
        }

        // -------------- Skill report loop (#10) --------------
        //
        // Every SkillReportIntervalSeconds, package the local player's m_skills levels
        // and send them to the server via ServerGuard_SkillReport. Skips when no local
        // player is spawned (main menu / loading) or no server RPC is bound (single-
        // player / host).
        //
        // The companion plugin is the only window the server has into a player's
        // skills - they live entirely client-side in Valheim. Trust is the same as
        // the manifest pipeline: the server requires the companion (RequireCompanion)
        // and hash-pins it (HashMismatch), so a forged or modified companion is
        // already kicked before it can lie about skills.

        private const float SkillReportIntervalSeconds = 60f;

        private IEnumerator SkillReportLoop()
        {
            // Wait for the player to actually spawn before the first report.
            yield return new WaitForSeconds(15f);

            while (true)
            {
                yield return new WaitForSeconds(SkillReportIntervalSeconds);
                try { SendSkillReportNow(); }
                catch (Exception ex) { LogS?.LogWarning($"[ServerGuard.Client] Skill report tick error: {ex.Message}"); }
            }
        }

        // Cached reflection handles. Resolved once on first use and reused. `m_skills`
        // is the field name in most Valheim builds; we still hunt by Skills type so a
        // rename doesn't kill us.
        private static System.Reflection.FieldInfo _playerSkillsField;
        private static System.Reflection.FieldInfo _skillsDataField;
        private static System.Reflection.FieldInfo _skillLevelField;

        private static Skills GetPlayerSkills(Player p)
        {
            if (p == null) return null;
            if (_playerSkillsField == null)
            {
                // Look for any field of type Skills on Player. Handles m_skills, Skills,
                // _skills, etc.
                foreach (var f in typeof(Player).GetFields(System.Reflection.BindingFlags.Instance
                                                           | System.Reflection.BindingFlags.Public
                                                           | System.Reflection.BindingFlags.NonPublic))
                {
                    if (f.FieldType == typeof(Skills))
                    {
                        _playerSkillsField = f;
                        break;
                    }
                }
                if (_playerSkillsField == null) return null;
            }
            return _playerSkillsField.GetValue(p) as Skills;
        }

        // Pulls the (SkillType, level) pairs out of a Skills instance via reflection on
        // its dictionary field, which is typically `m_skillData` (Dictionary<SkillType, Skill>).
        private static IEnumerable<KeyValuePair<string, float>> EnumerateSkills(Skills skills)
        {
            if (skills == null) yield break;

            if (_skillsDataField == null)
            {
                foreach (var f in typeof(Skills).GetFields(System.Reflection.BindingFlags.Instance
                                                          | System.Reflection.BindingFlags.Public
                                                          | System.Reflection.BindingFlags.NonPublic))
                {
                    if (typeof(System.Collections.IDictionary).IsAssignableFrom(f.FieldType))
                    {
                        _skillsDataField = f;
                        break;
                    }
                }
                if (_skillsDataField == null) yield break;
            }

            var dict = _skillsDataField.GetValue(skills) as System.Collections.IDictionary;
            if (dict == null) yield break;

            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                var name = entry.Key?.ToString();
                if (string.IsNullOrEmpty(name)) continue;

                var skillObj = entry.Value;
                if (skillObj == null) continue;

                // Find m_level on the Skill instance the first time we see one, then cache.
                if (_skillLevelField == null)
                {
                    foreach (var f in skillObj.GetType().GetFields(System.Reflection.BindingFlags.Instance
                                                                   | System.Reflection.BindingFlags.Public
                                                                   | System.Reflection.BindingFlags.NonPublic))
                    {
                        if (f.FieldType == typeof(float) && f.Name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            _skillLevelField = f;
                            break;
                        }
                    }
                    if (_skillLevelField == null) yield break;
                }

                float level = 0f;
                try { level = (float)_skillLevelField.GetValue(skillObj); }
                catch { continue; }

                yield return new KeyValuePair<string, float>(name, level);
            }
        }

        private void SendSkillReportNow()
        {
            if (_serverRpc == null) return;                       // not connected
            var p = Player.m_localPlayer;
            if (p == null) return;                                 // no spawned character yet
            var skills = GetPlayerSkills(p);
            if (skills == null) return;

            var sb = new StringBuilder();
            bool first = true;
            foreach (var kv in EnumerateSkills(skills))
            {
                var name  = kv.Key;
                var level = kv.Value;

                // Defensive: skip nonsense / mod values with weird characters
                if (string.IsNullOrEmpty(name)) continue;
                if (name.IndexOf(':') >= 0 || name.IndexOf('|') >= 0) continue;
                if (name.Length > 32) continue;

                if (!first) sb.Append('|');
                sb.Append(name);
                sb.Append(':');
                sb.Append(level.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                first = false;
            }

            if (sb.Length == 0) return;

            try
            {
                _serverRpc.Invoke("ServerGuard_SkillReport", sb.ToString());
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Skill report send failed: {ex.Message}");
            }
        }

        // Patches ZNet.OnNewConnection so we can register our request-handler on the
        // peer-specific ZRpc as soon as we connect to a server.
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        public static class Patch_RegisterClientHandler
        {
            public static void Postfix(ZNetPeer peer)
            {
                try
                {
                    if (peer == null || peer.m_rpc == null) return;

                    // Only run on the client side. ZNet.IsServer() returns true on the
                    // dedicated server / host; we never need to register the request
                    // handler there because servers don't send manifests to themselves.
                    if (ZNet.instance != null && ZNet.instance.IsServer()) return;

                    peer.m_rpc.Register<string>("ServerGuard_RequestManifest", (rpc, challenge) =>
                    {
                        try
                        {
                            var json = ClientPlugin.Instance.BuildManifestJson(challenge);
                            rpc.Invoke("ServerGuard_Manifest", json);
                            ClientPlugin.LogS.LogInfo($"[ServerGuard.Client] Sent manifest ({json.Length} bytes, {ClientPlugin.Instance._cachedManifest?.Count ?? 0} mods).");
                        }
                        catch (Exception ex)
                        {
                            ClientPlugin.LogS.LogError($"[ServerGuard.Client] Manifest send failed: {ex.Message}");
                        }
                    });

                    // Stash the server peer's RPC so the devcommands gate can report back to it.
                    if (ClientPlugin.Instance != null) ClientPlugin.Instance._serverRpc = peer.m_rpc;

                    // Register reply handler for admin chat commands (#16). Server sends
                    // \n-separated lines back; we display each in the local chat window.
                    peer.m_rpc.Register<string>("ServerGuard_AdminCommandReply", (rpc, text) =>
                    {
                        try { ClientPlugin.Instance?.DisplayAdminReply(text); }
                        catch (Exception ex) { ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Admin reply display failed: {ex.Message}"); }
                    });

                    // Cheat-item removal: the server sends a comma-separated prefab-name
                    // list on login; we strip those items from the local inventory.
                    peer.m_rpc.Register<string>("ServerGuard_RemoveItems", (rpc, itemList) =>
                    {
                        try { ClientPlugin.Instance?.OnRemoveItemsReceived(itemList); }
                        catch (Exception ex) { ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] RemoveItems handler error: {ex.Message}"); }
                    });

                    // Cheat taint (2.0): strip every item the game itself marked as
                    // cheat-made, except the prefab names in the payload.
                    peer.m_rpc.Register<string>("ServerGuard_StripCheated", (rpc, ignoredCsv) =>
                    {
                        try { ClientPlugin.Instance?.OnStripCheatedReceived(ignoredCsv); }
                        catch (Exception ex) { ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] StripCheated handler error: {ex.Message}"); }
                    });

                    // Arrival-shout policy: "1" = shout normally, "0" = swallow the
                    // vanilla first-spawn shout. Sent on connect and on every
                    // server-side settings.yaml reload.
                    peer.m_rpc.Register<string>("ServerGuard_ArrivalShout", (rpc, allowed) =>
                    {
                        try { ClientPlugin.OnArrivalShoutPolicyReceived(allowed); }
                        catch (Exception ex) { ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] ArrivalShout handler error: {ex.Message}"); }
                    });

                    // Console guard policy: how the F5 console and key binds are allowed
                    // to behave on this server. Sent on connect and on every server-side
                    // settings.yaml / admins.yaml reload.
                    peer.m_rpc.Register<string>("ServerGuard_ConsolePolicy", (rpc, payload) =>
                    {
                        try { ClientPlugin.OnConsolePolicyReceived(payload); }
                        catch (Exception ex) { ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] ConsolePolicy handler error: {ex.Message}"); }
                    });

                    // Customs: a server running it asks for inventory declarations once
                    // this client has attested. A fresh connection starts from nothing.
                    ClientPlugin.Instance?.CustomsReset();
                    peer.m_rpc.Register<string>(CustomsWire.RequestRpc, (rpc, payload) =>
                    {
                        try { ClientPlugin.Instance?.OnCustomsRequest(payload); }
                        catch (Exception ex) { ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Customs request handler error: {ex.Message}"); }
                    });

                    ClientPlugin.LogS.LogInfo("[ServerGuard.Client] Registered manifest request handler on server peer.");
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogError($"[ServerGuard.Client] Register handler failed: {ex.Message}");
                }
            }
        }

        // -------------- Devcommands gate (#5) --------------
        //
        // On multiplayer clients we want to make cheats unusable. We do it at the
        // text-dispatch layer (Terminal.TryRunCommand) so we don't depend on any
        // particular signature of Console.IsCheatsEnabled - that method's name and
        // shape has churned across Valheim builds, and patching a missing method
        // takes down all our Harmony patches with it.
        //
        // Strategy:
        //   1. Hardcoded blocklist - covers `devcommands` (the master enable command,
        //      which is NOT cheat-flagged) plus a handful of common abuse vectors.
        //   2. Reflection lookup into Terminal.commands - dynamically blocks any
        //      command registered with IsCheat=true, no hardcoding needed. Works
        //      across Valheim versions as long as the Terminal command registry
        //      exists in some form.
        //
        // Since `devcommands` itself is in the blocklist, players can never flip
        // Terminal.cheat to true. Combined with the cheat-flagged dynamic check,
        // this covers both "the enable command" and "anything the game considers
        // a cheat."
        //
        // The patch is a no-op on the host side (IsServer() == true), so single-
        // player / host-and-play sessions keep their cheats. Only true multiplayer
        // clients are gated.

        private static bool IsActiveMultiplayerClient()
        {
            try
            {
                if (ZNet.instance == null) return false;
                if (ZNet.instance.IsServer()) return false; // host - allow normal usage
                return true;
            }
            catch { return false; }
        }

        // Called by the gate patch when it blocks a command. Fires off the report RPC
        // if we have a live server connection; always logs locally.
        //
        // `category` is one of "cheat", "risky", "bind", "notallowed" and travels to the
        // server as "<command>|<category>" so it can decide how loudly to react. The
        // pipe-separated form is new in 1.7 - servers on 1.6.x parse the whole string as
        // the command name, which degrades to a slightly noisier log line, not a break.
        internal void ReportDevcommand(string command, string category = "cheat")
        {
            try
            {
                LogS.LogWarning($"[ServerGuard.Client] Blocked console command `{command}` ({category}) — server policy: {_consoleMode}");
                if (_serverRpc != null)
                {
                    try { _serverRpc.Invoke("ServerGuard_DevcommandAttempt", $"{command ?? ""}|{category}"); }
                    catch (Exception ex) { LogS.LogWarning($"[ServerGuard.Client] Could not report console attempt to server: {ex.Message}"); }
                }
            }
            catch { /* never let the gate throw into Valheim */ }
        }

        // Prints a short refusal into the local console so a blocked player understands
        // what happened instead of seeing the command silently do nothing.
        private static void NotifyBlocked(string cmd, string category)
        {
            try
            {
                var why = category == "bind"
                    ? "key binds are disabled on this server"
                    : category == "notallowed"
                        ? "this server only permits a whitelist of console commands"
                        : category == "moderator"
                            ? "this dev command is not in the server's moderator list"
                            : "this command is blocked by the server's security policy";
                ClientPlugin.Instance?.DisplayAdminReply($"[ServerGuard] `{cmd}` refused — {why}.");
            }
            catch { }
        }

        // Called by the animation-cancel patches when they swallow a cancel input that
        // arrived mid-attack. `source` is a short tag (e.g. "emote") so the server log
        // + Discord can show what vector was used.
        internal void ReportAnimationCancel(string source)
        {
            try
            {
                LogS.LogInfo($"[ServerGuard.Client] Blocked animation cancel via {source} (mid-attack).");
                if (_serverRpc != null)
                {
                    try { _serverRpc.Invoke("ServerGuard_AnimationCancelAttempt", source ?? ""); }
                    catch (Exception ex) { LogS.LogWarning($"[ServerGuard.Client] Could not report animation-cancel: {ex.Message}"); }
                }
            }
            catch { /* never let the gate throw into Valheim */ }
        }

        // Resolves Terminal's command registry (Dictionary<string, Terminal.ConsoleCommand>)
        // once and caches it. Naming has varied across builds (`commands`, `m_commands`,
        // `s_commands`), so we search by SHAPE rather than by name: a static dictionary
        // whose key type is string and whose value type is named ConsoleCommand.
        //
        // Matching on shape matters here. Terminal also holds `m_testList`
        // (Dictionary<string,string>) and `m_binds` (Dictionary<KeyCode, List<string>>)
        // as static fields, and m_testList is declared FIRST - a "take the first
        // IDictionary" search silently binds to the wrong dictionary and every
        // cheat-flag lookup comes back false.
        private static System.Collections.IDictionary _terminalCommands;
        private static bool _terminalCommandsResolved;

        private static System.Collections.IDictionary ResolveTerminalCommands()
        {
            if (_terminalCommandsResolved) return _terminalCommands;
            _terminalCommandsResolved = true;

            try
            {
                foreach (var f in typeof(Terminal).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!typeof(System.Collections.IDictionary).IsAssignableFrom(f.FieldType)) continue;
                    if (!f.FieldType.IsGenericType) continue;

                    var argsT = f.FieldType.GetGenericArguments();
                    if (argsT.Length != 2) continue;
                    if (argsT[0] != typeof(string)) continue;
                    if (argsT[1].Name.IndexOf("ConsoleCommand", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    _terminalCommands = f.GetValue(null) as System.Collections.IDictionary;
                    if (_terminalCommands != null)
                    {
                        LogS?.LogInfo($"[ServerGuard.Client] Terminal command registry bound: {f.Name} ({_terminalCommands.Count} commands).");
                        return _terminalCommands;
                    }
                }
                LogS?.LogWarning("[ServerGuard.Client] Could not locate Terminal's command registry - falling back to the static command lists only.");
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Terminal command registry lookup failed: {ex.Message}");
            }
            return _terminalCommands;
        }

        private static object LookupCommandObject(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            var commands = ResolveTerminalCommands();
            if (commands == null) return null;
            try
            {
                // Valheim lowercases the token before its own lookup, so match that.
                var key = cmd.ToLowerInvariant();
                if (commands.Contains(key)) return commands[key];

                foreach (System.Collections.DictionaryEntry entry in commands)
                {
                    if (entry.Key is string k && string.Equals(k, cmd, StringComparison.OrdinalIgnoreCase))
                        return entry.Value;
                }
            }
            catch { }
            return null;
        }

        // True when `cmd` is a command Valheim itself considers a cheat. Dynamic, so it
        // also covers cheat commands added by other mods.
        private static bool IsRegisteredCheatCommand(string cmd)
        {
            var cmdObj = LookupCommandObject(cmd);
            if (cmdObj == null) return false;
            return ReadBoolMember(cmdObj, new[] { "IsCheat", "isCheat", "m_isCheat", "Cheat", "cheat" });
        }

        // True when `cmd` is registered at all (vanilla or from another mod). Used by
        // whitelist mode: an unregistered token isn't a command, so there is nothing to
        // block - Valheim will just print "not a recognized command".
        private static bool IsRegisteredCommand(string cmd) => LookupCommandObject(cmd) != null;

        // True when `cmd` is something vanilla only runs on the server / for admins
        // (ConsoleCommand.OnlyServer, which every onlyAdmin: true command also sets).
        // Together with IsCheat this is the set a moderator's list has to be checked
        // against: everything else was already runnable by any player.
        private static bool IsRegisteredServerOnlyCommand(string cmd)
        {
            var cmdObj = LookupCommandObject(cmd);
            if (cmdObj == null) return false;
            // Two separate flags: the ConsoleEvent ctor sets OnlyServer from onlyServer
            // alone and keeps onlyAdmin in its own field, so both have to be read.
            // ReadBoolMember returns at the first member it FINDS, hence two calls.
            return ReadBoolMember(cmdObj, new[] { "OnlyServer", "onlyServer" })
                || ReadBoolMember(cmdObj, new[] { "OnlyAdmin", "onlyAdmin" });
        }

        private static bool IsDevOnlyCommand(string cmd) =>
            CheatCommands.Contains(cmd) || IsRegisteredCheatCommand(cmd) || IsRegisteredServerOnlyCommand(cmd);

        private static bool ReadBoolMember(object target, string[] names)
        {
            var t = target.GetType();
            foreach (var name in names)
            {
                var fi = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fi != null && fi.FieldType == typeof(bool))
                    return fi.GetValue(target) is bool fb && fb;

                var pi = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi != null && pi.PropertyType == typeof(bool))
                    return pi.GetValue(target) is bool pb && pb;
            }
            return false;
        }

        // ====================== Console guard decision ======================
        //
        // Returns true when `cmd` must not run, and sets `category` for reporting.
        // `cmd` arrives without any leading slash and without arguments.
        internal static bool ShouldBlockConsoleCommand(string cmd, out string category)
        {
            category = null;
            if (string.IsNullOrEmpty(cmd)) return false;

            // Moderator dev-command list. Evaluated before every exemption: the list is
            // the server's answer to "which dev commands may this moderator run", and
            // it applies whatever the console-guard mode is. Only dev-only commands are
            // held against it - `help`, `ping`, emotes etc. were never gated by it.
            //
            // This is load-bearing, not cosmetic: once a grant is active the
            // IsCheatsEnabled patch below reads m_cheat, so a cheat command that is NOT
            // OnlyServer (`find`, `printcreatures`, `nospawn`, ...) would otherwise pass
            // vanilla's own IsValid for a moderator who wasn't given it.
            if (_devMode == "list" && IsDevOnlyCommand(cmd))
            {
                // Granted commands bypass the tiers below too - a moderator who is NOT
                // console-guard exempt would otherwise hit the cheat tier for `fly`.
                if (IsDevCommandPermitted(cmd)) return false;
                category = "moderator";
                return true;
            }

            if (ConsoleGuardExempt) return false;
            if (_consoleMode == "open") return false;

            // Bind policy is evaluated first: under block/purge/wipe the whole bind
            // family is off limits regardless of the mode. `bind` because leaving it
            // usable would let a player re-add what the purge just removed;
            // `printbinds` because it reports exactly which binds survived the guard,
            // which is a map of where the gaps are.
            if (_consoleBindPolicy != "allow" && BindCommands.Contains(cmd))
            {
                category = "bind";
                return true;
            }

            // Operator-supplied additions apply in every non-open mode.
            if (_consoleExtraBlocked.Contains(cmd)) { category = "risky"; return true; }

            if (_consoleMode == "whitelist")
            {
                if (AlwaysAllowedCommands.Contains(cmd)) return false;
                if (_consoleAllowed.Contains(cmd)) return false;
                // Not a real command - nothing to block, vanilla prints "not recognized".
                if (!IsRegisteredCommand(cmd)) return false;
                category = "notallowed";
                return true;
            }

            // mode == "restricted" (and "disabled", which still gates chat-typed
            // commands - the console being unopenable doesn't close the chat line).
            if (CheatCommands.Contains(cmd))        { category = "cheat"; return true; }
            if (IsRegisteredCheatCommand(cmd))      { category = "cheat"; return true; }
            if (RiskyCommands.Contains(cmd))        { category = "risky"; return true; }

            return false;
        }

        // -------------- Animation-cancel gate --------------
        //
        // Classic Valheim attack-spam exploit: trigger an emote mid-attack to cancel the
        // recovery animation. The next attack then fires faster than the weapon's
        // animation should allow. The fix is to refuse that state transition while
        // Player.InAttack() returns true.
        //
        // Only blocks on multiplayer clients - single-player / host keeps full control.
        // Only blocks the LOCAL player's input - we never interfere with how other
        // players' animations sync over the network.
        //
        // Patches:
        //   * Player.StartEmote         - emote cancel (the most common exploit)
        //
        // Sheathing (Humanoid.HideHandItems) is deliberately NOT gated: it is ordinary
        // play - weapon swaps, picking up items, opening chests and building all holster
        // the weapon - so blocking it mid-attack produced constant false positives for
        // honest players.

        private static bool ShouldBlockAnimationCancel(Player p)
        {
            try
            {
                if (!IsActiveMultiplayerClient()) return false;
                // Owners are exempt from every rule, and this one is enforced entirely
                // client-side - the server never gets a chance to wave it through. The
                // role comes from the server's console-policy push, which is the only
                // channel that carries it.
                if (IsOwnerClient) return false;
                if (p == null) return false;
                if (p != Player.m_localPlayer) return false;
                return p.InAttack();
            }
            catch { return false; }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.StartEmote))]
        public static class Patch_Player_StartEmote_BlockDuringAttack
        {
            public static bool Prefix(Player __instance)
            {
                try
                {
                    if (ShouldBlockAnimationCancel(__instance))
                    {
                        ClientPlugin.Instance?.ReportAnimationCancel("emote");
                        return false; // swallow - emote does NOT fire
                    }
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Emote gate error: {ex.Message}");
                }
                return true;
            }
        }

        // ====================== Key-bind control ======================
        //
        // The threat, concretely: a player runs `bind f devcommands` offline. Valheim
        // writes "F devcommands" into the persisted ConsoleBindings pref. Next launch,
        // Chat.Awake reads that pref back and Terminal.updateBinds rebuilds the live
        // bind table. The player then joins a server and presses F.
        //
        // Two details make this worse than a normal console command:
        //
        //   1. Binds are dispatched from Chat.Update, NOT from Console.Update. Blocking
        //      the console (F5) does nothing to them - the console never has to be
        //      opened, or even openable, for a bind to fire.
        //   2. Chat.Update calls TryRunCommand(text, silentFail: true,
        //      skipAllowedCheck: true). That third argument makes ConsoleCommand.IsValid
        //      skip its context check, so a bind runs commands that would be refused as
        //      "not valid in the current context" if typed.
        //
        // Our TryRunCommand prefix still sees bind-dispatched commands (it patches the
        // method, not the caller), so the blocklist already covers their contents. The
        // purge below is the second layer: remove the binds themselves so nothing is
        // sitting on a hotkey waiting for a gap in the blocklist.
        //
        // Terminal.m_binds is only ever populated by Terminal.updateBinds - confirmed by
        // scanning every writer in assembly_valheim - so clearing it there is complete.

        private static System.Reflection.FieldInfo _bindsField;      // Dictionary<KeyCode, List<string>>
        private static System.Reflection.FieldInfo _bindListField;   // List<string>
        private static bool _bindFieldsResolved;

        private static void ResolveBindFields()
        {
            if (_bindFieldsResolved) return;
            _bindFieldsResolved = true;
            try
            {
                foreach (var f in typeof(Terminal).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!f.FieldType.IsGenericType) continue;
                    var argsT = f.FieldType.GetGenericArguments();

                    // Dictionary<KeyCode, List<string>>
                    if (_bindsField == null && argsT.Length == 2 && argsT[0] == typeof(KeyCode)
                        && typeof(System.Collections.IDictionary).IsAssignableFrom(f.FieldType))
                    {
                        _bindsField = f;
                        continue;
                    }

                    // List<string> - the raw "KEY command" lines backing the pref.
                    if (_bindListField == null && argsT.Length == 1 && argsT[0] == typeof(string)
                        && typeof(System.Collections.IList).IsAssignableFrom(f.FieldType))
                    {
                        _bindListField = f;
                    }
                }
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Bind field lookup failed: {ex.Message}");
            }

            if (_bindsField == null)
                LogS?.LogWarning("[ServerGuard.Client] Could not locate Terminal's bind table - bind purge is inactive.");
        }

        // Applies the server's bind policy right now. Safe to call repeatedly.
        internal static void ApplyBindPolicy()
        {
            try
            {
                if (ConsoleGuardExempt) return;
                if (_consoleBindPolicy == "allow" || _consoleBindPolicy == "block") return;
                if (!IsActiveMultiplayerClient()) return;

                ResolveBindFields();

                int cleared = 0;
                var dict = _bindsField?.GetValue(null) as System.Collections.IDictionary;
                if (dict != null && dict.Count > 0)
                {
                    cleared = dict.Count;
                    dict.Clear();
                }

                if (_consoleBindPolicy == "wipe")
                {
                    // Also drop the backing list and rewrite the persisted pref, so the
                    // binds are gone permanently rather than only for this session.
                    var list = _bindListField?.GetValue(null) as System.Collections.IList;
                    if (list != null && list.Count > 0)
                    {
                        list.Clear();
                        try
                        {
                            var update = typeof(Terminal).GetMethod("updateBinds",
                                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            update?.Invoke(null, null);
                        }
                        catch (Exception ex)
                        {
                            LogS?.LogWarning($"[ServerGuard.Client] Could not persist bind wipe: {ex.Message}");
                        }
                    }
                }

                if (cleared > 0)
                {
                    LogS?.LogWarning($"[ServerGuard.Client] Cleared {cleared} key bind(s) — server bind policy is '{_consoleBindPolicy}'.");
                    ClientPlugin.Instance?.DisplayAdminReply(
                        $"[ServerGuard] {cleared} custom key bind(s) removed — this server does not permit console key binds.");
                }
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] ApplyBindPolicy error: {ex.Message}");
            }
        }

        // Terminal.updateBinds is the only writer of the live bind table. Re-clearing
        // here catches every path that could repopulate it: Chat.Awake reloading the
        // persisted list on world load, and any successful bind/unbind command.
        [HarmonyPatch(typeof(Terminal), "updateBinds")]
        public static class Patch_Terminal_UpdateBinds
        {
            public static void Postfix()
            {
                try { ApplyBindPolicy(); }
                catch (Exception ex) { ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] updateBinds postfix error: {ex.Message}"); }
            }
        }

        // Leaving the server has to restore local control. Without this, disconnecting
        // from a server that runs consoleGuardMode: disabled would leave the player's
        // console dead in single-player until they restarted the game.
        //
        // The player's saved binds are untouched by "purge" (only the live table is
        // cleared), and Chat.Awake reloads them from the pref on the next world load,
        // so single-player binds come back on their own. "wipe" is permanent by design.
        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        public static class Patch_ZNet_Shutdown_ResetPolicy
        {
            // Customs departure report: it has to leave before the connection closes.
            public static void Prefix()
            {
                ClientPlugin.Instance?.CustomsSendLogout();
            }

            public static void Postfix()
            {
                try
                {
                    if (ClientPlugin.Instance != null) ClientPlugin.Instance._serverRpc = null;
                    ClientPlugin.Instance?.CustomsReset();
                    ResetConsolePolicy();
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Policy reset on shutdown failed: {ex.Message}");
                }
            }
        }

        // ====================== Console lockout ======================
        //
        // Console.Update early-returns when IsConsoleEnabled() is false, and that single
        // check gates both the F5 key and the gamepad chord. Forcing it false is
        // therefore a complete lockout of the console window with no input-handling
        // patch of our own.
        //
        // It does NOT stop chat, and it does NOT stop key binds (those live in
        // Chat.Update) - the bind policy above covers that half.
        //
        // Admins are exempt by default: `sg` commands are typed into this console, so
        // locking an admin out of it removes their moderation interface.
        // `global::` is required: `using System;` is in scope, so a bare `Console` binds
        // to System.Console. Valheim's Console type sits in the global namespace.
        [HarmonyPatch(typeof(global::Console), "IsConsoleEnabled")]
        public static class Patch_Console_IsConsoleEnabled
        {
            public static void Postfix(ref bool __result)
            {
                try
                {
                    if (!__result) return;                        // already off
                    if (_consoleMode != "disabled") return;
                    if (ConsoleGuardExempt) return;
                    if (!IsActiveMultiplayerClient()) return;      // single-player keeps its console
                    __result = false;
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Console lockout error: {ex.Message}");
                }
            }
        }

        // ====================== Staff dev commands (2.0) ======================
        //
        // Why cheats don't work on a dedicated-server client, and what we lift:
        //
        //   Terminal.IsCheatsEnabled()  = m_cheat && ZNet.instance && ZNet.IsServer()
        //   ConsoleCommand.IsValid()    = (!IsCheat || IsCheatsEnabled())
        //                              && (isAllowedCommand || skipAllowedCheck)   <- stub, always true
        //                              && (!IsNetwork || ZNet.instance)
        //                              && (!OnlyServer || ZNet.IsServer())
        //
        // On a client IsServer() is false, so every IsCheat command AND every
        // OnlyServer command (all the onlyAdmin: true ones - fly, god, spawn, goto...)
        // fails IsValid. TryRunCommand then forwards the command to the server if it
        // has RemoteCommand = true (skiptime, sleep, setworldmodifier, ...) and
        // otherwise prints "not valid in the current context".
        //
        // With a grant from the server:
        //   * IsCheatsEnabled() returns m_cheat - the local `devcommands` toggle - as it
        //     does in single-player. This also lights up the debugmode hotkeys, which
        //     read IsCheatsEnabled directly rather than going through a command.
        //   * IsValid() is re-evaluated for a PERMITTED, non-RemoteCommand command with
        //     the IsCheat / OnlyServer terms dropped, so it runs locally exactly as it
        //     would for a listen-server host. RemoteCommand commands are left invalid
        //     on purpose: vanilla then forwards them to the server, where the server
        //     half authorises staff (ServerPlugin, Patch_ZNet_RPC_RemoteCommand).
        //
        // The moderator list is enforced in ShouldBlockConsoleCommand, which runs
        // before dispatch, and nothing here widens IsValid for a non-permitted command.
        // Both patches are inert without a grant, and the grant dies with the
        // connection (ResetConsolePolicy), so single-player is untouched.

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.IsCheatsEnabled))]
        public static class Patch_Terminal_IsCheatsEnabled
        {
            public static bool Prefix(ref bool __result)
            {
                try
                {
                    if (!DevAccessGranted) return true;
                    if (!IsActiveMultiplayerClient()) return true;
                    __result = Terminal.m_cheat;
                    return false;
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] IsCheatsEnabled patch error: {ex.Message}");
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(Terminal.ConsoleCommand), nameof(Terminal.ConsoleCommand.IsValid))]
        public static class Patch_ConsoleCommand_IsValid
        {
            public static void Postfix(Terminal.ConsoleCommand __instance, ref bool __result)
            {
                try
                {
                    if (__result) return;
                    if (__instance == null || !DevAccessGranted) return;
                    if (!IsActiveMultiplayerClient()) return;
                    if (__instance.RemoteCommand) return;             // let vanilla forward it
                    if (!IsDevCommandPermitted(__instance.Command)) return;

                    // Vanilla's own terms minus IsCheat/OnlyServer. m_cheat must still be
                    // on for cheat commands - that is the player's `devcommands` toggle.
                    if (__instance.IsCheat && !Terminal.m_cheat) return;
                    if (__instance.IsNetwork && ZNet.instance == null) return;
                    __result = true;
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] ConsoleCommand.IsValid patch error: {ex.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.TryRunCommand))]
        public static class Patch_TryRunCommand
        {
            // Block the specific text-level commands and tell the server. Returning false
            // skips the original method body, so the command is never dispatched.
            //
            // NOTE: Terminal.TryRunCommand is VOID in current Valheim builds. We must not
            // declare a `ref bool __result` parameter - HarmonyX refuses to bind a Prefix
            // with __result to a void method and aborts patching ("Cannot get result from
            // void method"). The signature below is intentionally __result-free.
            public static bool Prefix(string text)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(text)) return true;

                    // Pull the first whitespace-separated token as the command name.
                    var trimmed = text.TrimStart();
                    int sp = trimmed.IndexOf(' ');
                    var cmd = sp >= 0 ? trimmed.Substring(0, sp) : trimmed;
                    var cmdNoSlash = cmd.StartsWith("/", StringComparison.Ordinal) ? cmd.Substring(1) : cmd;

                    // ServerGuard admin command - take precedence over devcommand gate.
                    // Accepts both `sg ...` and `/sg ...`. Always swallowed (never dispatched
                    // to Valheim's own command system) so we don't print "unknown command".
                    if (string.Equals(cmdNoSlash, "sg", StringComparison.OrdinalIgnoreCase))
                    {
                        var rest = sp >= 0 ? trimmed.Substring(sp + 1).TrimStart() : "";
                        ClientPlugin.Instance?.SendAdminCommand(rest);
                        return false;
                    }

                    // Below this line: the console guard. Only active on multiplayer
                    // clients - single-player and host-and-play keep full console access.
                    if (!IsActiveMultiplayerClient()) return true;

                    if (ShouldBlockConsoleCommand(cmdNoSlash, out var category))
                    {
                        ClientPlugin.Instance?.ReportDevcommand(cmdNoSlash, category);
                        NotifyBlocked(cmdNoSlash, category);
                        return false; // skip original - command is swallowed
                    }
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Console gate error: {ex.Message}");
                }
                return true; // let Valheim handle non-blocked commands
            }
        }

        // ====================== Chat reporting ======================
        //
        // Current Valheim builds send chat once PER RECIPIENT (per-user text
        // permission checks), so a dedicated server only routes — never handles —
        // chat packets, and a self-send (only player online) never reaches the
        // server at all. The only reliable interception point is the sending
        // client: Chat.SendText is the single entry point for /s, /w and normal
        // chat (whispers go through Talker.Say internally, but always via here).

        // Bind parameters by index (__0/__1) so the patch attaches regardless of
        // parameter names in the running Valheim build.
        //
        // Returns false to swallow the vanilla first-spawn shout when the server has
        // turned it off. Both jobs live in one prefix on purpose: Harmony skips the
        // remaining prefixes once any of them returns false, so a separate blocker
        // patch could suppress the reporting patch (or not) depending on patch order.
        [HarmonyPatch(typeof(Chat), "SendText")]
        public static class Patch_Chat_SendText_Report
        {
            public static bool Prefix(Talker.Type __0, string __1)
            {
                try
                {
                    if (ZNet.instance == null || ZNet.instance.IsServer()) return true;
                    if (__0 != Talker.Type.Shout) return true;

                    // Game.UpdateRespawn is the only caller that shouts on its own, so a
                    // Shout raised while it is on the stack is the arrival message - no
                    // text matching needed, and it works in every language. Anything the
                    // player types reaches SendText from the chat input instead, well
                    // outside this bracket.
                    if (_inRespawnUpdate && !_arrivalShoutAllowed && !_arrivalShoutConsumed)
                    {
                        _arrivalShoutConsumed = true;
                        LogS?.LogInfo("[ServerGuard.Client] Arrival shout suppressed (server policy).");
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(__1)) return true;

                    ClientPlugin.Instance?.SendChatReport((int)__0, __1);
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Chat hook error: {ex.Message}");
                }
                return true;
            }
        }

        // ====================== Arrival shout gate ======================
        //
        // Vanilla shouts the localised "I have arrived!" line from Game.UpdateRespawn
        // the first time the local player spawns into a session. Servers that already
        // announce logins can turn it off via enableArrivalShout in settings.yaml.
        //
        // Rather than string-matching the shout (which breaks in other languages and
        // would also eat a manual "I have arrived!"), we flag the window while
        // UpdateRespawn is on the stack and let the Chat.SendText prefix drop the
        // shout raised inside it.

        // Default true: a server that never sends the policy (older build, or the
        // setting left at its default) keeps vanilla behaviour.
        private static bool _arrivalShoutAllowed = true;

        // True only while UpdateRespawn is actually on the stack. It must be a
        // prefix/postfix bracket around the call and NOT a frame stamp: Game calls
        // UpdateRespawn every frame, so "UpdateRespawn ran this frame" is true on
        // every frame and swallowed every shout the player made.
        private static bool _inRespawnUpdate;

        // The arrival shout happens once per session, so suppress at most once. This
        // is the safety net for the one case a bracket can't cover: a postfix does
        // NOT run if the original method throws, which would otherwise latch the flag
        // on and mute the player for the rest of the session.
        private static bool _arrivalShoutConsumed;

        internal static void OnArrivalShoutPolicyReceived(string payload)
        {
            _arrivalShoutAllowed = !string.Equals((payload ?? "").Trim(), "0", StringComparison.Ordinal);
            // Fires on connect, so this also re-arms the one-shot for the new session.
            _arrivalShoutConsumed = false;
            _inRespawnUpdate = false;
            LogS?.LogInfo($"[ServerGuard.Client] Arrival shout {(_arrivalShoutAllowed ? "allowed" : "suppressed")} by server policy.");
        }

        [HarmonyPatch(typeof(Game), "UpdateRespawn")]
        public static class Patch_Game_UpdateRespawn_ArrivalShout
        {
            public static void Prefix() { _inRespawnUpdate = true; }
            public static void Postfix() { _inRespawnUpdate = false; }
        }

        // Payload: "<type>|<text>". Server resolves name/SteamID from the peer.
        internal void SendChatReport(int type, string text)
        {
            try
            {
                var serverRpc = ZNet.instance?.GetServerRPC();
                if (serverRpc == null) return;

                text = text.Replace('\n', ' ').Trim();
                if (text.Length > 256) text = text.Substring(0, 256);
                if (text.Length == 0) return;

                serverRpc.Invoke("ServerGuard_Chat", $"{type}|{text}");
                LogS?.LogInfo($"[ServerGuard.Client] Shout report sent.");
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Chat report failed: {ex.Message}");
            }
        }

        // ====================== Cheat taint report (2.0) ======================
        //
        // Valheim 1.0 keeps ItemData.m_cheated on every item that came out of a cheat
        // (spawned, crafted from spawned materials, dropped by a cheat-spawned creature,
        // ...) and persists it in the character file, so it survives a trip through
        // single-player. The game uses it to withhold achievements; the server uses it
        // as an anti-cheat signal. Same trust model as the skill report: the server
        // hash-pins this DLL, so a client that could lie here has already been kicked.
        //
        // Payload: "usedCheats|bypass|count|prefab:stack,prefab:stack,..."
        //   usedCheats - PlayerProfile.m_usedCheats: the character has run a cheat command
        //   bypass     - PlayerProfile.s_bypassCheatChecks: the `bypasscheatchecks` key is
        //                set, meaning the game has stopped marking anything for this character
        //
        // Sent every CheatStateHeartbeatSeconds, and within CheatStatePollSeconds of the
        // flagged set changing (pick up / drop / strip), so the server sees a change
        // promptly without a per-frame patch.

        private const float CheatStatePollSeconds      = 10f;
        private const float CheatStateHeartbeatSeconds = 60f;
        private string _lastCheatSignature = null;
        private float  _lastCheatSentAt    = -1000f;

        private IEnumerator CheatStateLoop()
        {
            yield return new WaitForSeconds(15f);
            while (true)
            {
                yield return new WaitForSeconds(CheatStatePollSeconds);
                try { SendCheatStateIfDue(false); }
                catch (Exception ex) { LogS?.LogWarning($"[ServerGuard.Client] Cheat state tick error: {ex.Message}"); }
            }
        }

        private void SendCheatStateIfDue(bool force)
        {
            if (_serverRpc == null) return;
            if (!IsActiveMultiplayerClient()) return;
            var player = Player.m_localPlayer;
            var inv = player?.GetInventory();
            if (inv == null) return;

            bool usedCheats = false;
            try { usedCheats = Game.instance != null && Game.instance.GetPlayerProfile().m_usedCheats; } catch { }
            bool bypass = false;
            try { bypass = PlayerProfile.s_bypassCheatChecks; } catch { }

            var flagged = new List<KeyValuePair<string, int>>();
            foreach (var item in inv.GetAllItems())
            {
                if (item == null || !item.m_cheated) continue;
                var name = item.m_dropPrefab != null ? item.m_dropPrefab.name : (item.m_shared?.m_name ?? "unknown");
                name = SanitiseShort(name, 48).Replace(':', '_').Replace(',', '_').Replace('|', '_');
                flagged.Add(new KeyValuePair<string, int>(name, Math.Max(1, item.m_stack)));
            }
            flagged.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            var sb = new StringBuilder();
            sb.Append(usedCheats ? '1' : '0').Append('|');
            sb.Append(bypass ? '1' : '0').Append('|');
            sb.Append(flagged.Count).Append('|');
            for (int i = 0; i < flagged.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(flagged[i].Key).Append(':').Append(flagged[i].Value);
            }
            var payload = sb.ToString();

            var now = Time.realtimeSinceStartup;
            var changed = !string.Equals(payload, _lastCheatSignature, StringComparison.Ordinal);
            if (!force && !changed && now - _lastCheatSentAt < CheatStateHeartbeatSeconds) return;

            try
            {
                _serverRpc.Invoke("ServerGuard_CheatState", payload);
                _lastCheatSignature = payload;
                _lastCheatSentAt    = now;
                if (changed && flagged.Count > 0)
                    LogS?.LogInfo($"[ServerGuard.Client] Reported {flagged.Count} cheat-flagged inventory item(s) to the server.");
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Cheat state send failed: {ex.Message}");
            }
        }

        // Server policy "strip" / "violation": remove every m_cheated item now, except
        // the prefab names the operator chose to ignore. Runs immediately - the player
        // is already spawned (the report that triggered this came from their inventory).
        internal void OnStripCheatedReceived(string ignoredCsv)
        {
            try
            {
                var inv = Player.m_localPlayer?.GetInventory();
                if (inv == null) return;

                var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in (ignoredCsv ?? "").Split(','))
                {
                    var v = s.Trim();
                    if (v.Length > 0) ignored.Add(v);
                }

                var toRemove = new List<ItemDrop.ItemData>();
                foreach (var item in inv.GetAllItems())
                {
                    if (item == null || !item.m_cheated) continue;
                    var name = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
                    if (name.Length > 0 && ignored.Contains(name)) continue;
                    toRemove.Add(item);
                }
                if (toRemove.Count == 0) return;

                foreach (var item in toRemove)
                    inv.RemoveItem(item);

                var total = toRemove.Sum(i => Math.Max(1, i.m_stack));
                LogS?.LogWarning($"[ServerGuard.Client] Removed {total} cheat-flagged item(s): "
                    + string.Join(", ", toRemove.Select(i => i.m_dropPrefab != null ? i.m_dropPrefab.name : "?")));
                try
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                        $"[ServerGuard] {total} cheat-spawned item(s) removed by server policy");
                }
                catch { }

                // Tell the server right away so its dedup state reflects the strip.
                SendCheatStateIfDue(true);
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] OnStripCheatedReceived error: {ex.Message}");
            }
        }

        // ====================== Customs: inventory declarations ======================
        //
        // A dedicated server cannot read this player's inventory, so a server running
        // Customs asks this client to declare it (ServerGuard_CustomsRequest) and to keep
        // the declaration current. The server judges the first declaration against the
        // character's last trusted baseline; later reports keep that baseline current
        // while the player is online. Every report is a full snapshot, never a diff, so a
        // lost report costs nothing but a moment of staleness.
        //
        //   declare     answers a request. On arrival it is what the character brought
        //               in, merged from two captures around the first Player.OnSpawned:
        //                 - BEFORE it: the inventory as loaded from the character file.
        //                   OnSpawned applies the character's inventory-row count and
        //                   drops anything outside the grid into the world, where it
        //                   could be picked up again later as an in-session "gain";
        //                 - AFTER it (last postfix): whatever other mods moved into the
        //                   inventory from their own OnSpawned patches. Work a mod
        //                   defers to a later frame is not part of the arrival.
        //   change      the inventory has differed from the last report for a while
        //   checkpoint  every checkpoint interval regardless, so a crash loses little
        //   logout      best effort, from the ZNet.Shutdown prefix, flushed at once
        //
        // What is read: Player.GetInventory().GetAllItems() - the whole grid, including
        // equipped items and any extra rows. A mod that adds rows or slots to that same
        // Inventory - the approach AzuExtendedPlayerInventory takes - is covered with no
        // mod-specific code; that has not been verified against a live AzuEPI install.
        // Not covered: items a mod keeps in a container of its own (such as the retired
        // EquipmentAndQuickSlots mod installed by itself). A container ITEM (a backpack)
        // is seen through its custom-data hash if the mod keeps the contents there.

        private const float CustomsPollSeconds = 2f;

        private string _customsNonce = "";          // "" = the server has not asked, or said stop
        private long   _customsSequence;            // per connection; never reset while connected
        private int    _customsCheckpointSeconds = 120;
        private int    _customsDebounceSeconds   = 5;
        private int    _customsMaxRecords        = CustomsLimits.DefaultRecords;
        private bool   _customsAnswered;            // the current request has had its declaration
        private bool   _customsArrivalSent;         // the spawn captures have been declared
        private bool   _customsSpawnSeen;           // first spawn of this connection captured
        private List<CustomsItem> _customsLoaded;   // before Player.OnSpawned
        private List<CustomsItem> _customsSpawned;  // after it
        private string _customsLastSignature = "";
        private float  _customsLastSentAt;
        private float  _customsPendingSince = -1f;
        private bool   _customsSizeWarned;
        private Coroutine _customsLoop;

        // New connection, or the old one is gone: nothing carries over between servers.
        internal void CustomsReset()
        {
            _customsNonce         = "";
            _customsSequence      = 0;
            _customsAnswered      = false;
            _customsArrivalSent   = false;
            _customsSpawnSeen     = false;
            _customsLoaded        = null;
            _customsSpawned       = null;
            _customsLastSignature = "";
            _customsLastSentAt    = 0f;
            _customsPendingSince  = -1f;
            _customsSizeWarned    = false;
        }

        internal void OnCustomsRequest(string payload)
        {
            CustomsWire.Request request;
            if (!CustomsWire.TryParseRequest(payload, out request))
            {
                LogS?.LogWarning("[ServerGuard.Client] Customs: could not read the server's request - no declaration sent.");
                return;
            }
            _customsCheckpointSeconds = request.CheckpointSeconds;
            _customsDebounceSeconds   = request.DebounceSeconds;
            _customsMaxRecords        = request.MaxRecords;

            if (request.Stop)
            {
                if (_customsNonce.Length > 0) LogS?.LogInfo("[ServerGuard.Client] Customs: the server stopped asking for inventory declarations.");
                _customsNonce = "";
                return;
            }
            if (request.Nonce == _customsNonce) return;   // same request, new timing only

            _customsNonce    = request.Nonce;
            _customsAnswered = false;
            LogS?.LogInfo($"[ServerGuard.Client] Customs: the server asked for inventory declarations "
                + $"(checkpoint {_customsCheckpointSeconds}s, changes after {_customsDebounceSeconds}s).");
            CustomsTryDeclare();
            if (_customsLoop == null) _customsLoop = StartCoroutine(CustomsLoop());
        }

        private IEnumerator CustomsLoop()
        {
            while (_customsNonce.Length > 0 && IsActiveMultiplayerClient())
            {
                yield return new WaitForSeconds(CustomsPollSeconds);
                try { CustomsTick(); }
                catch (Exception ex) { LogS?.LogWarning($"[ServerGuard.Client] Customs tick error: {ex.Message}"); }
            }
            _customsLoop = null;
        }

        private void CustomsTick()
        {
            if (_customsNonce.Length == 0 || _serverRpc == null) return;
            if (!_customsAnswered) { CustomsTryDeclare(); return; }

            var items = CustomsCollect();
            if (items == null) return;                       // no character right now (dead, loading)
            var now = Time.realtimeSinceStartup;
            if (CustomsItems.Signature(items) != _customsLastSignature)
            {
                // Throttled rather than sent per change: emptying a chest changes the
                // inventory many times a second, and only the settled state matters.
                if (_customsPendingSince < 0f) _customsPendingSince = now;
                if (now - _customsPendingSince >= _customsDebounceSeconds) CustomsSend(CustomsReportKind.Change, items);
                return;
            }
            _customsPendingSince = -1f;
            if (now - _customsLastSentAt >= _customsCheckpointSeconds) CustomsSend(CustomsReportKind.Checkpoint, items);
        }

        // Answers the current request as soon as there is something to declare: the
        // spawn captures if this connection's arrival has not been declared yet,
        // otherwise (Customs switched on mid-session) the inventory as it is now.
        private void CustomsTryDeclare()
        {
            if (_customsNonce.Length == 0 || _customsAnswered || _serverRpc == null) return;
            bool arrival = !_customsArrivalSent && _customsSpawned != null;
            var items = arrival ? CustomsItems.MaxMerge(_customsLoaded, _customsSpawned) : CustomsCollect();
            if (items == null) return;                       // still loading: declared at spawn
            if (!CustomsSend(CustomsReportKind.Declare, items)) return;

            _customsAnswered = true;
            if (arrival)
            {
                _customsArrivalSent = true;
                _customsLoaded = null;
                _customsSpawned = null;
            }
            LogS?.LogInfo($"[ServerGuard.Client] Customs: declared {CustomsItems.Total(items)} item(s) in {items.Count} stack(s).");
        }

        private bool CustomsSend(CustomsReportKind kind, List<CustomsItem> items)
        {
            if (_serverRpc == null || _customsNonce.Length == 0) return false;
            var profile = Game.instance != null ? Game.instance.GetPlayerProfile() : null;
            if (profile == null) return false;

            if (items.Count > _customsMaxRecords && !_customsSizeWarned)
            {
                _customsSizeWarned = true;
                LogS?.LogWarning($"[ServerGuard.Client] Customs: this inventory has {items.Count} distinct stacks but the server "
                    + $"accepts {_customsMaxRecords} - ask the server admin to raise customsMaxItemRecords.");
            }

            var report = new CustomsReport
            {
                Nonce         = _customsNonce,
                Sequence      = ++_customsSequence,
                Kind          = kind,
                CharacterId   = profile.GetPlayerID().ToString(System.Globalization.CultureInfo.InvariantCulture),
                CharacterName = CustomsItems.Clean(profile.GetName(), CustomsLimits.MaxNameLength),
                Items         = items,
            };
            try
            {
                _serverRpc.Invoke(CustomsWire.ReportRpc, CustomsWire.WriteReport(report));
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Customs: report not sent: {ex.Message}");
                return false;
            }
            _customsLastSignature = CustomsItems.Signature(items);
            _customsLastSentAt    = Time.realtimeSinceStartup;
            _customsPendingSince  = -1f;
            return true;
        }

        // The local player's inventory as customs records, cleaned and clamped to what
        // the server accepts (CustomsItems.ForReport), so an honest declaration is
        // always a valid one.
        private static List<CustomsItem> CustomsCollect()
        {
            var inventory = Player.m_localPlayer != null ? Player.m_localPlayer.GetInventory() : null;
            if (inventory == null) return null;

            var items = new List<CustomsItem>();
            foreach (var item in inventory.GetAllItems())
            {
                if (item == null) continue;
                items.Add(new CustomsItem(
                    item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name,
                    item.m_quality, item.m_variant, item.m_worldLevel,
                    item.m_crafterID, item.m_crafterName,
                    CustomsItems.HashCustomData(item.m_customData),
                    item.m_stack));
            }
            return CustomsItems.ForReport(items);
        }

        // From the ZNet.Shutdown prefix, while the connection is still up. Nothing will
        // pump the send queue after this, so the report is flushed by hand. Best effort:
        // a crash or a pulled cable skips it, which is what checkpoints are for.
        internal void CustomsSendLogout()
        {
            try
            {
                if (_customsNonce.Length == 0 || !_customsAnswered || _serverRpc == null) return;
                var items = CustomsCollect();
                if (items == null || !CustomsSend(CustomsReportKind.Logout, items)) return;
                _serverRpc.GetSocket()?.Flush();
                LogS?.LogInfo("[ServerGuard.Client] Customs: departure report sent.");
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Customs: departure report failed: {ex.Message}");
            }
        }

        // The two arrival captures around the first Player.OnSpawned of a connection. The
        // prefix runs first and the postfix last, so the captures bracket every other
        // mod's spawn-time work. Respawns after death are not arrivals and are skipped.
        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        public static class Patch_Player_OnSpawned_Customs
        {
            [HarmonyPriority(Priority.First)]
            public static void Prefix(Player __instance)
            {
                try
                {
                    var self = ClientPlugin.Instance;
                    if (self == null || self._customsSpawnSeen || __instance != Player.m_localPlayer || !IsActiveMultiplayerClient()) return;
                    self._customsLoaded = CustomsCollect();
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Customs spawn capture failed: {ex.Message}");
                }
            }

            [HarmonyPriority(Priority.Last)]
            public static void Postfix(Player __instance)
            {
                try
                {
                    var self = ClientPlugin.Instance;
                    if (self == null || self._customsSpawnSeen || __instance != Player.m_localPlayer || !IsActiveMultiplayerClient()) return;
                    self._customsSpawned   = CustomsCollect();
                    self._customsSpawnSeen = true;
                    self.CustomsTryDeclare();
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Customs spawn capture failed: {ex.Message}");
                }
            }
        }

        // ====================== Staff map coordinates (2.0) ======================
        //
        // On the large map, owners and moderators see the world X/Z of whatever the
        // cursor is over, appended to the biome label at the top of the map (which
        // vanilla already updates from the cursor position every frame in
        // Minimap.UpdateBiome). Handy for `goto`, `sg build at` and grief reports.
        //
        // m_biomeNameLarge is a TMP_Text; the project deliberately does not reference
        // Unity.TextMeshPro, so both the field and the `text` property go through
        // reflection, like the Quick Login panel does.
        private static readonly FieldInfo  MinimapBiomeLargeField =
            typeof(Minimap).GetField("m_biomeNameLarge", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo MinimapScreenToWorld =
            typeof(Minimap).GetMethod("ScreenToWorldPoint", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        [HarmonyPatch(typeof(Minimap), "UpdateBiome")]
        public static class Patch_Minimap_UpdateBiome_StaffCoords
        {
            public static bool Prepare()
            {
                var ok = AccessTools.Method(typeof(Minimap), "UpdateBiome") != null
                      && MinimapBiomeLargeField != null && MinimapScreenToWorld != null;
                if (!ok) ClientPlugin.LogS?.LogWarning("[ServerGuard.Client] Minimap.UpdateBiome / m_biomeNameLarge / ScreenToWorldPoint not found — staff map coordinates disabled.");
                return ok;
            }

            public static void Postfix(Minimap __instance)
            {
                try
                {
                    if (!IsStaffClient) return;
                    if (!IsActiveMultiplayerClient()) return;
                    if (__instance == null || __instance.m_mode != Minimap.MapMode.Large) return;

                    var label = MinimapBiomeLargeField.GetValue(__instance);
                    if (label == null) return;

                    Vector3 screen = ZInput.IsMouseActive() ? (Vector3)ZInput.pointerPosition
                                                            : new Vector3(Screen.width / 2f, Screen.height / 2f);
                    var world = (Vector3)MinimapScreenToWorld.Invoke(__instance, new object[] { screen });

                    var current = GetTmpProperty(label, "text") as string ?? "";
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var coords = string.Format(inv, "<size=70%><color=#a0e0ff>X {0:F0}   Z {1:F0}</color></size>", world.x, world.z);
                    SetTmpProperty(label, "text", current.Length > 0 ? current + "   " + coords : coords);
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Map coordinate overlay error: {ex.Message}");
                }
            }
        }

        // ====================== Cheat item removal ======================
        //
        // The server sends a comma-separated list of prefab names to remove from
        // the player's inventory. Removal is deferred until the player has fully
        // spawned into the world and their inventory is accessible.

        internal void OnRemoveItemsReceived(string itemList)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(itemList)) return;
                var prefabNames = itemList
                    .Split(',')
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
                if (prefabNames.Length == 0) return;
                StartCoroutine(RemoveItemsFromInventory(prefabNames));
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] OnRemoveItemsReceived error: {ex.Message}");
            }
        }

        private IEnumerator RemoveItemsFromInventory(string[] prefabNames)
        {
            // Wait up to 90 s for the player to spawn with a valid inventory.
            float elapsed = 0f;
            while (elapsed < 90f)
            {
                if (Player.m_localPlayer != null && Player.m_localPlayer.GetInventory() != null)
                    break;
                yield return new WaitForSeconds(0.5f);
                elapsed += 0.5f;
            }

            if (Player.m_localPlayer == null) yield break;

            try
            {
                var inventory = Player.m_localPlayer.GetInventory();
                if (inventory == null) yield break;

                var toRemove = new List<ItemDrop.ItemData>();
                foreach (var item in inventory.GetAllItems())
                {
                    if (item?.m_dropPrefab == null) continue;
                    if (prefabNames.Contains(item.m_dropPrefab.name, StringComparer.OrdinalIgnoreCase))
                        toRemove.Add(item);
                }

                foreach (var item in toRemove)
                    inventory.RemoveItem(item);

                if (toRemove.Count > 0)
                    LogS?.LogWarning($"[ServerGuard.Client] Removed {toRemove.Count} cheat item(s) from inventory: {string.Join(", ", toRemove.Select(i => i.m_dropPrefab.name))}");
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] RemoveItemsFromInventory error: {ex.Message}");
            }
        }

        // ====================== Quick Login UI ======================
        //
        // When QuickLoginEnabled is true a panel is injected into the main-menu
        // canvas that shows the server logo, name, description and live player
        // count. Clicking Connect initiates the same join flow as the in-game
        // "Join Game" button, with the configured password pre-filled.

        // ---- FejdStartup.SetupGui patch ----
        // SetupGui is called every time the title screen is shown (including after
        // returning from a session). We use Postfix to ensure the vanilla UI is
        // already built before we add our panel.
        [HarmonyPatch(typeof(FejdStartup), "SetupGui")]
        public static class Patch_FejdStartup_SetupGui
        {
            // If SetupGui is ever renamed/removed in a future Valheim build, skip this
            // patch cleanly instead of throwing — a thrown patch aborts PatchAll for the
            // whole assembly, which would take the critical attestation patches down too.
            public static bool Prepare()
            {
                var exists = AccessTools.Method(typeof(FejdStartup), "SetupGui") != null;
                if (!exists)
                    ClientPlugin.LogS?.LogWarning("[ServerGuard.Client] FejdStartup.SetupGui not found — Quick Login panel disabled for this build.");
                return exists;
            }

            public static void Postfix(FejdStartup __instance)
            {
                try
                {
                    ClientPlugin.Instance?.BuildQuickLoginPanel(__instance);
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] SetupGui patch error: {ex.Message}");
                }
            }
        }

        // ---- Quick-join: force the direct JoinServer() path ----
        // When armed (Connect was clicked), re-assert the queued server right before
        // OnCharacterStart reads GetServerToJoin().IsValid, so it connects directly
        // instead of showing the world/server browser.
        [HarmonyPatch(typeof(FejdStartup), "OnCharacterStart")]
        public static class Patch_FejdStartup_OnCharacterStart
        {
            public static bool Prepare() => AccessTools.Method(typeof(FejdStartup), "OnCharacterStart") != null;

            public static void Prefix(FejdStartup __instance)
            {
                var self = ClientPlugin.Instance;
                if (self == null || !self._quickJoinArmed) return;
                ClientPlugin.LogS?.LogInfo("[ServerGuard.Client] OnCharacterStart: re-asserting quick-join target.");
                self.ReassertServerToJoin(__instance);   // one-shot: clears the arm
            }
        }

        // Leaving character selection disarms the pending quick-join so a later
        // single-player start is never redirected to the server.
        [HarmonyPatch(typeof(FejdStartup), "OnSelelectCharacterBack")]
        public static class Patch_FejdStartup_CharacterBack
        {
            public static bool Prepare() => AccessTools.Method(typeof(FejdStartup), "OnSelelectCharacterBack") != null;

            public static void Postfix() => ClientPlugin.Instance?.DisarmQuickJoin();
        }

        // ---- Panel construction ----
        internal void BuildQuickLoginPanel(FejdStartup menu)
        {
            if (_clientSettings == null || !_clientSettings.QuickLoginEnabled) return;
            if (string.IsNullOrWhiteSpace(_clientSettings.ServerAddress)) return;

            // Destroy any previous instance (e.g. returning from a session).
            if (_quickLoginPanel != null)
            {
                Destroy(_quickLoginPanel);
                _quickLoginPanel = null;
                _playerCountText = null;
            }

            // Parent to a container that stays active across BOTH the main menu and the
            // character-selection screen so the panel persists when the menu hides. The
            // character-select screen's parent is exactly such a persistent GUI root;
            // fall back to the first canvas if it can't be found.
            Transform guiRoot = null;
            var csScreen = GetField(menu, "m_characterSelectScreen") as GameObject;
            if (csScreen != null && csScreen.transform.parent != null)
                guiRoot = csScreen.transform.parent;
            if (guiRoot == null)
            {
                var canvas = menu.GetComponentInChildren<Canvas>(true);
                guiRoot = canvas != null ? canvas.transform : null;
            }
            if (guiRoot == null)
            {
                LogS?.LogWarning("[ServerGuard.Client] BuildQuickLoginPanel: no GUI root found.");
                return;
            }

            // ---- Root panel ----
            _quickLoginPanel = new GameObject("SG_QuickLogin");
            _quickLoginPanel.transform.SetParent(guiRoot, false);

            // Anchor to the top-right corner with a fixed size so the panel occupies
            // only the upper-right region, not the full screen height.
            var rt = _quickLoginPanel.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(320f, 440f);
            rt.anchoredPosition = new Vector2(-30f, -70f);

            var bg = _quickLoginPanel.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.05f, 0.05f, 0.82f);

            // ---- Logo (optional) ----
            float contentTop = -10f;
            if (!string.IsNullOrWhiteSpace(_clientSettings.ServerLogoPath))
            {
                var tex = LoadTexture(Path.Combine(ConfDir, _clientSettings.ServerLogoPath));
                if (tex != null)
                {
                    var logoGo  = CreateChild("SG_Logo", _quickLoginPanel.transform);
                    var logoRt  = logoGo.AddComponent<RectTransform>();
                    logoRt.anchorMin = new Vector2(0.05f, 1f);
                    logoRt.anchorMax = new Vector2(0.95f, 1f);
                    logoRt.pivot     = new Vector2(0.5f, 1f);
                    logoRt.anchoredPosition = new Vector2(0f, contentTop);
                    float aspect = (float)tex.width / tex.height;
                    float logoH  = Mathf.Min(120f, 300f / aspect);
                    logoRt.sizeDelta = new Vector2(0f, logoH);
                    var img = logoGo.AddComponent<Image>();
                    img.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                    img.preserveAspect = true;
                    contentTop -= (logoH + 8f);
                }
            }

            // Use the menu button's TMP label as the font/style template so the panel
            // text matches the Connect button exactly. Fall back to the version label.
            var tmpTemplate = GetMenuButtonLabelTemplate(menu)
                ?? GetField(menu, "m_versionLabel") as Component;

            // ---- Server name ----
            contentTop = AddThemedLabel("SG_Name", _quickLoginPanel.transform, tmpTemplate,
                _clientSettings.ServerName, 24f, false, Color.white, contentTop, 32f);

            // ---- Description ----
            if (!string.IsNullOrWhiteSpace(_clientSettings.ServerDescription))
            {
                contentTop = AddThemedLabel("SG_Desc", _quickLoginPanel.transform, tmpTemplate,
                    _clientSettings.ServerDescription, 16f, false, new Color(0.85f, 0.85f, 0.85f, 1f), contentTop, 64f);
            }

            // ---- Announcements (scrollable) ----
            bool hasAnnouncements = !string.IsNullOrWhiteSpace(_clientSettings.ServerAnnouncements);
            if (hasAnnouncements)
            {
                // Grow the panel so the scroll box gets a usable viewport no matter how
                // tall the logo/name/description above it turned out. Capped so a large
                // logo can't push the panel off the bottom of the screen.
                float needed = -contentTop + AnnHeaderBlock + AnnMinViewport + AnnBottomStack;
                rt.sizeDelta = new Vector2(320f, Mathf.Clamp(needed, 560f, 720f));

                // Header.
                contentTop = AddThemedLabel("SG_AnnHeader", _quickLoginPanel.transform, tmpTemplate,
                    "Announcements", 18f, true, new Color(0.95f, 0.85f, 0.55f, 1f), contentTop, 22f);

                BuildAnnouncementsScrollBox(_quickLoginPanel.transform, tmpTemplate,
                    _clientSettings.ServerAnnouncements, contentTop, AnnBottomStack);
            }

            // ---- Player count ----
            // With announcements the scroll box owns the middle of the panel, so the
            // count is pinned just above the Connect button instead of flowing after
            // the description.
            _playerCountText = hasAnnouncements
                ? CreateThemedLabelComponent("SG_PlayerCount", _quickLoginPanel.transform,
                    tmpTemplate, "Players: querying...", 17f, false, new Color(0.7f, 0.9f, 0.7f, 1f),
                    0f, 24f, true, 72f)
                : CreateThemedLabelComponent("SG_PlayerCount", _quickLoginPanel.transform,
                    tmpTemplate, "Players: querying...", 17f, false, new Color(0.7f, 0.9f, 0.7f, 1f),
                    contentTop - 6f, 26f);

            // ---- Connect button (cloned from a vanilla menu button for theme + font) ----
            AddConnectButton(menu, _quickLoginPanel.transform);

            // Draw above sibling menu/character-select panels.
            _quickLoginPanel.transform.SetAsLastSibling();

            // Kick off a background player-count refresh.
            StartCoroutine(RefreshPlayerCount(
                _clientSettings.ServerAddress,
                _clientSettings.ServerPort));
        }

        // ================== Announcements box (scrollable, clickable links) ==================
        //
        // Vertical budget inside the panel, in px. The panel is grown in
        // BuildQuickLoginPanel so the scroll box never drops below AnnMinViewport.
        private const float AnnHeaderBlock = 26f;   // "Announcements" label + its gap
        private const float AnnMinViewport = 140f;  // smallest scroll box we accept
        private const float AnnBottomStack = 100f;  // player count + Connect button below it
        private const float AnnScrollbarW  = 8f;
        private const float AnnTextPad     = 6f;

        // Standard Unity ScrollRect hierarchy, built by hand because there's no prefab
        // to clone:
        //
        //   SG_AnnScroll        ScrollRect + frame Image
        //   ├─ SG_AnnViewport   RectMask2D + invisible raycast Image
        //   │  └─ SG_AnnContent RectTransform, height driven by the text
        //   │     └─ SG_AnnText the TMP label (+ link click handler)
        //   └─ SG_AnnScrollbar  Scrollbar
        //      └─ SG_AnnScrollArea
        //         └─ SG_AnnScrollHandle
        //
        // topOffset is negative (distance down from the panel's top edge); bottomInset
        // is positive (distance up from its bottom edge).
        private void BuildAnnouncementsScrollBox(Transform parent, Component tmpTemplate,
            string raw, float topOffset, float bottomInset)
        {
            var scrollGo = CreateChild("SG_AnnScroll", parent);
            var scrollRt = scrollGo.AddComponent<RectTransform>();
            scrollRt.anchorMin = Vector2.zero;
            scrollRt.anchorMax = Vector2.one;
            scrollRt.pivot     = new Vector2(0.5f, 0.5f);
            scrollRt.offsetMin = new Vector2(16f, bottomInset);
            scrollRt.offsetMax = new Vector2(-16f, topOffset);

            scrollGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);

            var scroll = scrollGo.AddComponent<ScrollRect>();
            scroll.horizontal       = false;
            scroll.vertical         = true;
            scroll.movementType     = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            scroll.inertia          = false;

            // ---- Viewport ----
            var viewGo = CreateChild("SG_AnnViewport", scrollGo.transform);
            var viewRt = viewGo.AddComponent<RectTransform>();
            viewRt.anchorMin = Vector2.zero;
            viewRt.anchorMax = Vector2.one;
            viewRt.pivot     = new Vector2(0f, 1f);
            viewRt.offsetMin = Vector2.zero;
            viewRt.offsetMax = new Vector2(-AnnScrollbarW, 0f);

            // Fully transparent, but still a raycast target: without a Graphic under the
            // pointer the mouse wheel has nothing to bubble a scroll event up from.
            var viewImg = viewGo.AddComponent<Image>();
            viewImg.color = new Color(1f, 1f, 1f, 0f);
            viewImg.raycastTarget = true;

            // RectMask2D rather than Mask: no extra material, and it doubles as a raycast
            // filter, so links scrolled out of view aren't clickable through the clip.
            viewGo.AddComponent<RectMask2D>();

            // ---- Content ----
            var contentGo = CreateChild("SG_AnnContent", viewGo.transform);
            var contentRt = contentGo.AddComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot     = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0f, AnnMinViewport);

            var textComp = CreateAnnouncementText(contentGo.transform, tmpTemplate, raw);

            // ---- Scrollbar ----
            var sbGo = CreateChild("SG_AnnScrollbar", scrollGo.transform);
            var sbRt = sbGo.AddComponent<RectTransform>();
            sbRt.anchorMin = new Vector2(1f, 0f);
            sbRt.anchorMax = new Vector2(1f, 1f);
            sbRt.pivot     = new Vector2(1f, 1f);
            sbRt.sizeDelta = new Vector2(AnnScrollbarW, 0f);
            sbRt.anchoredPosition = Vector2.zero;
            sbGo.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.07f);

            var sb = sbGo.AddComponent<Scrollbar>();
            sb.direction = Scrollbar.Direction.BottomToTop;

            var areaGo = CreateChild("SG_AnnScrollArea", sbGo.transform);
            var areaRt = areaGo.AddComponent<RectTransform>();
            areaRt.anchorMin = Vector2.zero;
            areaRt.anchorMax = Vector2.one;
            areaRt.offsetMin = Vector2.zero;
            areaRt.offsetMax = Vector2.zero;

            var handleGo = CreateChild("SG_AnnScrollHandle", areaGo.transform);
            var handleRt = handleGo.AddComponent<RectTransform>();
            handleRt.offsetMin = Vector2.zero;
            handleRt.offsetMax = Vector2.zero;
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.color = new Color(0.85f, 0.78f, 0.6f, 0.55f);

            sb.targetGraphic = handleImg;
            sb.handleRect    = handleRt;

            scroll.viewport                    = viewRt;
            scroll.content                     = contentRt;
            scroll.verticalScrollbar           = sb;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            StartCoroutine(FitAnnouncementContent(scroll, viewRt, contentRt, textComp));
        }

        // Creates the announcement label. Unlike CreateThemedLabelComponent this one is
        // left-aligned, stretches to fill its parent (the scroll content, whose height is
        // set later from the measured text) and carries the link click handler.
        private Component CreateAnnouncementText(Transform content, Component tmpTemplate, string raw)
        {
            var go = tmpTemplate != null
                ? Instantiate(tmpTemplate.gameObject, content, false)
                : CreateChild("SG_AnnText", content);
            go.name = "SG_AnnText";
            go.SetActive(true);

            // Same reason as CreateThemedLabelComponent: the cloned menu-button label
            // auto-sizes itself to one line and defeats word wrap.
            var csf = go.GetComponent<ContentSizeFitter>();
            if (csf != null) { csf.enabled = false; Destroy(csf); }
            var le = go.GetComponent<LayoutElement>();
            if (le != null) { le.enabled = false; Destroy(le); }

            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(AnnTextPad, AnnTextPad);
            rt.offsetMax = new Vector2(-AnnTextPad, -AnnTextPad);
            rt.localScale = Vector3.one;

            if (tmpTemplate != null)
            {
                var tmp = go.GetComponent(tmpTemplate.GetType());
                SetTmpProperty(tmp, "text", FormatAnnouncementsRich(raw));
                SetTmpProperty(tmp, "fontSize", 15f);
                SetTmpProperty(tmp, "color", new Color(0.88f, 0.88f, 0.88f, 1f));
                SetTmpProperty(tmp, "enableAutoSizing", false);
                SetTmpProperty(tmp, "enableWordWrapping", true);
                SetTmpProperty(tmp, "richText", true);
                // Needed twice over: the link hit test needs a raycast on this graphic,
                // and the wheel needs something to bubble a scroll from.
                SetTmpProperty(tmp, "raycastTarget", true);
                SetTmpEnum(tmp, "overflowMode", "Overflow");
                SetTmpEnum(tmp, "alignment", "TopLeft");
                SetTmpEnum(tmp, "fontStyle", "Normal");

                go.AddComponent<AnnouncementLinkClicker>().Init(tmp);
                return tmp;
            }

            // Fallback (no TMP available): no link clicking, URLs shown inline instead.
            var t = go.AddComponent<Text>();
            t.font       = Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize   = 15;
            t.color      = new Color(0.88f, 0.88f, 0.88f, 1f);
            t.alignment  = TextAnchor.UpperLeft;
            t.supportRichText    = true;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow   = VerticalWrapMode.Overflow;
            t.text = FormatAnnouncementsPlain(raw);
            return t;
        }

        // Sizes the scroll content to the laid-out text. Deferred by a frame because the
        // Destroy()d ContentSizeFitter/LayoutElement are still alive this frame, and the
        // text can't report a preferred height until the canvas has given it a width.
        // Measured twice: the first pass establishes the width, the second measures
        // against it.
        private IEnumerator FitAnnouncementContent(ScrollRect scroll, RectTransform viewport,
            RectTransform content, Component textComp)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                yield return null;
                if (scroll == null || content == null || textComp == null) yield break;

                Canvas.ForceUpdateCanvases();

                float measured = 0f;
                var uiText = textComp as Text;
                if (uiText != null)
                {
                    measured = uiText.preferredHeight;
                }
                else
                {
                    var v = GetTmpProperty(textComp, "preferredHeight");
                    if (v is float) measured = (float)v;

                    // preferredHeight measures against TMP's cached margin width, which
                    // is only right once the graphic has been rebuilt at least once. If
                    // it comes back empty, ask for an explicit measurement at our own
                    // width rather than silently truncating the announcements.
                    if (measured <= 0f)
                        measured = MeasureTmpHeight(textComp, content.rect.width - AnnTextPad * 2f);
                }

                // Never shorter than the viewport: a content rect smaller than its
                // viewport makes ScrollRect place it oddly and the scrollbar useless.
                content.sizeDelta = new Vector2(0f,
                    Mathf.Max(measured + AnnTextPad * 2f, viewport.rect.height));
            }

            scroll.verticalNormalizedPosition = 1f;   // start at the top
        }

        // tmp.GetPreferredValues(width, height).y — an explicit measurement at a width we
        // choose, rather than whatever TMP last cached.
        private static float MeasureTmpHeight(Component tmp, float width)
        {
            if (tmp == null || width <= 0f) return 0f;
            try
            {
                foreach (var m in tmp.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (m.Name != "GetPreferredValues") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 2 || ps[0].ParameterType != typeof(float) || ps[1].ParameterType != typeof(float))
                        continue;
                    var v = m.Invoke(tmp, new object[] { width, 32767f });
                    if (v is Vector2) return ((Vector2)v).y;
                    return 0f;
                }
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] Announcement height measurement failed: {ex.Message}");
            }
            return 0f;
        }

        // Markdown-style link: [label](https://example.com)
        private static readonly Regex AnnLinkRegex =
            new Regex(@"\[([^\]\r\n]+)\]\(\s*([^)\s]+)\s*\)");

        // Rewrites [label](url) into TMP's <link> markup. Everything else is passed
        // through untouched, so the config author can also use <b>/<i>/<color> directly.
        internal static string FormatAnnouncementsRich(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            return AnnLinkRegex.Replace(raw.Replace("\r\n", "\n").TrimEnd(), m =>
            {
                string label = m.Groups[1].Value;
                string url   = m.Groups[2].Value;
                // A link we would refuse to open shouldn't look clickable.
                if (!IsOpenableUrl(url)) return label;
                return "<link=\"" + url + "\"><color=#7FB3FF><u>" + label + "</u></color></link>";
            });
        }

        // Legacy UnityEngine.UI.Text has no <link> support, so show the URL inline.
        internal static string FormatAnnouncementsPlain(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            return AnnLinkRegex.Replace(raw.Replace("\r\n", "\n").TrimEnd(),
                m => m.Groups[1].Value + " (" + m.Groups[2].Value + ")");
        }

        // client.yaml ships inside modpacks, so the announcement text is not necessarily
        // written by the person sitting at the keyboard. Restrict what a click can launch
        // to web pages - no file://, no custom scheme handlers.
        private static bool IsOpenableUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return url.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        // Opens the URL behind a <link=...> span when the player clicks it.
        //
        // The plugin deliberately doesn't reference Unity.TextMeshPro (see the reflection
        // helpers below), so the hit test goes through TMP_TextUtilities.FindIntersectingLink
        // by reflection as well.
        internal class AnnouncementLinkClicker : MonoBehaviour, IPointerClickHandler
        {
            private Component _tmp;
            private Canvas    _canvas;
            private static MethodInfo _finder;
            private static bool       _finderResolved;

            internal void Init(Component tmp)
            {
                _tmp    = tmp;
                _canvas = GetComponentInParent<Canvas>();
            }

            public void OnPointerClick(PointerEventData eventData)
            {
                // A release that ends a scroll drag is not a click on a link.
                if (_tmp == null || eventData == null || eventData.dragging) return;

                try
                {
                    var finder = ResolveFinder(_tmp.GetType());
                    if (finder == null) return;

                    Camera cam = (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                        ? _canvas.worldCamera
                        : null;

                    var result = finder.Invoke(null, new object[] { _tmp, (Vector3)eventData.position, cam });
                    if (!(result is int)) return;
                    int index = (int)result;
                    if (index < 0) return;

                    string url = GetLinkId(_tmp, index);
                    if (string.IsNullOrEmpty(url)) return;
                    if (!IsOpenableUrl(url))
                    {
                        LogS?.LogWarning($"[ServerGuard.Client] Ignoring announcement link with an unsupported scheme: {url}");
                        return;
                    }

                    LogS?.LogInfo($"[ServerGuard.Client] Opening announcement link: {url}");
                    Application.OpenURL(url);
                }
                catch (Exception ex)
                {
                    LogS?.LogWarning($"[ServerGuard.Client] Announcement link click failed: {ex.Message}");
                }
            }

            // TMP_TextUtilities.FindIntersectingLink(TMP_Text, Vector3, Camera).
            // Matched by shape rather than by an exact parameter-type array, because we
            // can't name the TMP_Text type at compile time.
            private static MethodInfo ResolveFinder(Type tmpType)
            {
                if (_finderResolved) return _finder;
                _finderResolved = true;

                var utils = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType("TMPro.TMP_TextUtilities"); } catch { return null; } })
                    .FirstOrDefault(t => t != null);
                if (utils == null)
                {
                    LogS?.LogWarning("[ServerGuard.Client] TMP_TextUtilities not found - announcement links won't be clickable.");
                    return null;
                }

                foreach (var m in utils.GetMethods(BindingFlags.Static | BindingFlags.Public))
                {
                    if (m.Name != "FindIntersectingLink") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 3) continue;
                    if (!ps[0].ParameterType.IsAssignableFrom(tmpType)) continue;
                    if (ps[1].ParameterType != typeof(Vector3)) continue;
                    if (ps[2].ParameterType != typeof(Camera)) continue;
                    _finder = m;
                    break;
                }
                if (_finder == null)
                    LogS?.LogWarning("[ServerGuard.Client] FindIntersectingLink(TMP_Text, Vector3, Camera) not found - announcement links won't be clickable.");
                return _finder;
            }

            // tmp.textInfo.linkInfo[index].GetLinkID()
            private static string GetLinkId(Component tmp, int index)
            {
                var textInfo = GetTmpProperty(tmp, "textInfo")
                    ?? tmp.GetType().GetField("m_textInfo",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetValue(tmp);
                if (textInfo == null) return null;

                var arr = textInfo.GetType()
                    .GetField("linkInfo", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(textInfo) as Array;
                if (arr == null || index >= arr.Length) return null;

                var info = arr.GetValue(index);
                var get  = info?.GetType().GetMethod("GetLinkID", BindingFlags.Instance | BindingFlags.Public);
                return get?.Invoke(info, null) as string;
            }
        }

        // ---- Helpers ----

        private static GameObject CreateChild(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        private static object GetField(object obj, string name)
        {
            var f = obj?.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f?.GetValue(obj);
        }

        // Returns the TMP text component used by a main-menu button, so panel labels
        // can share the same font as the (cloned) Connect button.
        private static Component GetMenuButtonLabelTemplate(FejdStartup menu)
        {
            var buttons = GetField(menu, "m_menuButtons") as Button[];
            var template = buttons?.FirstOrDefault(b => b != null);
            if (template == null) return null;
            foreach (var comp in template.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var tn = comp.GetType().Name;
                if (tn == "TextMeshProUGUI" || tn == "TMP_Text")
                    return comp;
            }
            return null;
        }

        // Clones the given TMP template (Valheim's version label) into a word-wrapped
        // label at the top of the panel. Returns the new contentTop. Falls back to a
        // UnityEngine.UI.Text with Arial if no TMP template is available.
        private float AddThemedLabel(string name, Transform parent, Component tmpTemplate,
            string text, float fontSize, bool bold, Color color, float topOffset, float height)
        {
            CreateThemedLabelComponent(name, parent, tmpTemplate, text, fontSize, bold, color, topOffset, height);
            return topOffset - (height + 4f);
        }

        // Creates a themed label and returns the text Component (TMP_Text or UI.Text)
        // so callers can update it later (e.g. the live player count).
        //
        // anchorBottom pins the label `bottomOffset` px above the panel's bottom edge
        // instead of `topOffset` px below its top edge — used for the elements that sit
        // under the announcements scroll box, which has to own the flexible middle.
        private Component CreateThemedLabelComponent(string name, Transform parent, Component tmpTemplate,
            string text, float fontSize, bool bold, Color color, float topOffset, float height,
            bool anchorBottom = false, float bottomOffset = 0f)
        {
            var go = tmpTemplate != null
                ? Instantiate(tmpTemplate.gameObject, parent, false)
                : CreateChild(name, parent);
            go.name = name;
            go.SetActive(true);

            // Cloned menu-button labels carry a ContentSizeFitter/LayoutElement that
            // auto-size the label to one line and defeat word wrap — strip them so our
            // fixed width takes effect and text wraps inside the panel.
            var csf = go.GetComponent<ContentSizeFitter>();
            if (csf != null) Destroy(csf);
            var le = go.GetComponent<LayoutElement>();
            if (le != null) Destroy(le);

            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            if (anchorBottom)
            {
                rt.anchorMin = new Vector2(0.05f, 0f);
                rt.anchorMax = new Vector2(0.95f, 0f);
                rt.pivot     = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, bottomOffset);
            }
            else
            {
                rt.anchorMin = new Vector2(0.05f, 1f);
                rt.anchorMax = new Vector2(0.95f, 1f);
                rt.pivot     = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, topOffset);
            }
            rt.sizeDelta = new Vector2(0f, height);
            rt.localScale = Vector3.one;

            if (tmpTemplate != null)
            {
                var tmp = go.GetComponent(tmpTemplate.GetType());
                SetTmpProperty(tmp, "text", text);
                SetTmpProperty(tmp, "fontSize", fontSize);
                SetTmpProperty(tmp, "color", color);
                SetTmpProperty(tmp, "enableAutoSizing", false);
                SetTmpProperty(tmp, "enableWordWrapping", true);
                SetTmpEnum(tmp, "overflowMode", "Overflow");
                SetTmpEnum(tmp, "alignment", "Top");
                SetTmpEnum(tmp, "fontStyle", bold ? "Bold" : "Normal");
                return tmp;
            }

            // Fallback (no TMP available).
            var t = go.AddComponent<Text>();
            t.font       = Resources.GetBuiltinResource<Font>("Arial.ttf");
            t.fontSize   = Mathf.RoundToInt(fontSize);
            t.fontStyle  = bold ? FontStyle.Bold : FontStyle.Normal;
            t.color      = color;
            t.alignment  = TextAnchor.UpperCenter;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow   = VerticalWrapMode.Overflow;
            t.text = text;
            return t;
        }

        // Clones a real Valheim main-menu button so the Connect button inherits the
        // game's button graphic, hover sfx and font. Falls back to a plain green
        // button if no template can be found.
        private void AddConnectButton(FejdStartup menu, Transform parent)
        {
            var buttons = GetField(menu, "m_menuButtons") as Button[];
            var template = buttons?.FirstOrDefault(b => b != null);

            if (template != null)
            {
                var go = Instantiate(template.gameObject, parent, false);
                go.name = "SG_ConnectBtn";
                go.SetActive(true);

                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot     = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, 16f);
                rt.sizeDelta = new Vector2(240f, 48f);
                rt.localScale = Vector3.one;

                SetAnyText(go, "Connect");

                var btn = go.GetComponent<Button>();
                btn.onClick = new Button.ButtonClickedEvent();
                btn.onClick.AddListener(() => ConnectToConfiguredServer(menu));
                return;
            }

            // Fallback: plain themed button.
            var btnGo = CreateChild("SG_ConnectBtn", parent);
            var brt = btnGo.AddComponent<RectTransform>();
            brt.anchorMin = new Vector2(0.5f, 0f);
            brt.anchorMax = new Vector2(0.5f, 0f);
            brt.pivot     = new Vector2(0.5f, 0f);
            brt.anchoredPosition = new Vector2(0f, 16f);
            brt.sizeDelta = new Vector2(240f, 44f);
            btnGo.AddComponent<Image>().color = new Color(0.15f, 0.45f, 0.15f, 1f);
            var fb = btnGo.AddComponent<Button>();

            var txtGo = CreateChild("SG_ConnectBtnText", btnGo.transform);
            var trt = txtGo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            var txt = txtGo.AddComponent<Text>();
            txt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.fontSize = 18; txt.fontStyle = FontStyle.Bold; txt.color = Color.white;
            txt.alignment = TextAnchor.MiddleCenter; txt.text = "Connect";

            fb.onClick.AddListener(() => ConnectToConfiguredServer(menu));
        }

        // Sets the label text on a cloned object regardless of whether it uses
        // TextMeshPro or legacy UnityEngine.UI.Text.
        private static void SetAnyText(GameObject go, string text)
        {
            foreach (var comp in go.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var tn = comp.GetType().Name;
                if (tn == "TextMeshProUGUI" || tn == "TMP_Text")
                    SetTmpProperty(comp, "text", text);
                else if (comp is Text uiText)
                    uiText.text = text;
            }
        }

        private static void SetAnyText(Component comp, string text)
        {
            if (comp == null) return;
            if (comp is Text uiText) { uiText.text = text; return; }
            SetTmpProperty(comp, "text", text);
        }

        private static void SetTmpProperty(object tmp, string prop, object val)
        {
            if (tmp == null) return;
            var p = tmp.GetType().GetProperty(prop,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.CanWrite)
            {
                try { p.SetValue(tmp, val, null); } catch { }
            }
        }

        private static object GetTmpProperty(object tmp, string prop)
        {
            if (tmp == null) return null;
            var p = tmp.GetType().GetProperty(prop,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null || !p.CanRead) return null;
            try { return p.GetValue(tmp, null); } catch { return null; }
        }

        private static void SetTmpEnum(object tmp, string prop, string enumName)
        {
            if (tmp == null) return;
            var p = tmp.GetType().GetProperty(prop,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p == null) return;
            try
            {
                var v = Enum.Parse(p.PropertyType, enumName);
                p.SetValue(tmp, v, null);
            }
            catch { }
        }

        private static Texture2D LoadTexture(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    LogS?.LogWarning($"[ServerGuard.Client] Logo file not found: {path}");
                    return null;
                }
                var data = File.ReadAllBytes(path);
                var tex  = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                // In this Unity version LoadImage is NOT an instance method on Texture2D —
                // it was moved into the static UnityEngine.ImageConversion class (in
                // UnityEngine.ImageConversionModule). We can't reference that module
                // directly (netstandard 2.1 vs our net462 target), so resolve it at
                // runtime. Try the modern static extension first, then the legacy
                // instance method as a fallback for older builds.
                var convType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType("UnityEngine.ImageConversion"); } catch { return null; } })
                    .FirstOrDefault(t => t != null);
                var staticLoad = convType?.GetMethod("LoadImage",
                    BindingFlags.Static | BindingFlags.Public,
                    null, new[] { typeof(Texture2D), typeof(byte[]) }, null);
                if (staticLoad != null)
                {
                    var ok = staticLoad.Invoke(null, new object[] { tex, data });
                    if (ok is bool b && !b)
                        LogS?.LogWarning("[ServerGuard.Client] ImageConversion.LoadImage returned false — unsupported image (use PNG or JPG).");
                    return tex;
                }

                var instLoad = typeof(Texture2D).GetMethod("LoadImage",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(byte[]) }, null);
                if (instLoad != null)
                {
                    instLoad.Invoke(tex, new object[] { data });
                    return tex;
                }

                LogS?.LogWarning("[ServerGuard.Client] Could not resolve LoadImage; logo not displayed.");
                return null;
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] LoadTexture failed: {ex.Message}");
                return null;
            }
        }

        // ---- Connect logic ----
        //
        // Valheim's FejdStartup.OnCharacterStart branches on GetServerToJoin().IsValid:
        // if a server is queued it calls JoinServer() directly (no browser/IP/password);
        // otherwise it falls through to ShowStartGame() (the world/server selection).
        //
        // The queued server can be cleared between menu navigation and the moment
        // OnCharacterStart checks it, so instead of relying on the timing we ARM the
        // join here and re-assert the queued server in a Prefix on OnCharacterStart
        // (see Patch_FejdStartup_OnCharacterStart). That guarantees the direct
        // JoinServer() path is taken. The arming is one-shot and is cleared if the
        // player backs out of character selection, so normal single-player starts are
        // never hijacked.
        private void ConnectToConfiguredServer(FejdStartup menu)
        {
            try
            {
                if (menu == null) return;

                var valheimAsm = typeof(FejdStartup).Assembly;
                var dedType  = valheimAsm.GetType("ServerJoinDataDedicated");
                var joinType = valheimAsm.GetType("ServerJoinData");
                if (dedType == null || joinType == null)
                {
                    LogS?.LogWarning("[ServerGuard.Client] ServerJoinData types not found; cannot connect.");
                    return;
                }

                // ServerJoinData(ServerJoinDataDedicated(host, port))
                var dedCtor = dedType.GetConstructor(new[] { typeof(string), typeof(ushort) });
                var dedicated = dedCtor.Invoke(new object[] { _clientSettings.ServerAddress, (ushort)_clientSettings.ServerPort });
                var joinCtor = joinType.GetConstructor(new[] { dedType });

                // Arm the one-shot quick-join.
                _armedJoinData  = joinCtor.Invoke(new[] { dedicated });
                _armedPassword  = _clientSettings.ServerPassword ?? "";
                _quickJoinArmed = true;

                // Apply immediately too (harmless; the prefix re-asserts at join time).
                ReassertServerToJoin(menu, keepArmed: true);

                var charScreen = GetField(menu, "m_characterSelectScreen") as GameObject;
                bool onCharSelect = charScreen != null && charScreen.activeInHierarchy;

                if (onCharSelect)
                {
                    // Character already selected — connect now.
                    var onCharStart = typeof(FejdStartup).GetMethod("OnCharacterStart",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    onCharStart.Invoke(menu, null);
                    LogS?.LogInfo($"[ServerGuard.Client] Connecting to {_clientSettings.ServerAddress}:{_clientSettings.ServerPort} with selected character.");
                }
                else
                {
                    // Main menu — hide it and open character selection (vanilla "Start Game").
                    var onStartGame = typeof(FejdStartup).GetMethod("OnStartGame",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    onStartGame.Invoke(menu, null);
                    LogS?.LogInfo($"[ServerGuard.Client] Quick-join armed for {_clientSettings.ServerAddress}:{_clientSettings.ServerPort}; select a character to connect.");
                }
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] ConnectToConfiguredServer failed: {ex.Message}");
            }
        }

        // Re-applies the armed server + password onto FejdStartup so OnCharacterStart
        // sees a valid server to join and connects directly. One-shot unless keepArmed.
        //
        // IMPORTANT (verified by IL): OnCharacterStart checks m_queuedJoinServer — NOT
        // m_joinServer (which is what SetServerToJoin sets). If m_queuedJoinServer is
        // valid it copies it into m_joinServer, clears the queue and calls JoinServer();
        // otherwise it falls into ShowStartGame() (the world-selection panel). So the
        // queued field is the one we must write.
        internal void ReassertServerToJoin(FejdStartup menu, bool keepArmed = false)
        {
            try
            {
                if (menu == null || _armedJoinData == null) return;

                var queuedField = typeof(FejdStartup).GetField("m_queuedJoinServer",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (queuedField != null)
                {
                    queuedField.SetValue(menu, _armedJoinData);
                }
                else
                {
                    // Older builds may not have the queued field; fall back to the setter.
                    LogS?.LogWarning("[ServerGuard.Client] m_queuedJoinServer not found; falling back to SetServerToJoin.");
                    var setServer = typeof(FejdStartup).GetMethod("SetServerToJoin",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    setServer?.Invoke(menu, new[] { _armedJoinData });
                }

                // ServerPassword is a STATIC property. ZNet.RPC_ClientHandshake reads it
                // during the connection handshake — when set, the in-game password
                // dialog is skipped entirely. (An Instance-flags lookup returns null and
                // silently does nothing — that was the cause of the password prompt.)
                var passProp = typeof(FejdStartup).GetProperty("ServerPassword",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? typeof(FejdStartup).GetProperty("ServerPassword",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (passProp != null)
                {
                    var target = passProp.GetGetMethod(true)?.IsStatic == true ? null : (object)menu;
                    passProp.SetValue(target, _armedPassword ?? "", null);
                }

                LogS?.LogInfo("[ServerGuard.Client] Quick-join server + password asserted on FejdStartup.");
                if (!keepArmed) _quickJoinArmed = false;
            }
            catch (Exception ex)
            {
                LogS?.LogWarning($"[ServerGuard.Client] ReassertServerToJoin failed: {ex.Message}");
            }
        }

        internal void DisarmQuickJoin() => _quickJoinArmed = false;

        // ---- Live player count (A2S_INFO query) ----
        // Queries the server's Steam query port and updates _playerCountText.
        // The UDP exchange blocks, so it runs on a background thread; the coroutine
        // just polls for the answer and writes it on the main thread.
        private IEnumerator RefreshPlayerCount(string host, int gamePort)
        {
            // Give the UI a frame to render before starting the query.
            yield return null;

            // Single-element box instead of a captured local — no ValueTuple, and the
            // worker's completion (IsAlive == false) is what publishes the write.
            var box = new string[1];
            var worker = new System.Threading.Thread(() =>
            {
                // Valheim's query port is the game port + 1 (2457 for the default
                // 2456). Fall back to the game port for hosts that map it differently.
                int[] ports = { gamePort + 1, gamePort };
                foreach (var port in ports)
                {
                    int players, maxPlayers;
                    if (!QueryA2SInfo(host, port, out players, out maxPlayers)) continue;
                    box[0] = maxPlayers > 0
                        ? $"Players: {players} / {maxPlayers}"
                        : $"Players: {players}";
                    return;
                }
            });
            worker.IsBackground = true;
            worker.Start();

            float deadline = Time.realtimeSinceStartup + 10f;
            while (worker.IsAlive && Time.realtimeSinceStartup < deadline)
                yield return null;

            var result = box[0];
            if (result == null)
                LogS?.LogInfo($"[ServerGuard.Client] Player-count query to {host}:{gamePort + 1} got no answer.");

            if (_playerCountText != null)
                SetAnyText(_playerCountText, result ?? "Players: ?");
        }

        // Sends A2S_INFO to host:port and reads back the player counts.
        //
        // Since the December 2020 Valve update — which Valheim's server inherits via
        // the Steam game-server API — the first A2S_INFO gets an S2C_CHALLENGE reply
        // ('A', 0x41) instead of the info packet. The query has to be resent with the
        // 4-byte challenge appended before the server answers with 'I' (0x49).
        // Not doing that is why the panel only ever showed "Players: ?".
        private static bool QueryA2SInfo(string host, int port, out int players, out int maxPlayers)
        {
            players    = 0;
            maxPlayers = 0;
            try
            {
                using (var udp = new UdpClient())
                {
                    udp.Client.ReceiveTimeout = 2000;
                    udp.Client.SendTimeout    = 2000;
                    udp.Connect(host, port);

                    byte[] challenge = null;
                    // One initial query plus up to two challenge round-trips.
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        var request = BuildA2SInfoRequest(challenge);
                        udp.Send(request, request.Length);

                        var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                        var response = udp.Receive(ref ep);

                        // Only single-packet replies (0xFFFFFFFF) are handled; A2S_INFO
                        // never splits in practice.
                        if (response == null || response.Length < 5) return false;
                        if (response[0] != 0xFF || response[1] != 0xFF ||
                            response[2] != 0xFF || response[3] != 0xFF) return false;

                        if (response[4] == 0x41 && response.Length >= 9)
                        {
                            challenge = new byte[4];
                            Array.Copy(response, 5, challenge, 0, 4);
                            continue;
                        }

                        if (response[4] == 0x49)
                            return ParseA2SInfo(response, out players, out maxPlayers);

                        return false;
                    }
                }
            }
            catch { /* timeout, unreachable host, or bad address */ }
            return false;
        }

        // A2S_INFO request: 0xFFFFFFFF + 'T' + "Source Engine Query\0", with the
        // 4-byte challenge appended once the server has issued one.
        private static byte[] BuildA2SInfoRequest(byte[] challenge)
        {
            var payload = Encoding.ASCII.GetBytes("Source Engine Query\0");
            var request = new byte[5 + payload.Length + (challenge != null ? 4 : 0)];
            request[0] = request[1] = request[2] = request[3] = 0xFF;
            request[4] = 0x54;
            payload.CopyTo(request, 5);
            if (challenge != null) challenge.CopyTo(request, 5 + payload.Length);
            return request;
        }

        // Layout after the 0xFFFFFFFF header and 'I': protocol byte, the
        // null-terminated Name/Map/Folder/Game strings, a 2-byte AppID, then the
        // player, max-player and bot counts.
        private static bool ParseA2SInfo(byte[] response, out int players, out int maxPlayers)
        {
            players    = 0;
            maxPlayers = 0;

            int idx = 6;
            for (int skip = 0; skip < 4; skip++)
            {
                while (idx < response.Length && response[idx] != 0) idx++;
                idx++; // step over the terminator
            }
            idx += 2; // AppID (short)

            if (idx + 1 >= response.Length) return false;
            players    = response[idx];
            maxPlayers = response[idx + 1];
            return true;
        }
    }
}
