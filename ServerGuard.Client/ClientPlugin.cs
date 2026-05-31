using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
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
        public const string VERSION = "1.4.0";

        internal static ClientPlugin Instance;
        internal static ManualLogSource LogS;
        private Harmony _harmony;

        private string _sharedSecret = "";
        private List<ModManifestEntry> _cachedManifest;

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
                    sb.AppendLine("# sharedSecret MUST match the server's settings.yaml `sharedSecret` value");
                    sb.AppendLine("# verbatim. The server will reject manifests whose HMAC does not match.");
                    sb.AppendLine("# Leave empty only if the server has `requireHmac: false` (insecure).");
                    sb.AppendLine("sharedSecret: \"\"");
                    File.WriteAllText(ClientYaml, sb.ToString());
                }

                var deser = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var doc = deser.Deserialize<ClientSettings>(File.ReadAllText(ClientYaml)) ?? new ClientSettings();
                _sharedSecret = doc.SharedSecret ?? "";
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
    }
}
