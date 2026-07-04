using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;
using ValheimServerGuard.Shared;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ValheimServerGuardClient
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class ClientPlugin : BaseUnityPlugin
    {
        public const string GUID    = "com.taeguk.valheim.serverguard.client";
        public const string NAME    = "Valheim ServerGuard Client";
        public const string VERSION = "1.6.0";

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

        // Command names that are always blocked on multiplayer clients regardless of
        // whether Valheim has them flagged as "cheat: true". `devcommands` itself is the
        // enable flag; the rest are common abuse vectors. Console.IsCheatsEnabled is
        // also force-overridden to false, which blocks every cheat-flagged command at
        // the Terminal level - this list is belt-and-suspenders.
        private static readonly HashSet<string> BlockedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "devcommands", "debugmode", "imacheater",
            "god", "ghost", "fly", "nocost", "noplacementcost",
            "spawn", "pos", "goto", "tame", "killall",
            "event", "stopevent", "tod", "skiptime", "sleep",
            "raiseskill", "resetcharacter", "heal", "puke", "damage",
            "setkey", "resetkeys", "removedrops", "freefly"
        };

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
        }

        private void Awake()
        {
            Instance = this;
            LogS = Logger;

            EnsureConfig();

            _harmony = new Harmony(GUID);
            _harmony.PatchAll();

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
                sb.AppendLine("# The companion plugin (this DLL) is intentionally listed under required_mods,");
                sb.AppendLine("# NOT allowed_mods - the server demands its presence.");
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
                    File.WriteAllText(ClientYaml, sb.ToString());
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
        [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
        public static class Patch_PlacePiece_Report
        {
            public static void Postfix(Player __instance, Piece piece, Vector3 pos)
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

                    ClientPlugin.Instance?.SendBuildPlace(pieceName, pos);
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] PlacePiece hook error: {ex.Message}");
                }
            }
        }

        internal void SendBuildPlace(string pieceName, Vector3 pos)
        {
            if (_serverRpc == null) return;
            try
            {
                var name = SanitiseShort(pieceName, 64);
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var payload = string.Format(inv,
                    "{0}|{1:F1}|{2:F1}|{3:F1}",
                    name, pos.x, pos.y, pos.z);
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
        internal void ReportDevcommand(string command)
        {
            try
            {
                LogS.LogWarning($"[ServerGuard.Client] Blocked cheat attempt: `{command}` (multiplayer client)");
                if (_serverRpc != null)
                {
                    try { _serverRpc.Invoke("ServerGuard_DevcommandAttempt", command ?? ""); }
                    catch (Exception ex) { LogS.LogWarning($"[ServerGuard.Client] Could not report devcommand to server: {ex.Message}"); }
                }
            }
            catch { /* never let the gate throw into Valheim */ }
        }

        // Called by the animation-cancel patches when they swallow an emote / sheathe /
        // other cancel input that arrived mid-attack. `source` is a short tag (e.g.
        // "emote", "sheathe") so the server log + Discord can show what vector was used.
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

        // Looks up `cmd` in Terminal's command registry via reflection and returns true
        // if the registered command is flagged as a cheat. Robust across Valheim builds
        // because we probe for several common field/property names.
        private static bool IsRegisteredCheatCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            try
            {
                var terminalType = typeof(Terminal);

                // Find the command dictionary on Terminal. Naming has varied across
                // builds: `commands`, `m_commands`, `s_commands` etc. Pick the first
                // static field whose type implements IDictionary.
                System.Collections.IDictionary commands = null;
                foreach (var f in terminalType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (typeof(System.Collections.IDictionary).IsAssignableFrom(f.FieldType))
                    {
                        var val = f.GetValue(null) as System.Collections.IDictionary;
                        if (val != null) { commands = val; break; }
                    }
                }
                if (commands == null) return false;

                object cmdObj = null;
                foreach (System.Collections.DictionaryEntry entry in commands)
                {
                    if (entry.Key is string k && string.Equals(k, cmd, StringComparison.OrdinalIgnoreCase))
                    {
                        cmdObj = entry.Value;
                        break;
                    }
                }
                if (cmdObj == null) return false;

                // Inspect the command object for a cheat flag. Try fields then properties,
                // common names first.
                var t = cmdObj.GetType();
                foreach (var name in new[] { "IsCheat", "isCheat", "m_isCheat", "Cheat", "cheat" })
                {
                    var fi = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fi != null && fi.FieldType == typeof(bool))
                    {
                        return fi.GetValue(cmdObj) is bool fb && fb;
                    }
                    var pi = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (pi != null && pi.PropertyType == typeof(bool))
                    {
                        return pi.GetValue(cmdObj) is bool pb && pb;
                    }
                }
            }
            catch { /* fall through */ }
            return false;
        }

        // -------------- Animation-cancel gate --------------
        //
        // Classic Valheim attack-spam exploit: trigger an emote (or sheathe weapon)
        // mid-attack to cancel the recovery animation. The next attack then fires faster
        // than the weapon's animation should allow. The fix is to refuse those state
        // transitions while Player.InAttack() returns true.
        //
        // Only blocks on multiplayer clients - single-player / host keeps full control.
        // Only blocks the LOCAL player's input - we never interfere with how other
        // players' animations sync over the network.
        //
        // Patches:
        //   * Player.StartEmote         - emote cancel (the most common exploit)
        //   * Humanoid.HideHandItems    - sheathe cancel (weapon-swap / press-R)

        private static bool ShouldBlockAnimationCancel(Player p)
        {
            try
            {
                if (!IsActiveMultiplayerClient()) return false;
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

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.HideHandItems))]
        public static class Patch_Humanoid_HideHandItems_BlockDuringAttack
        {
            // Humanoid.HideHandItems is called for any humanoid (player, draugr, NPC, ...)
            // when their weapons get holstered. We only care about the LOCAL player's
            // own input on a multiplayer client, so the ShouldBlockAnimationCancel check
            // also filters out non-Player Humanoids and non-local Players.
            public static bool Prefix(Humanoid __instance)
            {
                try
                {
                    var asPlayer = __instance as Player;
                    if (asPlayer == null) return true;
                    if (ShouldBlockAnimationCancel(asPlayer))
                    {
                        ClientPlugin.Instance?.ReportAnimationCancel("sheathe");
                        return false; // skip - weapons stay drawn, animation finishes naturally
                    }
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Sheathe gate error: {ex.Message}");
                }
                return true;
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

                    // Below this line: the devcommand gate. Only active on multiplayer clients.
                    if (!IsActiveMultiplayerClient()) return true;

                    bool isHardBlocked = BlockedCommands.Contains(cmdNoSlash);
                    bool isCheatTagged = !isHardBlocked && IsRegisteredCheatCommand(cmdNoSlash);

                    if (isHardBlocked || isCheatTagged)
                    {
                        ClientPlugin.Instance?.ReportDevcommand(cmdNoSlash);
                        return false; // skip original - command is swallowed
                    }
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Devcommand gate error: {ex.Message}");
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
        [HarmonyPatch(typeof(Chat), "SendText")]
        public static class Patch_Chat_SendText_Report
        {
            public static void Prefix(Talker.Type __0, string __1)
            {
                try
                {
                    if (ZNet.instance == null || ZNet.instance.IsServer()) return;
                    if (__0 != Talker.Type.Shout) return;
                    if (string.IsNullOrWhiteSpace(__1)) return;

                    ClientPlugin.Instance?.SendChatReport((int)__0, __1);
                }
                catch (Exception ex)
                {
                    ClientPlugin.LogS?.LogWarning($"[ServerGuard.Client] Chat hook error: {ex.Message}");
                }
            }
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

            // ---- Player count ----
            _playerCountText = CreateThemedLabelComponent("SG_PlayerCount", _quickLoginPanel.transform,
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
        private Component CreateThemedLabelComponent(string name, Transform parent, Component tmpTemplate,
            string text, float fontSize, bool bold, Color color, float topOffset, float height)
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
            rt.anchorMin = new Vector2(0.05f, 1f);
            rt.anchorMax = new Vector2(0.95f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, topOffset);
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
        // Sends a minimal Source Engine server-info query (UDP) to port+1 and
        // parses the response player count. Updates _playerCountText on success.
        private IEnumerator RefreshPlayerCount(string host, int gamePort)
        {
            // Give the UI a frame to render before blocking.
            yield return null;

            var result = "Players: ?";
            try
            {
                // A2S_INFO query: 4-byte FF header + 0x54 + "Source Engine Query\0"
                byte[] request = new byte[25];
                request[0] = request[1] = request[2] = request[3] = 0xFF;
                request[4] = 0x54;
                Encoding.ASCII.GetBytes("Source Engine Query\0").CopyTo(request, 5);

                // Valheim's query port is the game port (some sources say +1; try both).
                int[] ports = { gamePort, gamePort + 1 };
                byte[] response = null;

                foreach (var port in ports)
                {
                    try
                    {
                        using var udp = new UdpClient();
                        udp.Client.ReceiveTimeout = 2000;
                        udp.Connect(host, port);
                        udp.Send(request, request.Length);
                        var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                        response = udp.Receive(ref ep);
                        if (response != null && response.Length > 14) break;
                        response = null;
                    }
                    catch { response = null; }
                }

                if (response != null && response.Length > 14 && response[4] == 0x49)
                {
                    // Skip header (5), protocol (1), then scan past null-terminated strings:
                    // Name, Map, Folder, Game (4 strings), then 2-byte ID, then player count.
                    int idx = 6;
                    for (int skip = 0; skip < 4 && idx < response.Length; skip++)
                        while (idx < response.Length && response[idx++] != 0) { }
                    idx += 2; // skip AppID (short)
                    if (idx < response.Length)
                    {
                        int players    = response[idx];
                        int maxPlayers = idx + 1 < response.Length ? response[idx + 1] : 0;
                        result = maxPlayers > 0
                            ? $"Players: {players} / {maxPlayers}"
                            : $"Players: {players}";
                    }
                }
            }
            catch { /* offline or unreachable — leave result as "?" */ }

            if (_playerCountText != null)
                SetAnyText(_playerCountText, result);
        }
    }
}
