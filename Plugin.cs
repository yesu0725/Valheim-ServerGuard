using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using ValheimServerGuard.Shared;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

[BepInPlugin("com.taeguk.valheim.serverguard", "Valheim ServerGuard", "1.5.0")]
public class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource LogS;
    private Harmony _harmony;
	
    // -------- Paths --------
    private static readonly string RootDir   = Path.Combine(Paths.ConfigPath, "ServerGuard");
    private static readonly string ConfDir   = Path.Combine(RootDir, "conf");
    private static readonly string ReadmeMD  = Path.Combine(RootDir, "README.md");

    private static readonly string SettingsYaml      = Path.Combine(ConfDir, "settings.yaml");
    private static readonly string AdminsYaml        = Path.Combine(ConfDir, "admins.yaml");
    private static readonly string AllowedModsYaml   = Path.Combine(ConfDir, "allowed_mods.yaml");
    private static readonly string RegistrationsYaml = Path.Combine(ConfDir, "registrations.yaml");
    private static readonly string ViolationsYaml    = Path.Combine(ConfDir, "violations.yaml");
    private static readonly string MetricsYaml       = Path.Combine(ConfDir, "metrics.yaml");

    // Legacy filenames (renamed to .legacy on first launch under v1.3+ if they exist)
    private static readonly string LegacyIgnoreModsYaml  = Path.Combine(ConfDir, "ignore_mods.yaml");
    private static readonly string LegacyModPatternsYaml = Path.Combine(ConfDir, "mod_patterns.yaml");

    // -------- YAML Serializer --------
    private static IDeserializer _yamlIn;
    private static ISerializer _yamlOut;

    // -------- In-memory state --------
    private Settings _settings;
    private HashSet<string> _admins = new(StringComparer.OrdinalIgnoreCase);
    private DetectionMetrics _metrics;

    // allowed_mods.yaml decoded into lookup-friendly form.
    private List<AllowedModEntry> _requiredMods = new();
    private List<AllowedModEntry> _allowedMods  = new();
    private List<AllowedModEntry> _bannedMods   = new();

    // SteamID -> outstanding manifest challenge. Keyed by peer.m_uid.
    private Dictionary<long, PendingAttestation> _pending = new();
    private readonly object _pendingLock = new object();

    // SteamID -> CharacterID
    private Dictionary<string, List<string>> _registrations = new(StringComparer.OrdinalIgnoreCase);

    // SteamID -> rule -> attempts
    private Dictionary<string, Dictionary<string, int>> _violations = new(StringComparer.OrdinalIgnoreCase);

    // Rule keys
    private const string RULE_COMPANION_MISSING       = "CompanionMissing";
    private const string RULE_HMAC_INVALID            = "HmacInvalid";
    private const string RULE_CHALLENGE_MISMATCH      = "ChallengeMismatch";
    private const string RULE_REQUIRED_MOD_MISSING    = "RequiredModMissing";
    private const string RULE_DISALLOWED_MOD          = "DisallowedMod";
    private const string RULE_BANNED_MOD              = "BannedMod";
    private const string RULE_CHAR_NAME_LIMIT         = "CharacterNameLimitExceeded";

    // -------- Raid event tracking --------
    private string    _currentRaidName         = null;
    private Vector3   _currentRaidPos          = Vector3.zero;
    private bool      _raidPaused              = false;
    private Coroutine _raidMonitorCoroutine    = null;

    // Maps Valheim internal event names -> human-readable in-game names.
    private static readonly Dictionary<string, string> RaidDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["army_eikthyr"]  = "Eikthyr Rallies His Herd",
        ["army_theelder"] = "The Forest Is Moving",
        ["army_bonemass"] = "A Foul Smell From the Swamp",
        ["army_moder"]    = "A Cold Wind Blows From the Mountains",
        ["army_goblin"]   = "The Horde Is Attacking",
        ["skeletons"]     = "Skeleton Surprise",
        ["blobs"]         = "The Ooze Bomb",
        ["foresttrolls"]  = "The Ground Is Shaking",
        ["wolves"]        = "You Are Being Hunted",
        ["bats"]          = "Bat Attack",
        ["surtlings"]     = "It's Raining Fire",
        ["army_gjall"]    = "Mistlands Quiver",
        ["army_gsecret"]  = "Seeker Swarm",
        ["army_dverger"]  = "Dverger Invasion",
        ["army_charred"]  = "Charred Assault",
        ["army_fallen"]   = "The Fallen March",
        ["army_asksvin"]  = "Asksvin Attack",
    };

    private static string GetRaidDisplayName(string internalName)
    {
        if (string.IsNullOrEmpty(internalName)) return internalName;
        return RaidDisplayNames.TryGetValue(internalName, out var display) ? display : internalName;
    }

    // File watchers (hot-reload)
    private FileSystemWatcher _watchSettings, _watchAdmins, _watchAllowed;
    private readonly Dictionary<string, DateTime> _lastSeenWrite = new();

    // -------------- Data Models --------------
    private class CountAsViolation
    {
        public bool CompanionMissing           { get; set; } = false;
        public bool HmacInvalid                { get; set; } = false;
        public bool ChallengeMismatch          { get; set; } = false;
        public bool RequiredModMissing         { get; set; } = false;
        public bool DisallowedMod              { get; set; } = false;
        public bool BannedMod                  { get; set; } = false;
        public bool CharacterNameLimitExceeded { get; set; } = true;
        public bool DevcommandAttempt          { get; set; } = true;
        public bool SpeedHack                  { get; set; } = true;
        public bool IllegalItem                { get; set; } = true;
        public bool StackOverflow              { get; set; } = true;
        public bool AnimationCancel            { get; set; } = false;
        public bool SkillOverflow              { get; set; } = true;
        public bool HashMismatch               { get; set; } = false;
    }

    private class Settings
    {
        // --- Core enforcement ---
        public int  ViolationThreshold   { get; set; } = 3;
        public bool Enforce              { get; set; } = true;
        public string KickMessage        { get; set; } = "You cannot join: server security policy violation. Contact an administrator.";
        public string BanReason          { get; set; } = "Auto-banned due to repeated security violations.";
        public int CharacterLimit        { get; set; } = 2;

        // --- Client-attestation handshake (v1.3+) ---
        // The server requests a signed mod manifest from every connecting peer via the
        // Valheim ServerGuard Client companion plugin. RequireCompanion=true means any
        // peer that fails to deliver a valid manifest is kicked (vanilla / wrong-modpack).
        public bool RequireCompanion         { get; set; } = true;
        public int  CompanionTimeoutSeconds  { get; set; } = 10;
        public bool RequireHmac              { get; set; } = true;
        public string SharedSecret           { get; set; } = "";
        public bool AllowUnlisted            { get; set; } = false;
        public int  MaxClockSkewSeconds      { get; set; } = 120;
        public bool LogPeerManifest          { get; set; } = false;

        // --- Operational ---
        public bool EnableMetrics            { get; set; } = true;
        public string discordWebhookUrl      { get; set; } = "";
        public string discordAdminWebhookUrl { get; set; } = "";
        // Backward-compat alias for the pre-v1.4 key name; ignored if discordAdminWebhookUrl is set.
        public string discordWebhookUrlAdmin { get; set; } = "";
        public string discordChannelLink     { get; set; } = "";
        public bool   maintenanceMode        { get; set; } = false;
        public bool   DiscordPublicMode      { get; set; } = true;
        public bool   DailySummaryEnabled    { get; set; } = true;
        public string DailySummaryChannel    { get; set; } = "admin";

        // --- Violation counting ---
        public CountAsViolation CountAsViolation { get; set; } = new CountAsViolation();

        // --- Active security gates (reserved; implementation forthcoming) ---
        public bool EnableDevcommandGate              { get; set; } = true;
        public bool EnableSpeedCheck                  { get; set; } = true;
        public float SpeedCheckMaxMetersPerSecond     { get; set; } = 30f;
        public int   SpeedCheckSampleSeconds          { get; set; } = 1;
        public int   SpeedCheckConsecutiveStrikes     { get; set; } = 3;
        public float SpeedCheckTeleportToleranceMeters{ get; set; } = 60f;
        public bool EnableInventoryCheck              { get; set; } = true;
        public bool InventoryCheckLogOnly             { get; set; } = true;
        public int  InventoryCheckStackTolerance      { get; set; } = 1;
        public bool EnableAnimationCancelGate         { get; set; } = true;
        public bool EnableSkillCap                    { get; set; } = true;
        public int  SkillCapMaxLevel                  { get; set; } = 100;
        public int  SkillCapTolerance                 { get; set; } = 5;

        // --- Cheat item removal ---
        // Items whose prefab names are listed here will be deleted from any non-admin
        // player's inventory immediately after they spawn into the world.
        public bool EnableCheatItemRemoval { get; set; } = true;
        public List<string> CheatItems     { get; set; } = new List<string> { "SwordCheat", "SledgeCheat" };

        // --- Logging ---
        public bool EnableDeathLog          { get; set; } = true;
        public bool EnableBuildLog          { get; set; } = true;
        public int  BuildLogRetentionDays   { get; set; } = 30;
        public bool EnableSelfTest          { get; set; } = true;
        public int  PingLogSampleSeconds    { get; set; } = 5;

        // --- Deprecated (kept for backward YAML parsing only; no runtime effect) ---
        public bool AggressiveNoModCheck   { get; set; } = false;
        public bool EnableAssemblyScanning { get; set; } = false;
        public bool UseWhitelistMode       { get; set; } = false;
        public bool RequireAttestation     { get; set; } = false;

        // Resolve the admin webhook URL, supporting the pre-v1.4 key name as a fallback.
        public string ResolvedAdminWebhookUrl =>
            !string.IsNullOrWhiteSpace(discordAdminWebhookUrl) ? discordAdminWebhookUrl : discordWebhookUrlAdmin;
    }

    private class AdminsDoc
    {
        public List<string> admins { get; set; } = new();
    }

    // allowed_mods.yaml schema. Each list entry is a string of the form:
    //   "GuidOrName"            - matches manifest entries by GUID or Name (case-insensitive)
    //   "GuidOrName|sha256hex"  - additionally pins the DLL hash; mismatch -> kick
    //
    // The [YamlMember(Alias = "...")] attributes pin the on-disk keys to snake_case
    // regardless of the deserializer's CamelCaseNamingConvention (which would otherwise
    // mangle `required_mods` -> `requiredMods` and silently parse zero entries).
    private class AllowedModsDoc
    {
        [YamlMember(Alias = "required_mods", ApplyNamingConventions = false)]
        public List<string> required_mods { get; set; } = new();

        [YamlMember(Alias = "allowed_mods", ApplyNamingConventions = false)]
        public List<string> allowed_mods  { get; set; } = new();

        [YamlMember(Alias = "banned_mods", ApplyNamingConventions = false)]
        public List<string> banned_mods   { get; set; } = new();
    }

    private class AllowedModEntry
    {
        public string Key;     // GUID or Name (lowercased for comparison)
        public string Sha256;  // optional, lowercase hex
    }

    // Per-peer challenge state. Created on connection, removed on manifest receipt or timeout.
    private class PendingAttestation
    {
        public string Challenge;
        public DateTime SentAt;
        public string SteamId;
        public ZNetPeer Peer;
    }

    private class DetectionMetrics
    {
        public long total_players_checked { get; set; } = 0;
        public long total_mods_detected { get; set; } = 0;
        public long phase1_rpc_detections { get; set; } = 0;
        public long phase2_assembly_detections { get; set; } = 0;
        public long version_keyword_detections { get; set; } = 0;
        public long allowlist_bypasses { get; set; } = 0;
        public long admin_bypasses { get; set; } = 0;
        public long violations_issued { get; set; } = 0;
        public long players_banned { get; set; } = 0;
        public Dictionary<string, long> top_detected_mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTime last_updated { get; set; } = DateTime.UtcNow;
    }

    private class RegistrationsDoc
	{
		public Dictionary<string, List<string>> registrations { get; set; } =
			new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
	}

    private class ViolationsDoc
    {
        public Dictionary<string, Dictionary<string, int>> violations { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // -------------- Unity Lifecycle --------------
    private void Awake()
    {
        Instance = this;
        LogS = Logger;

        // YAML serializer
        _yamlIn = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _yamlOut = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
            .Build();

        // Ensure folders + default files
        EnsureFoldersAndFiles();

        // Load YAML
        LoadSettings();
        LoadAdmins();
        LoadAllowedMods();
        LoadRegistrations();
        LoadViolations();
        LoadMetrics();

        // Start file watchers for hot-reload
        StartWatchers();

        // Harmony patches
        _harmony = new Harmony("com.taeguk.valheim.serverguard");
        _harmony.PatchAll();

        LogS.LogInfo(
            $"[ServerGuard] Loaded (v1.5.0). " +
            $"Enforcement: {(_settings.Enforce ? "ON" : "LOG-ONLY")}. " +
            $"RequireCompanion: {(_settings.RequireCompanion ? "ON" : "OFF")}. " +
            $"RequireHmac: {(_settings.RequireHmac ? "ON" : "OFF")}. " +
            $"AllowUnlisted: {(_settings.AllowUnlisted ? "ON" : "OFF")}. " +
            $"Required: {_requiredMods.Count}, Allowed: {_allowedMods.Count}, Banned: {_bannedMods.Count}. " +
            $"Metrics: {(_settings.EnableMetrics ? "ON" : "OFF")}");

        if (_settings.RequireHmac && !string.IsNullOrEmpty(_settings.SharedSecret))
        {
            // Print the secret once at startup so the operator can copy it into client.yaml.
            // Subsequent boots silently keep it.
            LogS.LogInfo($"[ServerGuard] sharedSecret in use (copy to every client.yaml): {_settings.SharedSecret}");
        }
		
		// Server boot notification
		_ = SendPublic(":white_check_mark: **Server is now online!**");
    }

    private void OnDestroy()
	{
		// Send shutdown notification synchronously so it fires before the process exits.
		try
		{
			var url = _settings?.maintenanceMode == true
				? _settings?.ResolvedAdminWebhookUrl
				: _settings?.discordWebhookUrl;
			if (!string.IsNullOrWhiteSpace(url))
			{
				var payload = new { content = ":octagonal_sign: **Server is shutting down.**" };
				var json = JsonConvert.SerializeObject(payload);
				using var req = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
				using var http = new System.Net.Http.HttpClient();
				http.PostAsync(url, req).GetAwaiter().GetResult();
			}
		}
		catch { }

		try
		{
			_harmony?.UnpatchSelf();
		}
		catch (Exception ex)
		{
			LogS?.LogWarning($"[ServerGuard] UnpatchSelf failed: {ex.Message}");
		}

		try
		{
			StopWatchers();
		}
		catch (Exception ex)
		{
			LogS?.LogWarning($"[ServerGuard] StopWatchers failed: {ex.Message}");
		}

		try
		{
			SaveAll();
		}
		catch (Exception ex)
		{
			LogS?.LogWarning($"[ServerGuard] SaveAll failed: {ex.Message}");
		}
	}
	
	// Sends to the public webhook. In maintenance mode, redirects to the admin webhook.
	private async Task SendPublic(string text)
    {
        try
        {
            var url = _settings?.maintenanceMode == true
                ? _settings?.ResolvedAdminWebhookUrl
                : _settings?.discordWebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            using var http = new System.Net.Http.HttpClient();
            var payload = new { content = text };
            var json = JsonConvert.SerializeObject(payload);
			using (var req = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json"))
			{
				await http.PostAsync(url, req);
			}
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] SendPublic failed: {ex.Message}");
        }
    }

	// Always sends to the admin webhook regardless of maintenance mode.
	private async Task SendAdmin(string text)
    {
        try
        {
            var url = _settings?.ResolvedAdminWebhookUrl;
            if (string.IsNullOrWhiteSpace(url)) return;

            using var http = new System.Net.Http.HttpClient();
            var payload = new { content = text };
            var json = JsonConvert.SerializeObject(payload);
			using (var req = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json"))
			{
				await http.PostAsync(url, req);
			}
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] SendAdmin failed: {ex.Message}");
        }
    }

    // -------------- Folder & File Bootstrapping --------------
    private void EnsureFoldersAndFiles()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(ConfDir);

        if (!File.Exists(SettingsYaml))
        {
            var secret = GenerateSharedSecret();
            var yaml = string.Join("\n", new[]
            {
                "# ServerGuard settings (v1.5.0)",
                "# ---------------------------------------------------------------",
                "# Core enforcement",
                "violationThreshold: 3",
                "enforce: true",
                "kickMessage: 'You cannot join: server security policy violation. Contact an administrator.'",
                "banReason: Auto-banned due to repeated security violations.",
                "characterLimit: 2",
                "",
                "# Client-attestation handshake (v1.3+)",
                "#   requireCompanion        - kick peers without the ServerGuard.Client companion plugin.",
                "#   requireHmac             - manifests must carry a valid HMAC signature.",
                "#   sharedSecret            - must match every client's client.yaml `sharedSecret`.",
                "#   allowUnlisted           - if true, mods absent from allowed_mods.yaml are tolerated.",
                "#   maxClockSkewSeconds     - reject manifests timestamped more than this many seconds off.",
                "#   logPeerManifest         - log every connecting peer's full mod list (verbose; for GUID harvesting).",
                "requireCompanion: true",
                "companionTimeoutSeconds: 10",
                "requireHmac: true",
                $"sharedSecret: '{secret}'",
                "maxClockSkewSeconds: 120",
                "",
                "# Metrics",
                "enableMetrics: true",
                "",
                "# Discord",
                "#   discordWebhookUrl      - public channel (server boot/shutdown, shouts, raids).",
                "#   discordAdminWebhookUrl - admin-only channel (violations, bans, whispers, full log stream).",
                "#   discordChannelLink     - optional link shown in server-online embeds.",
                "#   discordPublicMode      - if true, public events go to discordWebhookUrl; false sends everything to admin only.",
                "#   maintenanceMode        - redirect public events to the admin webhook temporarily.",
                "#   dailySummaryEnabled    - post a daily summary to dailySummaryChannel webhook.",
                "#   dailySummaryChannel    - 'public' or 'admin'.",
                "discordWebhookUrl: ''",
                "discordAdminWebhookUrl: ''",
                "discordChannelLink: ''",
                "discordPublicMode: true",
                "maintenanceMode: false",
                "dailySummaryEnabled: true",
                "dailySummaryChannel: admin",
                "",
                "# Which violation types count toward the ban threshold.",
                "# Set to false to log-only without incrementing the counter.",
                "countAsViolation:",
                "  companionMissing: false",
                "  hmacInvalid: false",
                "  challengeMismatch: false",
                "  requiredModMissing: false",
                "  disallowedMod: false",
                "  bannedMod: false",
                "  hashMismatch: false",
                "  characterNameLimitExceeded: true",
                "  devcommandAttempt: true",
                "  speedHack: true",
                "  illegalItem: true",
                "  stackOverflow: true",
                "  animationCancel: false",
                "  skillOverflow: true",
                "",
                "# Active security gates",
                "enableDevcommandGate: true",
                "enableSpeedCheck: true",
                "speedCheckMaxMetersPerSecond: 30",
                "speedCheckSampleSeconds: 1",
                "speedCheckConsecutiveStrikes: 3",
                "speedCheckTeleportToleranceMeters: 60",
                "enableInventoryCheck: true",
                "inventoryCheckLogOnly: true",
                "inventoryCheckStackTolerance: 1",
                "enableAnimationCancelGate: true",
                "enableSkillCap: true",
                "skillCapMaxLevel: 100",
                "skillCapTolerance: 5",
                "",
                "# Cheat item removal",
                "#   enableCheatItemRemoval - send removal command to companion plugin on player login.",
                "#   cheatItems            - prefab names to strip; add more or leave empty to disable per-item.",
                "enableCheatItemRemoval: true",
                "cheatItems:",
                "  - SwordCheat",
                "  - SledgeCheat",
                "",
                "# Logging",
                "enableDeathLog: true",
                "enableBuildLog: true",
                "buildLogRetentionDays: 30",
                "enableSelfTest: true",
                "pingLogSampleSeconds: 5",
            });
            File.WriteAllText(SettingsYaml, yaml + "\n");
        }

        if (!File.Exists(AdminsYaml))
        {
            var doc = new AdminsDoc { admins = new List<string>() };
            var sb = new StringBuilder();
            sb.AppendLine("# Admin whitelist: one SteamID per entry");
            sb.AppendLine(_yamlOut.Serialize(doc));
            File.WriteAllText(AdminsYaml, sb.ToString());
        }

        // v1.3+ migration: rename pre-attestation files out of the way so they don't
        // confuse admins. Their old contents wouldn't have been honored anyway.
        TryRenameLegacy(LegacyIgnoreModsYaml,  LegacyIgnoreModsYaml  + ".legacy");
        TryRenameLegacy(LegacyModPatternsYaml, LegacyModPatternsYaml + ".legacy");

        if (!File.Exists(AllowedModsYaml))
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ServerGuard allowed_mods.yaml (v1.3+)");
            sb.AppendLine("#");
            sb.AppendLine("# Each entry references a mod by its BepInEx plugin GUID (preferred) or display Name.");
            sb.AppendLine("# Optional `|<sha256_hex>` suffix pins the DLL hash; mismatch -> kick.");
            sb.AppendLine("#");
            sb.AppendLine("#   required_mods: every connecting client MUST report all of these in its manifest.");
            sb.AppendLine("#   allowed_mods : extra mods the client may run beyond the required set.");
            sb.AppendLine("#   banned_mods  : if any of these appear in the client manifest, the client is kicked.");
            sb.AppendLine("#");
            sb.AppendLine("# To harvest GUIDs from a real client connection, set logPeerManifest: true in settings.yaml.");
            sb.AppendLine("# The names below were bootstrapped from your modpack's BepInEx LogOutput.log; replace them");
            sb.AppendLine("# with GUIDs over time for stricter matching.");
            sb.AppendLine();
            sb.AppendLine("required_mods:");
            sb.AppendLine("  - com.taeguk.valheim.serverguard.client    # the ServerGuard companion plugin");
            sb.AppendLine();
            sb.AppendLine("allowed_mods:");
            // Bootstrapped from the user's client BepInEx LogOutput.log
            foreach (var name in new[] {
                "Armoire",
                "AzuAntiCheat",
                "FastLink",
                "Recycle_N_Reclaim",
                "BalrondShipyard",
                "ComfyQuickSlots",
                "Trader Overhaul",
                "Haldor Bounties",
                "Jotunn",
                "Offline Companions",
                "Newtonsoft.Json Detector",
                "YamlDotNet Detector",
                "Wandering Companions",
                "Better Networking",
                "SimpleMarket",
                "Quick Stack - Store - Sort - Trash - Restock",
                "PlanBuild",
                "ImpactfulSkills",
                "SlayerSkills",
                "DiscordConnectorClient",
                "Creature Level & Loot Control",
                "Groups",
                "Player Activity",
                "Protective Wards",
                "ValkyrieDeathMessages",
                "WackysDatabase",
                "More_World_Locations_AIO",
                "Zen.ModLib",
                "ZenBossStone",
            })
            {
                // Quote names that may contain reserved YAML characters
                var safe = name.IndexOfAny(new[] { ':', '|', '#', '&', '*', '!', '%', '@', '`' }) >= 0
                    ? "\"" + name.Replace("\"", "\\\"") + "\""
                    : name;
                sb.AppendLine($"  - {safe}");
            }
            sb.AppendLine();
            sb.AppendLine("banned_mods: []");
            sb.AppendLine();
            File.WriteAllText(AllowedModsYaml, sb.ToString());
        }

        if (!File.Exists(RegistrationsYaml))
        {
            var doc = new RegistrationsDoc();
            File.WriteAllText(RegistrationsYaml, _yamlOut.Serialize(doc));
        }

        if (!File.Exists(ViolationsYaml))
        {
            var doc = new ViolationsDoc();
            File.WriteAllText(ViolationsYaml, _yamlOut.Serialize(doc));
        }

        if (!File.Exists(MetricsYaml))
        {
            var doc = new DetectionMetrics();
            var sb = new StringBuilder();
            sb.AppendLine("# ServerGuard Detection Metrics (auto-updated)");
            sb.AppendLine(_yamlOut.Serialize(doc));
            File.WriteAllText(MetricsYaml, sb.ToString());
        }

    }

    private static void TryRenameLegacy(string from, string to)
    {
        try
        {
            if (!File.Exists(from)) return;
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
            LogS?.LogWarning($"[ServerGuard] Renamed legacy config '{Path.GetFileName(from)}' -> '{Path.GetFileName(to)}'. The new client-attestation flow uses allowed_mods.yaml.");
        }
        catch (Exception ex)
        {
            LogS?.LogWarning($"[ServerGuard] Could not rename legacy file '{from}': {ex.Message}");
        }
    }


    // -------------- YAML Load / Save --------------
    private void LoadSettings()
    {
        try
        {
            _settings = _yamlIn.Deserialize<Settings>(File.ReadAllText(SettingsYaml)) ?? new Settings();

            // Self-heal: if HMAC is required but no secret is configured, mint one and persist
            // it back so subsequent boots and the operator's eyes see the same value.
            if (_settings.RequireHmac && string.IsNullOrWhiteSpace(_settings.SharedSecret))
            {
                _settings.SharedSecret = GenerateSharedSecret();
                try
                {
                    PersistSharedSecret(_settings.SharedSecret);
                    LogS.LogWarning("[ServerGuard] sharedSecret was empty - generated a new one and wrote it back to settings.yaml. Copy this value into every client's client.yaml:");
                    LogS.LogWarning($"[ServerGuard] sharedSecret: {_settings.SharedSecret}");
                }
                catch (Exception persistEx)
                {
                    LogS.LogError($"[ServerGuard] Failed to persist generated sharedSecret: {persistEx.Message}. Generated value (use this in client.yaml): {_settings.SharedSecret}");
                }
            }

            LogS.LogInfo("[ServerGuard] settings.yaml loaded");
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Failed to load settings.yaml: {ex.Message}");
            _settings = new Settings();
        }
    }

    private void LoadAdmins()
    {
        try
        {
            var text = File.ReadAllText(AdminsYaml);
            var doc = _yamlIn.Deserialize<AdminsDoc>(text) ?? new AdminsDoc();
            _admins = new HashSet<string>((doc.admins ?? new List<string>()).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
            LogS.LogInfo($"[ServerGuard] admins.yaml loaded ({_admins.Count} admins)");
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Failed to load admins.yaml: {ex.Message}");
            _admins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void LoadAllowedMods()
    {
        try
        {
            var text = File.ReadAllText(AllowedModsYaml);
            var doc = _yamlIn.Deserialize<AllowedModsDoc>(text) ?? new AllowedModsDoc();
            _requiredMods = ParseAllowedList(doc.required_mods);
            _allowedMods  = ParseAllowedList(doc.allowed_mods);
            _bannedMods   = ParseAllowedList(doc.banned_mods);
            LogS.LogInfo($"[ServerGuard] allowed_mods.yaml loaded (required={_requiredMods.Count}, allowed={_allowedMods.Count}, banned={_bannedMods.Count})");
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Failed to load allowed_mods.yaml: {ex.Message}");
            _requiredMods = new List<AllowedModEntry>();
            _allowedMods  = new List<AllowedModEntry>();
            _bannedMods   = new List<AllowedModEntry>();
        }
    }

    private static List<AllowedModEntry> ParseAllowedList(List<string> raw)
    {
        var result = new List<AllowedModEntry>();
        if (raw == null) return result;
        foreach (var line in raw)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split('|');
            var key = parts[0].Trim();
            var sha = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : null;
            if (string.IsNullOrEmpty(key)) continue;
            result.Add(new AllowedModEntry { Key = key.ToLowerInvariant(), Sha256 = sha });
        }
        return result;
    }

    private void LoadRegistrations()
	{
		try
		{
			var text = File.ReadAllText(RegistrationsYaml);
			var doc = _yamlIn.Deserialize<RegistrationsDoc>(text);
			if (doc?.registrations != null && doc.registrations.Count > 0)
			{
				_registrations = doc.registrations;
			}
			else
			{
				var legacy = _yamlIn.Deserialize<Dictionary<string, Dictionary<string, string>>>(text);
				if (legacy != null && legacy.TryGetValue("registrations", out var mapV1) && mapV1 != null)
				{
					var v2 = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
					foreach (var kv in mapV1)
					{
						if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
							v2[kv.Key] = new List<string> { kv.Value.Trim() };
					}
					_registrations = v2;
					SaveRegistrations();
				}
				else
				{
					_registrations = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
				}
			}
			LogS.LogInfo($"[ServerGuard] registrations.yaml loaded ({_registrations.Count} SteamIDs)");
		}
		catch (Exception ex)
		{
			LogS.LogError($"[ServerGuard] Failed to load registrations.yaml: {ex.Message}");
			_registrations = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		}
	}

    private void LoadViolations()
    {
        try
        {
            var text = File.ReadAllText(ViolationsYaml);
            var doc = _yamlIn.Deserialize<ViolationsDoc>(text) ?? new ViolationsDoc();
            _violations = doc.violations ?? new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            LogS.LogInfo($"[ServerGuard] violations.yaml loaded ({_violations.Count} players)");
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Failed to load violations.yaml: {ex.Message}");
            _violations = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void LoadMetrics()
    {
        try
        {
            var text = File.ReadAllText(MetricsYaml);
            _metrics = _yamlIn.Deserialize<DetectionMetrics>(text) ?? new DetectionMetrics();
            _metrics.last_updated = DateTime.UtcNow;
            LogS.LogInfo($"[ServerGuard] metrics.yaml loaded (Checked: {_metrics.total_players_checked}, Detected: {_metrics.total_mods_detected})");
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] Failed to load metrics.yaml: {ex.Message}");
            _metrics = new DetectionMetrics();
        }
    }

    private void SaveRegistrations()
	{
		var doc = new RegistrationsDoc { registrations = _registrations };
		File.WriteAllText(RegistrationsYaml, _yamlOut.Serialize(doc));
	}

    private void SaveViolations()
    {
        var doc = new ViolationsDoc { violations = _violations };
        File.WriteAllText(ViolationsYaml, _yamlOut.Serialize(doc));
    }

    private void SaveMetrics()
    {
        try
        {
            if (!_settings.EnableMetrics) return;
            _metrics.last_updated = DateTime.UtcNow;
            var doc = _metrics;
            File.WriteAllText(MetricsYaml, _yamlOut.Serialize(doc));
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] Failed to save metrics.yaml: {ex.Message}");
        }
    }

    private void SaveAll()
    {
        SaveRegistrations();
        SaveViolations();
        SaveMetrics();
    }

    // -------------- Helpers --------------
    private static string GetPeerPlatformId(object znetPeer)
	{
		try
		{
			var fPlat = znetPeer.GetType().GetField("m_platformUserID",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (fPlat != null)
			{
				var val = fPlat.GetValue(znetPeer);
				if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
			}

			var mGetPlat = znetPeer.GetType().GetMethod("GetPlatformUserID",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (mGetPlat != null)
			{
				var val = mGetPlat.Invoke(znetPeer, null);
				if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
			}

			var fSock = znetPeer.GetType().GetField("m_socket", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			var socket = fSock?.GetValue(znetPeer);
			if (socket != null)
			{
				var fPeerId = socket.GetType().GetField("m_peerID",
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				if (fPeerId != null)
				{
					var val = fPeerId.GetValue(socket);
					if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
				}

				var mPeerId = socket.GetType().GetMethod("GetPeerID",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (mPeerId != null)
				{
					var val = mPeerId.Invoke(socket, null);
					if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
				}

				var mGetSteamId = socket.GetType().GetMethod("GetSteamID",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (mGetSteamId != null)
				{
					var val = mGetSteamId.Invoke(socket, null);
					if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
				}

				var mGetSteamId64 = socket.GetType().GetMethod("GetSteamID64",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (mGetSteamId64 != null)
				{
					var val = mGetSteamId64.Invoke(socket, null);
					if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
				}

				var pSteamId = socket.GetType().GetProperty("SteamID",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (pSteamId != null)
				{
					var val = pSteamId.GetValue(socket, null);
					if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
				}

				var fSteamStruct = socket.GetType().GetField("m_SteamID",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (fSteamStruct != null)
				{
					var val = fSteamStruct.GetValue(socket);
					if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
				}

				var fSteamStruct2 = socket.GetType().GetField("m_steamID",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (fSteamStruct2 != null)
				{
					var val = fSteamStruct2.GetValue(socket);
					if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
				}

				var mHost = socket.GetType().GetMethod("GetHostName",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (mHost != null)
				{
					var host = Convert.ToString(mHost.Invoke(socket, null));
					var fromHost = ExtractSteamIdFromString(host);
					if (IsValidSteamId(fromHost)) return fromHost;
				}

				var any = ExtractSteamIdFromString(socket.ToString());
				if (IsValidSteamId(any)) return any;
			}

			var sPeer = ExtractSteamIdFromString(znetPeer.ToString());
			if (IsValidSteamId(sPeer)) return sPeer;
		}
		catch
		{
		}

		return "UNKNOWN";
	}

	private static bool TryNormalizeSteamId(object raw, out string normalized)
	{
		normalized = null;
		if (raw == null) return false;

		switch (raw)
		{
			case ulong u when u != 0UL:
				normalized = u.ToString(); return true;
			case long l when l > 0L:
				normalized = l.ToString(); return true;
			case string s when IsValidSteamId(s):
				normalized = s; return true;
		}

		var t = raw.GetType();
		var fInner = t.GetField("m_SteamID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (fInner != null)
		{
			var inner = fInner.GetValue(raw);
			if (inner != null && ulong.TryParse(inner.ToString(), out var u2) && u2 != 0UL)
			{
				normalized = u2.ToString(); return true;
			}
		}

		var pVal = t.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (pVal != null)
		{
			var v = pVal.GetValue(raw, null);
			if (v != null && ulong.TryParse(v.ToString(), out var u3) && u3 != 0UL)
			{
				normalized = u3.ToString(); return true;
			}
		}

		var fromString = ExtractSteamIdFromString(raw.ToString());
		if (IsValidSteamId(fromString)) { normalized = fromString; return true; }

		return false;
	}

	private static string ExtractSteamIdFromString(string s)
	{
		if (string.IsNullOrEmpty(s)) return null;
		int run = 0, start = -1;
		for (int i = 0; i < s.Length; i++)
		{
			if (char.IsDigit(s[i]))
			{
				if (run == 0) start = i;
				run++;
				if (run == 17)
					return s.Substring(start, 17);
			}
			else
			{
				run = 0; start = -1;
			}
		}
		return null;
	}

	private static bool IsValidSteamId(string candidate)
	{
		if (string.IsNullOrWhiteSpace(candidate)) return false;
		if (candidate.Length != 17) return false;
		for (int i = 0; i < candidate.Length; i++)
			if (candidate[i] < '0' || candidate[i] > '9') return false;
		return candidate != "00000000000000000";
	}

    private static string GetPeerPlayerName(object znetPeer)
    {
        var f = znetPeer.GetType().GetField("m_playerName", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return f?.GetValue(znetPeer)?.ToString() ?? "Unknown";
    }

    private static string GetPeerCharacterId(object znetPeer)
    {
        var f = znetPeer.GetType().GetField("m_characterID", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        var v = f?.GetValue(znetPeer);
        return v?.ToString() ?? "CHAR_UNKNOWN";
    }

    private bool IsAdmin(string platformId) => _admins.Contains(platformId);

    // Renders "<CharacterName> (<SteamID>)" for logs and Discord messages.
    //
    // The name is pulled from registrations.yaml. If the SteamID has multiple
    // characters registered, all of them are listed comma-separated. If the
    // SteamID has never logged in before (no entry in the dict), "NewPlayer"
    // is shown - subsequent connections, once Patch_RPC_PeerInfo has recorded
    // their character, will display the real name.
    private string FormatPlayer(string steamId)
    {
        if (string.IsNullOrWhiteSpace(steamId)) return "NewPlayer (UNKNOWN)";

        if (_registrations != null
            && _registrations.TryGetValue(steamId, out var names)
            && names != null
            && names.Count > 0)
        {
            return $"{string.Join(", ", names)} ({steamId})";
        }

        return $"NewPlayer ({steamId})";
    }

    private void RecordMetricDetection(string modToken, string detectionMethod)
    {
        if (!_settings.EnableMetrics || _metrics == null) return;

        _metrics.total_mods_detected++;
        
        if (detectionMethod == "RPC") _metrics.phase1_rpc_detections++;
        else if (detectionMethod == "Assembly") _metrics.phase2_assembly_detections++;
        else if (detectionMethod == "Version") _metrics.version_keyword_detections++;

        if (!_metrics.top_detected_mods.ContainsKey(modToken))
            _metrics.top_detected_mods[modToken] = 0;
        _metrics.top_detected_mods[modToken]++;

        SaveMetrics();
    }

    private bool RuleCounts(string rule)
    {
        var cav = _settings.CountAsViolation;
        if (cav == null) return true;
        return rule switch
        {
            RULE_COMPANION_MISSING    => cav.CompanionMissing,
            RULE_HMAC_INVALID         => cav.HmacInvalid,
            RULE_CHALLENGE_MISMATCH   => cav.ChallengeMismatch,
            RULE_REQUIRED_MOD_MISSING => cav.RequiredModMissing,
            RULE_DISALLOWED_MOD       => cav.DisallowedMod,
            RULE_BANNED_MOD           => cav.BannedMod,
            RULE_CHAR_NAME_LIMIT      => cav.CharacterNameLimitExceeded,
            _                         => true
        };
    }

    private void AddViolation(string platformId, string rule)
    {
        var who = FormatPlayer(platformId);

        if (!RuleCounts(rule))
        {
            LogS.LogWarning($"[ServerGuard] {who} triggered {rule} (log-only; countAsViolation is false for this rule).");
            _ = SendAdmin($":notepad_spiral: {who} triggered **{rule}** (log-only).");
            return;
        }

        if (!_violations.TryGetValue(platformId, out var map))
        {
            map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _violations[platformId] = map;
        }
        map.TryGetValue(rule, out var c);
        map[rule] = c + 1;
        SaveViolations();

        if (_settings.EnableMetrics)
        {
            _metrics.violations_issued++;
            SaveMetrics();
        }

        LogS.LogWarning($"[ServerGuard] {who} violated {rule}. Count={map[rule]}/{_settings.ViolationThreshold}");
		_ = SendAdmin($":warning: Violation by {who} — **{rule}** ({map[rule]}/{_settings.ViolationThreshold})");

        if (_settings.Enforce && map[rule] >= _settings.ViolationThreshold)
        {
            TryBan(platformId, _settings.BanReason);
            if (_settings.EnableMetrics)
            {
                _metrics.players_banned++;
                SaveMetrics();
            }
			_ = SendAdmin($":no_entry: Auto-banned {who}. Reason: {_settings.BanReason}");
        }
    }

    private void TryKick(object znetPeer, string reason)
    {
        try
        {
            if (znetPeer is not ZNetPeer peer || peer == null) return;
            if (ZNet.instance == null) return;

            var pid = GetPeerPlatformId(peer);
            var who = FormatPlayer(pid);

            // Tell the client *why* it's being disconnected. Best-effort - even if this
            // fails (e.g. socket already torn down), the Disconnect call below still runs.
            try
            {
                peer.m_rpc?.Invoke("Error", 3); // ZNet.ConnectionStatus.ErrorBanned-style code
            }
            catch { }

            // The reflection-based Kick(ZNetPeer)/Kick(string) overloads we used previously
            // resolved to a method that *queued* a soft kick but did not actually disconnect
            // the socket synchronously, so the handshake completed and the player got past
            // the kick. ZNet.Disconnect(peer) is the public method that tears the connection
            // down for real (used by Valheim's own console "kick" command path).
            try
            {
                ZNet.instance.Disconnect(peer);
                LogS.LogWarning($"[ServerGuard] Disconnected {who}. Reason: {reason}");
                _ = SendAdmin($":boot: Disconnected {who}. Reason: {reason}");
                return;
            }
            catch (Exception primaryEx)
            {
                LogS.LogWarning($"[ServerGuard] ZNet.Disconnect threw ({primaryEx.Message}); falling back to reflection.");
            }

            // Fallback path - older Valheim builds.
            var znet = typeof(ZNet).GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            if (znet == null) return;

            var disconnectMethod = znet.GetType().GetMethod("Disconnect", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(ZNetPeer) }, null);
            if (disconnectMethod != null)
            {
                disconnectMethod.Invoke(znet, new object[] { peer });
                LogS.LogWarning($"[ServerGuard] Disconnected {who} (reflection). Reason: {reason}");
                _ = SendAdmin($":boot: Disconnected {who}. Reason: {reason}");
                return;
            }

            var internalKick = znet.GetType().GetMethod("InternalKick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(ZNetPeer) }, null);
            if (internalKick != null)
            {
                internalKick.Invoke(znet, new object[] { peer });
                LogS.LogWarning($"[ServerGuard] InternalKick'd {who}. Reason: {reason}");
                _ = SendAdmin($":boot: Kicked {who}. Reason: {reason}");
            }
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Kick failed: {ex}");
        }
    }

    private void TryBan(string platformId, string reason)
    {
        try
        {
            var znet = typeof(ZNet).GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            if (znet == null) return;

            var banId = znet.GetType().GetMethod("Ban", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(string) }, null);
            if (banId != null)
            {
                banId.Invoke(znet, new object[] { platformId });
                var who = FormatPlayer(platformId);
                LogS.LogWarning($"[ServerGuard] Auto-banned {who}. Reason: {reason}");
				_ = SendAdmin($":no_entry: Auto-banned {who}. Reason: {reason}");
            }
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Ban failed: {ex}");
        }
    }

    // Sends a cheat-item removal list to the peer's companion plugin. The client
    // removes the listed prefab names from the player's inventory after spawn.
    private void SendCheatItemRemovalIfEnabled(ZNetPeer peer)
    {
        try
        {
            if (!_settings.EnableCheatItemRemoval) return;
            var items = _settings.CheatItems;
            if (items == null || items.Count == 0) return;
            if (peer?.m_rpc == null) return;

            var payload = string.Join(",", items.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (string.IsNullOrEmpty(payload)) return;

            peer.m_rpc.Invoke("ServerGuard_RemoveItems", payload);
            LogS.LogInfo($"[ServerGuard] Sent cheat-item removal list to {FormatPlayer(GetPeerPlatformId(peer))}: [{payload}]");
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] SendCheatItemRemovalIfEnabled failed: {ex.Message}");
        }
    }

    // -------------- Raid Event Handlers --------------

    internal void OnRaidStarted(string name, Vector3 pos)
    {
        // Deduplicate: ignore repeated SetRandomEvent calls for the same event
        if (string.Equals(name, _currentRaidName, StringComparison.Ordinal)) return;

        // If a different event was running, close it out first
        if (_currentRaidName != null) OnRaidEnded();

        _currentRaidName = name;
        _currentRaidPos  = pos;
        _raidPaused      = false;

        var displayName = GetRaidDisplayName(name);
        var coord = $"X:{pos.x:F0}, Z:{pos.z:F0}";
        LogS.LogInfo($"[ServerGuard] RAID START | {displayName} ({name}) at ({coord})");
        _ = SendPublic($":crossed_swords: **{displayName}** has started! Location: `{coord}`");

        if (_raidMonitorCoroutine != null) StopCoroutine(_raidMonitorCoroutine);
        _raidMonitorCoroutine = StartCoroutine(MonitorRaidEvent());
    }

    internal void OnRaidEnded()
    {
        if (_currentRaidName == null) return;

        var displayName = GetRaidDisplayName(_currentRaidName);
        var coord = $"X:{_currentRaidPos.x:F0}, Z:{_currentRaidPos.z:F0}";
        LogS.LogInfo($"[ServerGuard] RAID END | {displayName} ({_currentRaidName})");
        _ = SendPublic($":white_check_mark: **{displayName}** is over! Location was: `{coord}`");

        _currentRaidName = null;
        _raidPaused      = false;

        if (_raidMonitorCoroutine != null)
        {
            StopCoroutine(_raidMonitorCoroutine);
            _raidMonitorCoroutine = null;
        }
    }

    private IEnumerator MonitorRaidEvent()
    {
        while (_currentRaidName != null)
        {
            yield return new WaitForSeconds(5f);

            if (_currentRaidName == null || RandEventSystem.instance == null) break;

            // GetCurrentRandomEvent != null but GetActiveEvent == null means no players
            // are in the event area — the timer is frozen (paused state).
            var current = RandEventSystem.instance.GetCurrentRandomEvent();
            var active  = RandEventSystem.instance.GetActiveEvent();
            bool isPaused = current != null && active == null;

            var displayName = GetRaidDisplayName(_currentRaidName);

            if (isPaused && !_raidPaused)
            {
                _raidPaused = true;
                var coord = $"X:{_currentRaidPos.x:F0}, Z:{_currentRaidPos.z:F0}";
                LogS.LogInfo($"[ServerGuard] RAID PAUSED | {displayName}");
                _ = SendPublic(
                    $":pause_button: **{displayName}** is paused — no players in the event area. Location: `{coord}`");
            }
            else if (!isPaused && _raidPaused)
            {
                _raidPaused = false;
                LogS.LogInfo($"[ServerGuard] RAID RESUMED | {displayName}");
                _ = SendPublic($":arrow_forward: **{displayName}** has resumed.");
            }
        }
        _raidMonitorCoroutine = null;
    }

    // -------------- Harmony Patches --------------
    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    public static class Patch_OnNewConnection
    {
        public static void Postfix(ZNetPeer peer)
        {
            try
            {
                if (peer == null || peer.m_rpc == null) return;
                if (!ZNet.instance || !ZNet.instance.IsServer()) return;

                var pid   = Plugin.GetPeerPlatformId(peer);
                Plugin.LogS.LogInfo($"[ServerGuard] Incoming connection: {Plugin.Instance.FormatPlayer(pid)}");

                // Death-report receiver. Registered for every peer (including admins —
                // the handler itself filters admin deaths out).
                peer.m_rpc.Register<string>("ServerGuard_PlayerDeath", (rpc, payload) =>
                {
                    Plugin.Instance.OnPlayerDeathReceived(peer, payload);
                });

                // Chat receiver (shouts/whispers). Sent by the companion plugin
                // because chat packets can't be observed server-side anymore.
                peer.m_rpc.Register<string>("ServerGuard_Chat", (rpc, payload) =>
                {
                    Plugin.Instance.OnChatReceived(peer, payload);
                });

                if (Plugin.Instance.IsAdmin(pid))
                {
                    Plugin.LogS.LogInfo($"[ServerGuard] {Plugin.Instance.FormatPlayer(pid)} is admin - skipping attestation.");
                    if (Plugin.Instance._settings.EnableMetrics)
                    {
                        Plugin.Instance._metrics.admin_bypasses++;
                        Plugin.Instance.SaveMetrics();
                    }
                    return;
                }

                if (Plugin.Instance._settings.EnableMetrics)
                {
                    Plugin.Instance._metrics.total_players_checked++;
                    Plugin.Instance.SaveMetrics();
                }

                // 1. Register the manifest receiver on this peer's ZRpc so we get a
                //    callback when the client replies. Idempotent if it somehow runs twice.
                peer.m_rpc.Register<string>("ServerGuard_Manifest", (rpc, json) =>
                {
                    Plugin.Instance.OnManifestReceived(peer, json);
                });

                // 2. Generate a fresh challenge bound to this peer + session.
                var challenge = Plugin.Instance.GenerateChallenge();
                Plugin.Instance.RegisterPending(peer, pid, challenge);

                // 3. Ask the client to attest. Companion plugin replies via ServerGuard_Manifest.
                peer.m_rpc.Invoke("ServerGuard_RequestManifest", challenge);

                // 4. Schedule a kick if the client never replies (= vanilla / wrong-version client).
                Plugin.Instance.StartCoroutine(Plugin.Instance.AttestationTimeoutCoroutine(peer, pid));
            }
            catch (Exception ex)
            {
                Plugin.LogS.LogError($"[ServerGuard] OnNewConnection error: {ex}");
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
	public static class Patch_RPC_PeerInfo
	{
		public static void Postfix(ZNet __instance, ZRpc rpc)
		{
			try
			{
				if (!ZNet.instance || !ZNet.instance.IsServer()) return;

				var peer = ResolvePeerFromRpc(__instance, rpc);
				if (peer == null) return;

				var steamId  = Plugin.GetPeerPlatformId(peer);
				var charName = Plugin.GetPeerPlayerName(peer)?.Trim();

				if (!IsValidSteamId(steamId)) { Plugin.LogS.LogWarning("[ServerGuard] PeerInfo without valid SteamID; deferring."); return; }
				if (string.IsNullOrWhiteSpace(charName) || string.Equals(charName, "Unknown", StringComparison.OrdinalIgnoreCase)) return;

				if (Plugin.Instance.IsAdmin(steamId))
				{
					Plugin.LogS.LogInfo($"[ServerGuard] ADMIN LOGIN | {charName} ({steamId})");
					_ = Plugin.Instance.SendAdmin($":shield: Admin **{charName}** (`{steamId}`) logged in.");
					return;
				}

				if (!Plugin.Instance._registrations.TryGetValue(steamId, out var names) || names == null)
				{
					names = new List<string>();
					Plugin.Instance._registrations[steamId] = names;
				}

				if (names.Any(n => string.Equals(n, charName, StringComparison.Ordinal)))
				{
					Plugin.LogS.LogInfo($"[ServerGuard] PLAYER LOGIN | {charName} ({steamId})");
					_ = Plugin.Instance.SendPublic($":video_game: **{charName}** has joined the server!");
					Plugin.Instance.SendCheatItemRemovalIfEnabled(peer);
					return;
				}

				int limit = Math.Max(1, Plugin.Instance._settings.CharacterLimit);
				if (names.Count < limit)
				{
					names.Add(charName);
					Plugin.Instance.SaveRegistrations();
					Plugin.LogS.LogInfo($"[ServerGuard] Registered character #{names.Count}/{limit} for {Plugin.Instance.FormatPlayer(steamId)} -> '{charName}'");
					Plugin.LogS.LogInfo($"[ServerGuard] PLAYER LOGIN | {charName} ({steamId})");
					_ = Plugin.Instance.SendPublic($":video_game: **{charName}** has joined the server!");
					Plugin.Instance.SendCheatItemRemovalIfEnabled(peer);
				}
				else
				{
					Plugin.Instance.AddViolation(steamId, RULE_CHAR_NAME_LIMIT);
					if (Plugin.Instance._settings.Enforce)
					{
						Plugin.Instance.TryKick(peer, $"{Plugin.Instance._settings.KickMessage} (Character limit {limit} reached: {string.Join(", ", names)})");
					}
					else
					{
						Plugin.LogS.LogWarning($"[ServerGuard] {Plugin.Instance.FormatPlayer(steamId)} exceeded character limit ({limit}). Tried '{charName}'. Allowed: {string.Join(", ", names)}");
					}
				}
			}
			catch (Exception ex)
			{
				Plugin.LogS.LogError($"[ServerGuard] RPC_PeerInfo error: {ex}");
			}
		}
	}

    // -------------- Chat Log (shouts only) --------------

    // Handles the ServerGuard_Chat RPC sent by the companion plugin when the
    // local player shouts. Payload: "<type>|<text>" (Talker.Type.Shout = 2).
    // Names/SteamIDs come from the server-side peer, not the client payload.
    public void OnChatReceived(ZNetPeer peer, string payload)
    {
        try
        {
            if (peer == null || string.IsNullOrEmpty(payload)) return;

            var sep = payload.IndexOf('|');
            if (sep <= 0) return;
            if (!int.TryParse(payload.Substring(0, sep), out var type)) return;
            if (type != 2) return; // shouts only (Talker.Type.Shout)

            var text = payload.Substring(sep + 1).Trim();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text.Length > 256) text = text.Substring(0, 256);

            var steamId  = GetPeerPlatformId(peer);
            var charName = GetPeerPlayerName(peer)?.Trim();
            if (string.IsNullOrWhiteSpace(charName) || charName == "Unknown")
                charName = FormatPlayer(steamId);

            LogS.LogInfo($"[ServerGuard] SHOUT | {charName} ({steamId}): {text}");
            _ = SendPublic($":mega: **{charName}** shouted: {text}");
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnChatReceived error: {ex}");
        }
    }

    // -------------- Player Death Log --------------

    // Handles ServerGuard_PlayerDeath RPC. The companion plugin sends a payload
    // describing the local player's death; the server formats and posts to the
    // public Discord webhook. Admin deaths are skipped entirely.
    //
    // Payload format (pipe-separated, invariant-culture floats):
    //   posX|posY|posZ|attackerKind|attackerLabel|causeHint
    //
    //   attackerKind  : "player" | "creature" | "self" | "environment"
    //   attackerLabel : character name (player) | mob hover name (creature) | "" (env)
    //   causeHint     : dominant damage type, e.g. "Fire", "Frost", "Blunt", "Slash"
    public void OnPlayerDeathReceived(ZNetPeer peer, string payload)
    {
        try
        {
            if (peer == null) return;
            if (_settings == null || !_settings.EnableDeathLog) return;
            if (string.IsNullOrWhiteSpace(payload)) return;

            var victimSteamId = GetPeerPlatformId(peer);
            var charName      = GetPeerPlayerName(peer)?.Trim();
            if (string.IsNullOrWhiteSpace(charName) || charName == "Unknown")
                charName = FormatPlayer(victimSteamId);

            // Admin deaths are not logged.
            if (IsAdmin(victimSteamId))
            {
                LogS.LogInfo($"[ServerGuard] DEATH (admin, suppressed) | {charName} ({victimSteamId})");
                return;
            }

            var parts = payload.Split('|');
            if (parts.Length < 6) return;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out var px)) px = 0f;
            if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out var py)) py = 0f;
            if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out var pz)) pz = 0f;
            var attackerKind  = (parts[3] ?? "").Trim().ToLowerInvariant();
            var attackerLabel = (parts[4] ?? "").Trim();
            var causeHint     = (parts[5] ?? "").Trim();

            // Bound any client-supplied strings so a malicious client can't flood Discord.
            if (attackerLabel.Length > 48) attackerLabel = attackerLabel.Substring(0, 48);
            if (causeHint.Length     > 24) causeHint     = causeHint.Substring(0, 24);

            string killedBy;
            switch (attackerKind)
            {
                case "player":
                {
                    // Try to map character name -> SteamID via registrations.yaml.
                    var attackerSteamId = LookupSteamIdByCharName(attackerLabel);
                    killedBy = string.IsNullOrEmpty(attackerSteamId)
                        ? $"killed by **{attackerLabel}** (another player)"
                        : $"killed by **{attackerLabel}** ({attackerSteamId})";
                    break;
                }
                case "creature":
                    killedBy = string.IsNullOrEmpty(attackerLabel)
                        ? "killed by a creature"
                        : $"killed by a **{attackerLabel}**";
                    break;
                case "self":
                    killedBy = "took their own life";
                    break;
                default: // environment
                    killedBy = HumanizeDeathCause(causeHint);
                    break;
            }

            // [x, z] is the in-game world coordinate familiar to admins and players.
            var line = $":skull: **{charName}** died at `[{px:F0}, {pz:F0}]` — {killedBy}";
            LogS.LogInfo($"[ServerGuard] DEATH | {charName} ({victimSteamId}) at [{px:F0}, {pz:F0}] — {killedBy.Replace("**", "")}");
            _ = SendPublic(line);
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnPlayerDeathReceived error: {ex}");
        }
    }

    // Reverse lookup against registrations.yaml: given a character name, find the
    // SteamID that registered it. Returns "" if not found / not registered.
    private string LookupSteamIdByCharName(string charName)
    {
        if (string.IsNullOrWhiteSpace(charName)) return "";
        if (_registrations == null) return "";
        foreach (var kv in _registrations)
        {
            if (kv.Value == null) continue;
            foreach (var n in kv.Value)
            {
                if (string.Equals(n, charName, StringComparison.OrdinalIgnoreCase))
                    return kv.Key;
            }
        }
        return "";
    }

    // Turns a HitData damage-type hint into a human-readable cause for environmental
    // / no-attacker deaths. Conservative defaults - if we don't recognise the hint,
    // we say "died" without speculating.
    private string HumanizeDeathCause(string causeHint)
    {
        if (string.IsNullOrWhiteSpace(causeHint)) return "died";
        switch (causeHint.ToLowerInvariant())
        {
            case "fire":      return "burned to death";
            case "frost":     return "froze to death";
            case "poison":    return "succumbed to poison";
            case "spirit":    return "drowned or fell to a spirit";
            case "lightning": return "struck by lightning";
            case "blunt":     return "fell to their death";
            case "slash":     return "bled out";
            case "pierce":    return "bled out";
            case "chop":      return "died";
            case "pickaxe":   return "died";
            default:          return $"died ({causeHint.ToLowerInvariant()})";
        }
    }

    // -------------- Connection Logging Patches --------------

    /// <summary>
    /// Announces player departures. Fires for both voluntary logouts and kicks.
    /// Peers that never completed login (e.g. failed attestation before PeerInfo)
    /// have no player name yet and are skipped silently.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "Disconnect")]
    public static class Patch_Disconnect
    {
        public static void Prefix(ZNetPeer peer)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (peer == null) return;

                var charName = Plugin.GetPeerPlayerName(peer)?.Trim();
                if (string.IsNullOrWhiteSpace(charName) || charName == "Unknown") return;

                var steamId = Plugin.GetPeerPlatformId(peer);
                Plugin.LogS.LogInfo($"[ServerGuard] PLAYER LOGOUT | {charName} ({steamId})");

                if (Plugin.Instance.IsAdmin(steamId))
                    _ = Plugin.Instance.SendAdmin($":shield: Admin **{charName}** (`{steamId}`) logged out.");
                else
                    _ = Plugin.Instance.SendPublic($":wave: **{charName}** has left the server.");
            }
            catch (Exception ex)
            {
                Plugin.LogS?.LogError($"[ServerGuard] Disconnect patch error: {ex.Message}");
            }
        }
    }

    // -------------- Chat Logging (shouts only) --------------
    //
    // NOTE: chat CANNOT be intercepted server-side on current Valheim builds.
    // Chat.SendText no longer broadcasts to Everybody — the client sends one copy
    // per recipient (per-user text-permission checks). The dedicated server only
    // routes those packets and never handles them. The companion plugin therefore
    // reports outgoing shouts to the server over the "ServerGuard_Chat" ZRpc —
    // see OnChatReceived below, and the registration in Patch_OnNewConnection.

    // -------------- Raid Event Patches --------------

    /// <summary>
    /// Fires when the server sets a new random event (the core private method that all
    /// public entry points funnel into). Logs start with event name and world coordinates.
    /// Uses TargetMethod() because SetRandomEvent is private.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_SetRandomEvent
    {
        static MethodBase TargetMethod()
        {
            try
            {
                var m = typeof(RandEventSystem).GetMethod(
                    "SetRandomEvent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (m == null)
                    Plugin.LogS?.LogWarning("[ServerGuard] RandEventSystem.SetRandomEvent not found — raid start logging unavailable.");
                return m;
            }
            catch (Exception ex)
            {
                Plugin.LogS?.LogWarning($"[ServerGuard] Failed to locate RandEventSystem.SetRandomEvent: {ex.Message}");
                return null;
            }
        }

        // Harmony injects ev and pos by matching parameter names in the original method.
        public static void Postfix(RandomEvent ev, Vector3 pos)
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (ev == null || string.IsNullOrEmpty(ev.m_name)) return;
                Plugin.Instance.OnRaidStarted(ev.m_name, pos);
            }
            catch (Exception ex)
            {
                Plugin.LogS?.LogError($"[ServerGuard] SetRandomEvent patch error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Fires when the server ends the active raid event. Uses Prefix so the event name
    /// stored in Plugin fields is still valid at the time the Discord message is sent.
    /// </summary>
    [HarmonyPatch(typeof(RandEventSystem), "ResetRandomEvent")]
    public static class Patch_ResetRandomEvent
    {
        public static void Prefix()
        {
            try
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                Plugin.Instance.OnRaidEnded();
            }
            catch (Exception ex)
            {
                Plugin.LogS?.LogError($"[ServerGuard] ResetRandomEvent patch error: {ex.Message}");
            }
        }
    }

    private static ZNetPeer ResolvePeerFromRpc(ZNet znet, ZRpc rpc)
    {
        if (znet == null || rpc == null) return null;

        var mZrpc = typeof(ZNet).GetMethod("GetPeer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(ZRpc) }, null);
        if (mZrpc != null)
        {
            return (ZNetPeer)mZrpc.Invoke(znet, new object[] { rpc });
        }

        var getUid = rpc.GetType().GetMethod("GetUID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (getUid != null)
        {
            var uidObj = getUid.Invoke(rpc, null);
            if (uidObj is long uid)
            {
                var mLong = typeof(ZNet).GetMethod("GetPeer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(long) }, null);
                if (mLong != null)
                {
                    return (ZNetPeer)mLong.Invoke(znet, new object[] { uid });
                }
            }
        }

        Plugin.LogS.LogWarning("[ServerGuard] ResolvePeerFromRpc: unable to resolve peer from ZRpc.");
        return null;
    }

    // -------------- Client Attestation --------------

    private string GenerateChallenge()
    {
        var bytes = new byte[24];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string GenerateSharedSecret()
    {
        // 32 bytes -> 256 bits of entropy, base64-encoded for easy copy/paste into client.yaml.
        var bytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    // Updates the sharedSecret line in settings.yaml in-place, preserving comments and
    // surrounding keys. If no line exists, appends one.
    private static void PersistSharedSecret(string value)
    {
        var lines = File.Exists(SettingsYaml)
            ? File.ReadAllLines(SettingsYaml).ToList()
            : new List<string>();

        var rx = new System.Text.RegularExpressions.Regex(@"^\s*sharedSecret\s*:.*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        bool replaced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (rx.IsMatch(lines[i]))
            {
                lines[i] = $"sharedSecret: '{value}'";
                replaced = true;
                break;
            }
        }
        if (!replaced) lines.Add($"sharedSecret: '{value}'");

        File.WriteAllLines(SettingsYaml, lines);
    }

    private void RegisterPending(ZNetPeer peer, string steamId, string challenge)
    {
        lock (_pendingLock)
        {
            _pending[peer.m_uid] = new PendingAttestation
            {
                Challenge = challenge,
                SentAt    = DateTime.UtcNow,
                SteamId   = steamId,
                Peer      = peer
            };
        }
    }

    public IEnumerator AttestationTimeoutCoroutine(ZNetPeer peer, string steamId)
    {
        var seconds = Mathf.Max(1, _settings.CompanionTimeoutSeconds);
        yield return new WaitForSeconds(seconds);

        PendingAttestation pending;
        lock (_pendingLock)
        {
            if (!_pending.TryGetValue(peer.m_uid, out pending) || pending == null) yield break;
            _pending.Remove(peer.m_uid);
        }

        // Pending entry still present means the manifest never arrived in time.
        var label = FormatPlayer(steamId);
        LogS.LogWarning($"[ServerGuard] {label} did not deliver a manifest within {seconds}s. Treating as no-companion.");
        _ = SendAdmin($":hourglass: No manifest from {label} in {seconds}s. Companion plugin missing or unreachable.");

        if (_settings.RequireCompanion)
        {
            AddViolation(steamId, RULE_COMPANION_MISSING);
            if (_settings.Enforce)
            {
                TryKick(peer, $"{_settings.KickMessage} (Missing required companion plugin: ServerGuard.Client)");
            }
        }
    }

    public void OnManifestReceived(ZNetPeer peer, string json)
    {
        string steamId = "UNKNOWN";
        try
        {
            steamId = GetPeerPlatformId(peer);
            var who = FormatPlayer(steamId);

            // Pop the pending attestation; ignore replies that arrive after timeout.
            PendingAttestation pending;
            lock (_pendingLock)
            {
                if (!_pending.TryGetValue(peer.m_uid, out pending) || pending == null)
                {
                    LogS.LogWarning($"[ServerGuard] Manifest from {who} arrived with no pending challenge (timed out or duplicate). Ignoring.");
                    return;
                }
                _pending.Remove(peer.m_uid);
            }

            ModManifest manifest;
            try
            {
                manifest = JsonConvert.DeserializeObject<ModManifest>(json);
            }
            catch (Exception ex)
            {
                LogS.LogWarning($"[ServerGuard] Failed to parse manifest from {who}: {ex.Message}");
                AddViolation(steamId, RULE_HMAC_INVALID);
                if (_settings.Enforce) TryKick(peer, $"{_settings.KickMessage} (Malformed manifest)");
                return;
            }
            if (manifest == null)
            {
                LogS.LogWarning($"[ServerGuard] Empty manifest from {who}.");
                AddViolation(steamId, RULE_HMAC_INVALID);
                if (_settings.Enforce) TryKick(peer, $"{_settings.KickMessage} (Empty manifest)");
                return;
            }

            // 1. Challenge match (defeats cross-peer / cross-session replay).
            if (!ModManifest.ConstantTimeEquals(manifest.Challenge ?? "", pending.Challenge ?? ""))
            {
                LogS.LogWarning($"[ServerGuard] Challenge mismatch from {who}.");
                AddViolation(steamId, RULE_CHALLENGE_MISMATCH);
                if (_settings.Enforce) TryKick(peer, $"{_settings.KickMessage} (Challenge mismatch)");
                return;
            }

            // 2. Timestamp window.
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(nowUnix - manifest.TimestampUtc) > Math.Max(10, _settings.MaxClockSkewSeconds))
            {
                LogS.LogWarning($"[ServerGuard] Timestamp out of window for {who} (client={manifest.TimestampUtc} server={nowUnix}).");
                AddViolation(steamId, RULE_HMAC_INVALID);
                if (_settings.Enforce) TryKick(peer, $"{_settings.KickMessage} (Clock skew exceeds policy)");
                return;
            }

            // 3. HMAC.
            if (_settings.RequireHmac)
            {
                if (string.IsNullOrEmpty(_settings.SharedSecret))
                {
                    LogS.LogError($"[ServerGuard] Cannot validate manifest from {who}: requireHmac=true but sharedSecret is empty on server.");
                    if (_settings.Enforce) TryKick(peer, $"{_settings.KickMessage} (Server misconfiguration: missing sharedSecret)");
                    return;
                }
                var expected = ModManifest.ComputeHmac(manifest.CanonicalForHmac(), _settings.SharedSecret);
                if (!ModManifest.ConstantTimeEquals(expected, manifest.Hmac ?? ""))
                {
                    LogS.LogWarning($"[ServerGuard] HMAC mismatch for {who}. Either bad sharedSecret on client, or tampered manifest.");
                    AddViolation(steamId, RULE_HMAC_INVALID);
                    if (_settings.Enforce) TryKick(peer, $"{_settings.KickMessage} (Invalid signature)");
                    return;
                }
            }

            // 4. Optionally log full manifest for GUID harvesting.
            if (_settings.LogPeerManifest)
            {
                var lines = (manifest.Mods ?? new List<ModManifestEntry>()).Select(m => $"  - {m.Guid}|{m.Name}|{m.Version}|{m.Sha256}");
                LogS.LogInfo($"[ServerGuard] Manifest from {who} ({manifest.Mods?.Count ?? 0} mods):\n" + string.Join("\n", lines));
            }

            // 5. Validate against allowed_mods.yaml.
            var verdict = ValidateAgainstPolicy(manifest);
            if (!verdict.Allowed)
            {
                LogS.LogWarning($"[ServerGuard] {who} REJECTED: {verdict.Rule} - {verdict.Reason}");
                _ = SendAdmin($":no_entry_sign: Rejected {who} - {verdict.Rule}: {verdict.Reason}");
                AddViolation(steamId, verdict.Rule);
                if (_settings.Enforce) TryKick(peer, $"{_settings.KickMessage} ({verdict.Reason})");
                return;
            }

            LogS.LogInfo($"[ServerGuard] {who} attested OK ({manifest.Mods?.Count ?? 0} mods).");
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnManifestReceived error for {FormatPlayer(steamId)}: {ex}");
        }
    }

    private struct PolicyVerdict
    {
        public bool Allowed;
        public string Rule;
        public string Reason;
    }

    private PolicyVerdict ValidateAgainstPolicy(ModManifest manifest)
    {
        var mods = manifest.Mods ?? new List<ModManifestEntry>();

        // Index manifest by lowercase guid AND name for matching.
        var byKey = new Dictionary<string, ModManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
        {
            if (!string.IsNullOrEmpty(m?.Guid)) byKey[m.Guid.ToLowerInvariant()] = m;
            if (!string.IsNullOrEmpty(m?.Name)) byKey[m.Name.ToLowerInvariant()] = m;
        }

        // 1. banned_mods - any presence is fatal.
        foreach (var b in _bannedMods)
        {
            if (byKey.TryGetValue(b.Key, out var hit))
            {
                return new PolicyVerdict { Allowed = false, Rule = RULE_BANNED_MOD, Reason = $"Disallowed mod present: {hit.Name ?? hit.Guid}" };
            }
        }

        // 2. required_mods - every entry must be present (with hash match if pinned).
        foreach (var r in _requiredMods)
        {
            if (!byKey.TryGetValue(r.Key, out var hit))
            {
                return new PolicyVerdict { Allowed = false, Rule = RULE_REQUIRED_MOD_MISSING, Reason = $"Required mod missing: {r.Key}" };
            }
            if (!string.IsNullOrEmpty(r.Sha256) && !string.Equals(r.Sha256, hit.Sha256 ?? "", StringComparison.OrdinalIgnoreCase))
            {
                return new PolicyVerdict { Allowed = false, Rule = RULE_DISALLOWED_MOD, Reason = $"Required mod hash mismatch: {r.Key}" };
            }
        }

        // 3. If allowUnlisted=false, every manifest mod must be in (required ∪ allowed).
        if (!_settings.AllowUnlisted)
        {
            var allow = new Dictionary<string, AllowedModEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _requiredMods) allow[e.Key] = e;
            foreach (var e in _allowedMods)  allow[e.Key] = e;

            foreach (var m in mods)
            {
                AllowedModEntry rule = null;
                if (!string.IsNullOrEmpty(m.Guid) && allow.TryGetValue(m.Guid.ToLowerInvariant(), out var byGuid)) rule = byGuid;
                else if (!string.IsNullOrEmpty(m.Name) && allow.TryGetValue(m.Name.ToLowerInvariant(), out var byName)) rule = byName;

                if (rule == null)
                {
                    var label = !string.IsNullOrEmpty(m.Guid) ? m.Guid : m.Name;
                    return new PolicyVerdict { Allowed = false, Rule = RULE_DISALLOWED_MOD, Reason = $"Unapproved mod: {label}" };
                }
                if (!string.IsNullOrEmpty(rule.Sha256) && !string.Equals(rule.Sha256, m.Sha256 ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    return new PolicyVerdict { Allowed = false, Rule = RULE_DISALLOWED_MOD, Reason = $"Hash pin mismatch: {m.Name ?? m.Guid}" };
                }
            }
        }

        return new PolicyVerdict { Allowed = true };
    }

    private void StartWatchers()
    {
        _watchSettings = MakeWatcher(SettingsYaml,     () => LoadSettings());
        _watchAdmins   = MakeWatcher(AdminsYaml,       () => LoadAdmins());
        _watchAllowed  = MakeWatcher(AllowedModsYaml,  () => LoadAllowedMods());
    }

    private void StopWatchers()
    {
        try { _watchSettings?.Dispose(); } catch { }
        try { _watchAdmins?.Dispose(); } catch { }
        try { _watchAllowed?.Dispose(); } catch { }
    }

    private FileSystemWatcher MakeWatcher(string filePath, Action reloadAction)
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(filePath)!, Path.GetFileName(filePath));
        watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
        watcher.Changed += (s, e) => DebouncedReload(e.FullPath, reloadAction);
        watcher.Created += (s, e) => DebouncedReload(e.FullPath, reloadAction);
        watcher.Renamed += (s, e) => DebouncedReload(e.FullPath, reloadAction);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void DebouncedReload(string path, Action reloadAction, int debounceMs = 200)
    {
        var now = DateTime.UtcNow;
        if (_lastSeenWrite.TryGetValue(path, out var last) && (now - last).TotalMilliseconds < debounceMs)
            return;

        _lastSeenWrite[path] = now;

        System.Timers.Timer t = new System.Timers.Timer(debounceMs);
        t.AutoReset = false;
        t.Elapsed += (s, e) =>
        {
            try
            {
                reloadAction();
                LogS.LogInfo($"[ServerGuard] Reloaded: {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                LogS.LogError($"[ServerGuard] Reload failed for {Path.GetFileName(path)}: {ex.Message}");
            }
            finally
            {
                t.Dispose();
            }
        };
        t.Start();
    }
}
