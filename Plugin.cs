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

[BepInPlugin("com.taeguk.valheim.serverguard", "Valheim ServerGuard", "1.6.0")]
public class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource LogS;
    private Harmony _harmony;
	
	// -------- NEW: Discord log listener --------
    private DiscordLogListener _discordListener;

    // -------- Paths --------
    private static readonly string RootDir     = Path.Combine(Paths.ConfigPath, "ServerGuard");
    private static readonly string ConfDir     = Path.Combine(RootDir, "conf");
    private static readonly string BuildLogDir = Path.Combine(RootDir, "build_log");
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

    // Modset fingerprint (#2) - canonical identifier of the server's curated mod set.
    // Recomputed on every LoadAllowedMods. Written to ConfDir\modset_fingerprint.txt
    // so admins can publish it and players can verify a match against their client.
    private string _modsetFingerprintStrict = "";
    private string _modsetFingerprintLoose  = "";

    // SteamID -> outstanding manifest challenge. Keyed by peer.m_uid.
    private Dictionary<long, PendingAttestation> _pending = new();
    private readonly object _pendingLock = new object();

    // Peer m_uid -> reason we kicked them. Lets the disconnect hook suppress a redundant
    // "left" event when we just posted a "was kicked" event for the same peer.
    private readonly HashSet<long> _suppressLogoutFor = new HashSet<long>();

    // Per-peer ping-log state (#18). Rolling samples of m_ping reads, plus a flag
    // for whether we've posted the "first ping" message yet (one per session).
    private class PingState
    {
        public bool FirstPosted;
        public List<float> Samples = new List<float>();
    }
    private readonly Dictionary<long, PingState> _pingState = new Dictionary<long, PingState>();

    // Reflection handle for ZRpc.m_ping. The field is private in some Valheim builds;
    // we don't reference it directly in the source to avoid PlatformUserID-style
    // compile churn when Valheim shifts the ZRpc type around.
    private static System.Reflection.FieldInfo _rpcPingField;

    // Per-peer speed-check state (#6). Tracks last-seen position + time so the next
    // sample can compute a velocity. Reset on disconnect via Patch_Disconnect.
    private class SpeedState
    {
        public Vector3 LastPos;
        public bool HasLastPos;
        public float LastSampleTime;
        public int OverThresholdCount;
    }
    private readonly Dictionary<long, SpeedState> _speedState = new Dictionary<long, SpeedState>();

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
    private const string RULE_HASH_MISMATCH           = "HashMismatch";
    private const string RULE_CHAR_NAME_LIMIT         = "CharacterNameLimitExceeded";
    private const string RULE_DEVCOMMAND_ATTEMPT      = "DevcommandAttempt";
    private const string RULE_SPEED_HACK              = "SpeedHack";
    private const string RULE_ILLEGAL_ITEM            = "IllegalItem";
    private const string RULE_STACK_OVERFLOW          = "StackOverflow";
    private const string RULE_ANIMATION_CANCEL        = "AnimationCancel";
    private const string RULE_SKILL_OVERFLOW          = "SkillOverflow";

    // All rule keys, used to seed default countAsViolation map and validate user input.
    private static readonly string[] ALL_RULES = new[]
    {
        RULE_COMPANION_MISSING,
        RULE_HMAC_INVALID,
        RULE_CHALLENGE_MISMATCH,
        RULE_REQUIRED_MOD_MISSING,
        RULE_DISALLOWED_MOD,
        RULE_BANNED_MOD,
        RULE_HASH_MISMATCH,
        RULE_CHAR_NAME_LIMIT,
        RULE_DEVCOMMAND_ATTEMPT,
        RULE_SPEED_HACK,
        RULE_ILLEGAL_ITEM,
        RULE_STACK_OVERFLOW,
        RULE_ANIMATION_CANCEL,
        RULE_SKILL_OVERFLOW,
    };

    // File watchers (hot-reload)
    private FileSystemWatcher _watchSettings, _watchAdmins, _watchAllowed;
    private readonly Dictionary<string, DateTime> _lastSeenWrite = new();

    // -------- Raid event tracking --------
    private string    _currentRaidName      = null;
    private Vector3   _currentRaidPos       = Vector3.zero;
    private bool      _raidPaused           = false;
    private Coroutine _raidMonitorCoroutine = null;

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

    // -------------- Data Models --------------
    private class Settings
    {
        // --- Core enforcement ---
        public int  ViolationThreshold   { get; set; } = 3;
        public bool Enforce              { get; set; } = true;
        public string KickMessage        { get; set; } = "You cannot join: server security policy violation. Contact an administrator.";
        public string BanReason          { get; set; } = "Auto-banned due to repeated security violations.";
        public int CharacterLimit        { get; set; } = 1;

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
        public bool EnableMetrics        { get; set; } = true;

        // Public channel: receives ONLY player-facing events (joined / kicked / banned)
        // in plain language. Safe to share with the whole community.
        public string discordWebhookUrl  { get; set; } = ""; // public webhook (legacy name kept for back-compat)

        // Admin channel: receives curated admin-relevant events only (violation hits,
        // config reloads, admin command audit, kicks/bans). Set DiscordVerboseMirror
        // below to also mirror every ServerGuard log line.
        public string discordWebhookUrlAdmin { get; set; } = "";

        // If true, additionally attach a BepInEx log listener that forwards every
        // ServerGuard LogInfo/Warning/Error line to the admin webhook. Default false
        // for a clean admin channel; flip to true for verbose debug-style output.
        public bool DiscordVerboseMirror { get; set; } = false;

        public string discordChannelLink { get; set; } = "";

        // --- Daily summary post ---
        // Posts a one-paragraph digest of the last 24h to the chosen channel.
        // channel: "public" | "admin" | "both"
        // postHourUtc: 0-23 (UTC hour at which to fire each day)
        public bool DailySummaryEnabled { get; set; } = true;
        public int  DailySummaryHourUtc { get; set; } = 0;
        public string DailySummaryChannel { get; set; } = "admin";

        // --- Per-rule violation accounting ---
        // For each rule, decide whether a failure increments the player's violation count
        // (which can lead to auto-ban once it crosses ViolationThreshold). When false,
        // the player is still kicked (if Enforce is on) but no strike is recorded.
        // Missing keys are treated as `true` (default safe behaviour).
        [YamlMember(Alias = "countAsViolation", ApplyNamingConventions = false)]
        public Dictionary<string, bool> CountAsViolation { get; set; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            // Connection/attestation rules - the player is ALREADY kicked when one of these
            // fires, so counting them as violation strikes would double-punish. Defaulted off.
            ["CompanionMissing"]              = false,
            ["HmacInvalid"]                   = false,
            ["ChallengeMismatch"]             = false,
            ["RequiredModMissing"]            = false,
            ["DisallowedMod"]                 = false,
            ["BannedMod"]                     = false,
            ["HashMismatch"]                  = false,

            // In-game behavioural rules - escalate to auto-ban after repeats.
            ["CharacterNameLimitExceeded"]    = true,
            ["DevcommandAttempt"]             = true,
            ["SpeedHack"]                     = true,

            // Newer/experimental rules - defaulted off. Audit logs first, then opt in.
            ["IllegalItem"]                   = false,
            ["StackOverflow"]                 = false,
            ["AnimationCancel"]               = false,
            ["SkillOverflow"]                 = false,
        };

        // --- Devcommands gate (#5) ---
        // Server side of the gate: controls whether attempts reported by the companion
        // plugin are logged, posted to Discord, and recorded as violations.
        // The companion plugin ALWAYS blocks devcommands client-side regardless of this
        // setting; the toggle only controls server-side accounting.
        public bool EnableDevcommandGate { get; set; } = true;

        // --- Movement-speed sanity check (#6) ---
        // The server polls each connected player's position at a fixed interval and
        // computes horizontal speed (XZ plane - falling/jumping doesn't count). Speeds
        // above the threshold for N consecutive samples trigger SpeedHack.
        //
        // Defaults are conservative: vanilla sprint is ~5 m/s, a longship under sail is
        // ~9 m/s, modded mounts/skills can push higher. 15 m/s gives generous headroom.
        // Single-sample jumps larger than the teleport tolerance (e.g. portal travel)
        // are ignored to avoid false positives.
        public bool   EnableSpeedCheck                  { get; set; } = true;
        public double SpeedCheckMaxMetersPerSecond      { get; set; } = 15.0;
        public double SpeedCheckSampleSeconds           { get; set; } = 1.0;
        public int    SpeedCheckConsecutiveStrikes      { get; set; } = 3;
        public double SpeedCheckTeleportToleranceMeters { get; set; } = 60.0;

        // --- Inventory item validation (#7) ---
        // Server-side Harmony patch on Inventory.AddItem. For every item added on the
        // server, validate:
        //   * the item's m_shared.m_name is recognised by ObjectDB (catches spawned junk)
        //   * the item's stack count is at most m_maxStackSize * stackTolerance
        // Defaulted to log-only so admins can audit false positives from modded items
        // before flipping `inventoryCheckLogOnly: false` to actively reject the adds.
        public bool   EnableInventoryCheck      { get; set; } = true;
        public bool   InventoryCheckLogOnly     { get; set; } = true;
        public double InventoryCheckStackTolerance { get; set; } = 1.0;

        // --- Animation-cancel gate (anti-cheat) ---
        // The classic Valheim attack-spam exploit: trigger an emote (or sheathe weapon)
        // during attack recovery to cancel the recovery animation, allowing the next
        // attack to fire sooner than vanilla intended. The companion plugin blocks the
        // cancel client-side; this server-side toggle controls whether reports of those
        // blocks are logged + posted + counted as violations.
        public bool EnableAnimationCancelGate { get; set; } = true;

        // --- Skill-level cap enforcement (#10) ---
        // The companion plugin periodically reports the local player's m_skills levels
        // (Swords, Bows, Run, etc.) via the ServerGuard_SkillReport RPC. Any reported
        // level above SkillCapMaxLevel + SkillCapTolerance is flagged as SkillOverflow.
        // Vanilla cap is 100; some modpacks legitimately raise it - tune the tolerance
        // and max for your server.
        public bool   EnableSkillCap     { get; set; } = true;
        public double SkillCapMaxLevel   { get; set; } = 100.0;
        public double SkillCapTolerance  { get; set; } = 5.0;

        // --- Player death log (public Discord) ---
        // When the companion plugin sees the local player die, it sends a death report
        // RPC containing the position and (if known) the attacker. The server formats
        // a human-readable message and posts it to the PUBLIC Discord channel.
        // Pure forensic / social log - no violation rule attached.
        public bool EnableDeathLog { get; set; } = true;

        // --- Build / destroy heatmap (#14) ---
        // Server records every piece placement (via companion RPC) and every piece
        // destruction (via server-side WearNTear patches) to a daily CSV file under
        // BepInEx/config/ServerGuard/build_log/. Useful for investigating grief reports
        // ("who built/destroyed what at [x, z]?"). No Discord output, no violation
        // rule - it's a pure forensic log.
        public bool EnableBuildLog        { get; set; } = true;
        public int  BuildLogRetentionDays { get; set; } = 30;

        // --- Self-test (#17) ---
        // On boot, run a suite of smoke tests (HMAC, settings, fingerprint, etc.) and
        // post a one-line summary to the admin Discord channel. FAILs always post,
        // passes only post when SelfTestPostOnPass is true. Also available on-demand
        // via the `sg selftest` console command.
        public bool EnableSelfTest         { get; set; } = true;
        public bool SelfTestPostOnPass     { get; set; } = false;

        // --- Ping / latency log (#18) ---
        // When true, the server samples each peer's RTT every ~5s and posts to the
        // ADMIN channel: one "first ping" message shortly after join, plus a session
        // average on disconnect. Useful for spotting proxy users / VPN users.
        public bool EnablePingLog          { get; set; } = false;
        public int  PingLogSampleSeconds   { get; set; } = 5;

        // --- Cheat item removal ---
        // Items whose prefab names are listed here are deleted from any non-admin
        // player's inventory on login (the companion performs the removal after spawn).
        public bool EnableCheatItemRemoval { get; set; } = true;
        public List<string> CheatItems     { get; set; } = new List<string> { "SwordCheat", "SledgeCheat" };

        // Deprecated (kept so old YAML loads without errors). v1.4+ uses two webhooks instead.
        public bool DiscordPublicMode { get; set; } = true;

        // --- Deprecated (kept for backward YAML parsing only; no runtime effect) ---
        public bool AggressiveNoModCheck   { get; set; } = false;
        public bool EnableAssemblyScanning { get; set; } = false;
        public bool UseWhitelistMode       { get; set; } = false;
        public bool RequireAttestation     { get; set; } = false;
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

    // Tracks the URL the admin DiscordLogListener is currently bound to. When settings.yaml
    // hot-reloads and the URL changes, we tear down the old listener and attach a new one.
    // Without this, edits to discordWebhookUrlAdmin AFTER server boot are silently ignored
    // because the listener URL is captured at construction time.
    private string _attachedAdminWebhookUrl = "";

    // Becomes true after Awake completes. Used to suppress admin Discord posts during
    // initial config load (which would spam the channel with reload notices on every
    // restart) and only fire them on actual hot-reloads.
    private bool _bootCompleted = false;

    // Guard against starting the daily-summary coroutine more than once. Awake may start
    // it; hot-reload may also start it if the URL was added post-boot.
    private bool _dailySummaryStarted = false;

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
            $"[ServerGuard] Loaded (v1.6.0). " +
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
		
		// Discord channels + daily summary. This call is idempotent and runs on every
		// settings.yaml hot-reload so adding/changing webhook URLs after boot takes
		// effect without a server restart.
		ReconfigureDiscordAndSummary();

		// Movement-speed sanity check (#6). Runs forever; the toggle is checked each tick
		// so flipping it at runtime via hot-reload of settings.yaml takes effect immediately.
		StartCoroutine(SpeedCheckLoop());

		// Build-log retention pruner (#14). Hourly sweep; toggle / retention re-read each
		// pass so settings.yaml hot-reloads are honoured.
		StartCoroutine(BuildLogCleanupLoop());

		// Ping-log sampler (#18). Toggle checked each tick; default disabled.
		StartCoroutine(PingLogLoop());

		// Self-test (#17). One-shot suite of smoke tests. Always logs the result;
		// posts to admin Discord on FAIL (and on PASS if SelfTestPostOnPass is true).
		if (_settings.EnableSelfTest)
		{
			try
			{
				var results = RunSelfTest();
				LogS.LogInfo(FormatSelfTestReport(results));
				var anyFail = results.Any(r => !r.Pass);
				if (anyFail || _settings.SelfTestPostOnPass)
				{
					PostAdminEvent(FormatSelfTestForDiscord(results));
				}
			}
			catch (Exception ex)
			{
				LogS.LogError($"[ServerGuard] Self-test failed to run: {ex}");
			}
		}

		// One-line admin-channel announcement that the plugin is up. Lets moderators
		// confirm the server came back online after a restart without scraping logs.
		PostAdminEvent(
			$":rocket: **ServerGuard online** v1.6.0  " +
			$"enforce={(_settings.Enforce ? "ON" : "off")}  " +
			$"requireHmac={(_settings.RequireHmac ? "ON" : "off")}  " +
			$"req/allow/ban={_requiredMods.Count}/{_allowedMods.Count}/{_bannedMods.Count}  " +
			$"modset=`{ModsetFingerprint.Short(_modsetFingerprintLoose)}`");

		// Boot done - hot-reload notices will now reach the admin channel.
		_bootCompleted = true;
		if (_settings.EnableSpeedCheck)
		{
			LogS.LogInfo(
				$"[ServerGuard] Speed check enabled  threshold={_settings.SpeedCheckMaxMetersPerSecond:F1}m/s  " +
				$"sample={_settings.SpeedCheckSampleSeconds:F1}s  " +
				$"strikes={_settings.SpeedCheckConsecutiveStrikes}  " +
				$"teleport-tol={_settings.SpeedCheckTeleportToleranceMeters:F1}m");
		}
    }

    private void OnDestroy()
	{
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
			if (_discordListener != null)
			{
				try
				{
					BepInEx.Logging.Logger.Listeners.Remove(_discordListener);
				}
				catch (Exception ex)
				{
					LogS?.LogWarning($"[ServerGuard] Removing Discord listener failed: {ex.Message}");
				}

				try
				{
					_discordListener.Dispose();
				}
				catch (Exception ex)
				{
					LogS?.LogWarning($"[ServerGuard] Disposing Discord listener failed: {ex.Message}");
				}

				_discordListener = null;
			}
		}
		catch (Exception ex)
		{
			LogS?.LogWarning($"[ServerGuard] Discord listener cleanup failed: {ex.Message}");
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
	
    // Channel targets for one-shot Discord posts.
    private enum DiscordChannel { Public, Admin, Both }

    private async Task PostToWebhook(string url, string text)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            using var http = new System.Net.Http.HttpClient();
            var payload = new { content = text };
            var json = JsonConvert.SerializeObject(payload);
            using var req = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
            await http.PostAsync(url, req);
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] PostToWebhook failed: {ex.Message}");
        }
    }

    // Sends one message to the requested channel(s). Default = Public (player-facing events).
    private async Task SendDiscordNow(string text, DiscordChannel target = DiscordChannel.Public)
    {
        if (_settings == null) return;
        var pub = _settings.discordWebhookUrl;
        var adm = _settings.discordWebhookUrlAdmin;

        switch (target)
        {
            case DiscordChannel.Public:
                await PostToWebhook(pub, text);
                break;
            case DiscordChannel.Admin:
                // Fall back to public if no dedicated admin webhook (single-channel deployments).
                await PostToWebhook(string.IsNullOrWhiteSpace(adm) ? pub : adm, text);
                break;
            case DiscordChannel.Both:
                await PostToWebhook(pub, text);
                if (!string.IsNullOrWhiteSpace(adm) && !string.Equals(adm, pub, StringComparison.Ordinal))
                    await PostToWebhook(adm, text);
                break;
        }
    }

    // -------------- Folder & File Bootstrapping --------------
    private void EnsureFoldersAndFiles()
    {
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(ConfDir);

        if (!File.Exists(SettingsYaml))
        {
            var defaults = new Settings
            {
                SharedSecret = GenerateSharedSecret()
            };
            var sb = new StringBuilder();
            sb.AppendLine("# ServerGuard settings (v1.6.0)");
            sb.AppendLine("#");
            sb.AppendLine("# Client-attestation handshake:");
            sb.AppendLine("#   requireCompanion       - if true, peers without the ServerGuard.Client plugin are kicked.");
            sb.AppendLine("#   companionTimeoutSeconds - how long to wait for the manifest before declaring 'no companion'.");
            sb.AppendLine("#   requireHmac            - if true, manifests must carry a valid HMAC signature.");
            sb.AppendLine("#   sharedSecret           - secret string. Must match every client's client.yaml `sharedSecret`.");
            sb.AppendLine("#                            Generate something long and random (e.g. `openssl rand -hex 32`).");
            sb.AppendLine("#   allowUnlisted          - if true, mods absent from allowed_mods.yaml are tolerated.");
            sb.AppendLine("#                            Default false = strict allowlist.");
            sb.AppendLine("#   maxClockSkewSeconds    - reject manifests whose timestamp is more than this off from server time.");
            sb.AppendLine("#   logPeerManifest        - if true, log every connecting peer's full manifest (verbose).");
            sb.AppendLine("#                            Useful for harvesting plugin GUIDs to populate allowed_mods.yaml.");
            sb.AppendLine("#");
            sb.AppendLine("# Identity / character limits:");
            sb.AppendLine("#   characterLimit         - max distinct character names a SteamID may use on this server.");
            sb.AppendLine("#");
            sb.AppendLine("# Devcommands gate (anti-cheat):");
            sb.AppendLine("#   enableDevcommandGate   - if true, devcommand attempts reported by the companion");
            sb.AppendLine("#                            plugin are logged + posted + counted. The companion");
            sb.AppendLine("#                            ALWAYS blocks `devcommands` and forces");
            sb.AppendLine("#                            Console.IsCheatsEnabled=false on multiplayer clients;");
            sb.AppendLine("#                            this toggle only affects server-side accounting.");
            sb.AppendLine("#");
            sb.AppendLine("# Movement-speed sanity check (anti-cheat):");
            sb.AppendLine("#   enableSpeedCheck                  - master toggle.");
            sb.AppendLine("#   speedCheckMaxMetersPerSecond      - horizontal speed cap. Vanilla sprint ~5 m/s,");
            sb.AppendLine("#                                       longship sail ~9-10 m/s. 15 m/s is a generous");
            sb.AppendLine("#                                       default; raise for modded mounts/skills.");
            sb.AppendLine("#   speedCheckSampleSeconds           - poll interval. Lower = faster detection,");
            sb.AppendLine("#                                       more sensitive to lag spikes. 1.0 is balanced.");
            sb.AppendLine("#   speedCheckConsecutiveStrikes      - over-threshold samples needed to fire SpeedHack.");
            sb.AppendLine("#   speedCheckTeleportToleranceMeters - single-sample displacements larger than this");
            sb.AppendLine("#                                       are treated as legitimate teleports (portals,");
            sb.AppendLine("#                                       stones) and reset the strike counter rather");
            sb.AppendLine("#                                       than incrementing it.");
            sb.AppendLine("#");
            sb.AppendLine("# Inventory item validation (anti-cheat):");
            sb.AppendLine("#   enableInventoryCheck         - master toggle for Inventory.AddItem validation.");
            sb.AppendLine("#   inventoryCheckLogOnly        - if true (default), invalid items are logged but");
            sb.AppendLine("#                                  still added. Flip to false to actively block them.");
            sb.AppendLine("#                                  Start in log-only mode to audit false positives,");
            sb.AppendLine("#                                  then tighten.");
            sb.AppendLine("#   inventoryCheckStackTolerance - multiplier on each item's m_maxStackSize. 1.0 is");
            sb.AppendLine("#                                  strict; 2.0 allows up to 2x the vanilla cap for");
            sb.AppendLine("#                                  modpacks that legitimately raise limits.");
            sb.AppendLine("#");
            sb.AppendLine("# Animation-cancel gate (anti-cheat):");
            sb.AppendLine("#   enableAnimationCancelGate - if true, attempts to cancel attack-recovery");
            sb.AppendLine("#                               animations (emote, sheathe) reported by the");
            sb.AppendLine("#                               companion are logged + posted + counted.");
            sb.AppendLine("#                               The companion ALWAYS blocks the cancel client-side;");
            sb.AppendLine("#                               this toggle only controls server-side accounting.");
            sb.AppendLine("#");
            sb.AppendLine("# Skill-level cap (anti-cheat):");
            sb.AppendLine("#   enableSkillCap     - master toggle. Companion plugin sends a snapshot of");
            sb.AppendLine("#                        each player's m_skills every ~60s; server flags any");
            sb.AppendLine("#                        skill above the cap.");
            sb.AppendLine("#   skillCapMaxLevel   - max allowed level. Vanilla is 100.");
            sb.AppendLine("#   skillCapTolerance  - added to skillCapMaxLevel to form the actual flag");
            sb.AppendLine("#                        threshold. Use to absorb float rounding / minor over-shoot.");
            sb.AppendLine("#                        Raise both for modpacks that legitimately allow higher.");
            sb.AppendLine("#");
            sb.AppendLine("# Death log (public Discord):");
            sb.AppendLine("#   enableDeathLog     - if true, posts a public-channel message every time a");
            sb.AppendLine("#                        player dies. Includes position and killer (player name");
            sb.AppendLine("#                        + SteamID for PvP, creature name for mobs, cause for");
            sb.AppendLine("#                        environmental). Pure forensic log - no violation rule.");
            sb.AppendLine("#");
            sb.AppendLine("# Build / destroy heatmap:");
            sb.AppendLine("#   enableBuildLog        - if true, every piece placement and destruction is");
            sb.AppendLine("#                           appended to a daily CSV file at");
            sb.AppendLine("#                           BepInEx/config/ServerGuard/build_log/YYYY-MM-DD.csv.");
            sb.AppendLine("#                           Useful for investigating grief reports. No Discord");
            sb.AppendLine("#                           output, no violation rule - pure forensic log.");
            sb.AppendLine("#   buildLogRetentionDays - delete CSV files older than this. Default 30.");
            sb.AppendLine("#");
            sb.AppendLine("# Self-test (boot-time smoke checks):");
            sb.AppendLine("#   enableSelfTest      - run a suite of smoke tests (HMAC, fingerprint,");
            sb.AppendLine("#                         build-log dir, webhook syntax, ...) at startup.");
            sb.AppendLine("#                         Result is logged and posted to admin Discord on FAIL.");
            sb.AppendLine("#                         Re-run on demand via the `sg selftest` console cmd.");
            sb.AppendLine("#   selfTestPostOnPass  - if true, also post a green-checkmark line to admin");
            sb.AppendLine("#                         even when all tests pass. Default false (only FAILs).");
            sb.AppendLine("#");
            sb.AppendLine("# Ping / latency log:");
            sb.AppendLine("#   enablePingLog        - if true, sample each peer's RTT and post the first");
            sb.AppendLine("#                          measurement after join + session avg on disconnect");
            sb.AppendLine("#                          to the admin channel. Useful for proxy / VPN spotting.");
            sb.AppendLine("#                          Default false.");
            sb.AppendLine("#   pingLogSampleSeconds - sampling interval. Default 5.");
            sb.AppendLine("#");
            sb.AppendLine("# Cheat item removal:");
            sb.AppendLine("#   enableCheatItemRemoval - if true, the companion strips the items listed in");
            sb.AppendLine("#                            cheatItems from any non-admin player's inventory on login.");
            sb.AppendLine("#   cheatItems             - prefab names to remove (default: SwordCheat, SledgeCheat).");
            sb.AppendLine("#");
            sb.AppendLine("# Discord (two independent channels - either, both, or neither):");
            sb.AppendLine("#   discordWebhookUrl       - PUBLIC channel. Receives only player-facing");
            sb.AppendLine("#                             events (joined / kicked / banned / died) in plain");
            sb.AppendLine("#                             language. Safe for community-visible channels.");
            sb.AppendLine("#   discordWebhookUrlAdmin  - ADMIN channel. Receives CURATED admin-relevant");
            sb.AppendLine("#                             events: violation strikes, config reloads, admin");
            sb.AppendLine("#                             command audit, kicks/bans, daily summary. Clean");
            sb.AppendLine("#                             enough to scan; use a moderator-only channel.");
            sb.AppendLine("#   discordVerboseMirror    - if true, ALSO mirror every ServerGuard log line");
            sb.AppendLine("#                             to the admin channel (noisy). Default false.");
            sb.AppendLine("#");
            sb.AppendLine("# Daily summary:");
            sb.AppendLine("#   dailySummaryEnabled     - if true, post a one-paragraph digest each day.");
            sb.AppendLine("#   dailySummaryHourUtc     - 0..23, UTC hour at which the post fires.");
            sb.AppendLine("#   dailySummaryChannel     - 'public' | 'admin' | 'both'.");
            sb.AppendLine("#");
            sb.AppendLine("# Per-rule violation accounting (countAsViolation):");
            sb.AppendLine("#   Each rule can independently decide whether a failure increments the");
            sb.AppendLine("#   player's violation count toward auto-ban. A 'false' rule still kicks the");
            sb.AppendLine("#   player (when enforce: true) but doesn't add a strike. Tune to match how");
            sb.AppendLine("#   strict you want your server to be. Defaults shown below.");
            sb.AppendLine("#");
            sb.AppendLine(_yamlOut.Serialize(defaults));
            File.WriteAllText(SettingsYaml, sb.ToString());
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
            sb.AppendLine("# Recommended workflow:");
            sb.AppendLine("#   1. Install the ServerGuard companion plugin on a client that has your full modpack.");
            sb.AppendLine("#   2. Launch Valheim once. The client writes a snippet to:");
            sb.AppendLine("#        <profile>/BepInEx/config/ServerGuard/mods_for_allowed_mods.yaml");
            sb.AppendLine("#   3. Paste that snippet's `allowed_mods:` block into this file.");
            sb.AppendLine("#");
            sb.AppendLine("# Or for ad-hoc harvesting: set logPeerManifest: true in settings.yaml and connect a");
            sb.AppendLine("# real client - every GUID will appear in BepInEx/LogOutput.log.");
            sb.AppendLine();
            sb.AppendLine("required_mods:");
            sb.AppendLine("  - com.taeguk.valheim.serverguard.client    # the ServerGuard companion plugin");
            sb.AppendLine();
            sb.AppendLine("allowed_mods: []");
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
            if (_bootCompleted) PostAdminEvent(":arrows_counterclockwise: settings.yaml reloaded");
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Failed to load settings.yaml: {ex.Message}");
            _settings = new Settings();
        }

        // Re-evaluate Discord listener + daily-summary scheduler after every reload.
        // This is what makes "edit settings.yaml, save, no restart needed" actually work
        // for the admin webhook. Without this call, attaching the admin webhook URL after
        // server boot has no effect until the next restart.
        try { ReconfigureDiscordAndSummary(); }
        catch (Exception ex) { LogS?.LogWarning($"[ServerGuard] Discord/summary reconfigure failed: {ex.Message}"); }
    }

    // Idempotent: ensures the Discord admin log mirror is attached to the current URL,
    // and that the daily-summary coroutine is running if any webhook is configured.
    // Safe to call multiple times - on second+ calls, it tears down the old listener
    // before attaching a new one only when the URL has actually changed.
    private void ReconfigureDiscordAndSummary()
    {
        if (_settings == null) return;

        var pub = _settings.discordWebhookUrl ?? "";
        var adm = _settings.discordWebhookUrlAdmin ?? "";

        // ----- Public channel: nothing to attach. SendDiscordNow reads _settings live. -----
        if (!string.IsNullOrWhiteSpace(pub))
        {
            // Log once per URL change to keep the log readable.
            // (Reusing _attachedAdminWebhookUrl style would need a separate field; for now
            // we just log on every reload - cheap and clear.)
        }

        // ----- Admin channel listener (verbose mirror, opt-in via DiscordVerboseMirror) -----
        // When DiscordVerboseMirror is false (default), we DO NOT attach a log listener
        // at all. The admin channel only receives the curated PostAdminEvent calls
        // (violations, kicks, bans, reloads, admin actions). This keeps the channel
        // readable. Set DiscordVerboseMirror: true in settings.yaml to get the full
        // BepInEx log mirror for debug sessions.
        bool wantVerbose = !string.IsNullOrWhiteSpace(adm) && _settings.DiscordVerboseMirror;
        bool haveVerbose = _discordListener != null;
        bool urlChanged  = !string.Equals(adm, _attachedAdminWebhookUrl, StringComparison.Ordinal);

        if (haveVerbose && (!wantVerbose || urlChanged))
        {
            // Tear down the existing listener (turning off mirror, or URL changed).
            try { BepInEx.Logging.Logger.Listeners.Remove(_discordListener); } catch { }
            try { _discordListener.Dispose(); } catch { }
            _discordListener = null;
            haveVerbose = false;
        }

        if (wantVerbose && !haveVerbose)
        {
            try
            {
                var allowedSource = LogS?.SourceName ?? "Valheim ServerGuard";
                _discordListener = new DiscordLogListener(adm, "[ServerGuard]", allowedSource);
                BepInEx.Logging.Logger.Listeners.Add(_discordListener);
                LogS.LogInfo($"[ServerGuard] Admin Discord verbose mirror enabled for source '{allowedSource}'.");
            }
            catch (Exception ex)
            {
                LogS.LogWarning($"[ServerGuard] Failed to enable admin Discord verbose mirror: {ex.Message}");
            }
        }

        // Log a one-time status line on URL change so the admin can confirm wiring.
        if (urlChanged)
        {
            if (!string.IsNullOrWhiteSpace(adm))
            {
                LogS.LogInfo($"[ServerGuard] Admin Discord channel armed (curated events; verbose mirror: {(_settings.DiscordVerboseMirror ? "ON" : "OFF")}).");
            }
            else
            {
                LogS.LogInfo("[ServerGuard] Admin Discord channel disabled (URL not set).");
            }
            _attachedAdminWebhookUrl = adm;
        }

        // ----- Daily summary coroutine: start once if a webhook is configured. -----
        if (!_dailySummaryStarted
            && _settings.DailySummaryEnabled
            && (!string.IsNullOrWhiteSpace(pub) || !string.IsNullOrWhiteSpace(adm)))
        {
            StartCoroutine(DailySummaryLoop());
            _dailySummaryStarted = true;
            LogS.LogInfo($"[ServerGuard] Daily summary enabled (fires at {_settings.DailySummaryHourUtc:D2}:00 UTC, channel: {_settings.DailySummaryChannel}).");
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
            if (_bootCompleted) PostAdminEvent($":arrows_counterclockwise: admins.yaml reloaded ({_admins.Count} admins)");
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
            if (_bootCompleted)
            {
                PostAdminEvent($":arrows_counterclockwise: allowed_mods.yaml reloaded — req={_requiredMods.Count} allow={_allowedMods.Count} ban={_bannedMods.Count}");
            }
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Failed to load allowed_mods.yaml: {ex.Message}");
            _requiredMods = new List<AllowedModEntry>();
            _allowedMods  = new List<AllowedModEntry>();
            _bannedMods   = new List<AllowedModEntry>();
        }

        RecomputeModsetFingerprint();
    }

    // Recomputes the canonical mod-set fingerprint over (required_mods ∪ allowed_mods)
    // and writes it to ConfDir\modset_fingerprint.txt. Called on startup and on every
    // hot reload of allowed_mods.yaml. Banned mods are intentionally NOT included -
    // they describe what's forbidden, not what's part of the curated set.
    private void RecomputeModsetFingerprint()
    {
        try
        {
            var pairs = _requiredMods
                .Concat(_allowedMods)
                .Select(e => new KeyValuePair<string, string>(e?.Key ?? "", e?.Sha256 ?? ""))
                .ToList();

            _modsetFingerprintStrict = ModsetFingerprint.ComputeStrict(pairs);
            _modsetFingerprintLoose  = ModsetFingerprint.ComputeLoose(pairs);

            var fpPath = Path.Combine(ConfDir, "modset_fingerprint.txt");
            var sb = new StringBuilder();
            sb.AppendLine("# Modset fingerprint for this server.");
            sb.AppendLine("# Re-generated on every hot reload of allowed_mods.yaml.");
            sb.AppendLine("#");
            sb.AppendLine("# LOOSE  - matches across version bumps. Useful for 'are we on the same modpack?'");
            sb.AppendLine("# STRICT - also pins each mod's DLL hash. Matches only on identical binaries.");
            sb.AppendLine("#");
            sb.AppendLine("# Players can compare these against their client startup log line:");
            sb.AppendLine("#   [ServerGuard.Client] Modset fingerprint  loose=XXXXXXXX  strict=YYYYYYYY");
            sb.AppendLine();
            sb.AppendLine($"loose:  {_modsetFingerprintLoose}");
            sb.AppendLine($"strict: {_modsetFingerprintStrict}");
            sb.AppendLine();
            sb.AppendLine($"short_loose:  {ModsetFingerprint.Short(_modsetFingerprintLoose)}");
            sb.AppendLine($"short_strict: {ModsetFingerprint.Short(_modsetFingerprintStrict)}");
            File.WriteAllText(fpPath, sb.ToString());

            LogS.LogInfo(
                $"[ServerGuard] Modset fingerprint  loose={ModsetFingerprint.Short(_modsetFingerprintLoose)}  " +
                $"strict={ModsetFingerprint.Short(_modsetFingerprintStrict)}  " +
                $"(full values in {Path.GetFileName(fpPath)})");
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] Failed to compute modset fingerprint: {ex.Message}");
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

    // Returns the player-friendly explanation of a rule for Discord / kick messages.
    // Used everywhere we present a reason publicly. `detail` is an optional mod name etc.
    private static string FriendlyReason(string rule, string detail = null)
    {
        switch (rule)
        {
            case RULE_COMPANION_MISSING:    return "missing the required companion mod";
            case RULE_HMAC_INVALID:         return "wrong password";
            case RULE_CHALLENGE_MISMATCH:   return "wrong password";
            case RULE_REQUIRED_MOD_MISSING: return string.IsNullOrEmpty(detail) ? "missing a required mod" : $"missing a required mod ({detail})";
            case RULE_DISALLOWED_MOD:       return string.IsNullOrEmpty(detail) ? "had a mod that isn't allowed" : $"had a mod that isn't allowed ({detail})";
            case RULE_BANNED_MOD:           return string.IsNullOrEmpty(detail) ? "had a banned mod" : $"had a banned mod ({detail})";
            case RULE_HASH_MISMATCH:        return string.IsNullOrEmpty(detail) ? "mod file doesn't match the server's copy" : $"mod file doesn't match the server's copy ({detail})";
            case RULE_CHAR_NAME_LIMIT:      return "tried to use too many characters";
            case RULE_DEVCOMMAND_ATTEMPT:   return string.IsNullOrEmpty(detail) ? "tried to use cheats" : $"tried to use cheats (`{detail}`)";
            case RULE_SPEED_HACK:           return string.IsNullOrEmpty(detail) ? "moved suspiciously fast" : $"moved suspiciously fast (~{detail})";
            case RULE_ILLEGAL_ITEM:         return string.IsNullOrEmpty(detail) ? "had an unknown item" : $"had an unknown item ({detail})";
            case RULE_STACK_OVERFLOW:       return string.IsNullOrEmpty(detail) ? "had an over-sized item stack" : $"had an over-sized item stack ({detail})";
            case RULE_ANIMATION_CANCEL:     return string.IsNullOrEmpty(detail) ? "tried to cancel attack animation" : $"tried to cancel attack with {detail}";
            case RULE_SKILL_OVERFLOW:       return string.IsNullOrEmpty(detail) ? "skill level above cap" : $"skill level above cap ({detail})";
            default:                        return "policy violation";
        }
    }

    // Returns true if the given rule should increment the violation count, false otherwise.
    // Missing keys default to FALSE - all new rules are opt-in. Admins must explicitly
    // add a `RuleName: true` entry under countAsViolation to escalate that rule to
    // auto-ban tracking.
    private bool RuleCountsAsViolation(string rule)
    {
        if (_settings?.CountAsViolation == null) return false;
        return _settings.CountAsViolation.TryGetValue(rule, out var v) && v;
    }

    // Posts a curated admin-channel message. No-op if no admin webhook configured.
    // Use for moderation-relevant events: violations, config reloads, admin actions,
    // plugin lifecycle. Keep messages short - the goal is a readable channel.
    private void PostAdminEvent(string text)
    {
        if (_settings == null) return;
        if (string.IsNullOrWhiteSpace(_settings.discordWebhookUrlAdmin)) return;
        if (string.IsNullOrWhiteSpace(text)) return;
        _ = SendDiscordNow(text, DiscordChannel.Admin);
    }

    // Posts a player-facing event to Discord. No-op if no webhook configured.
    // Also increments the daily-summary counters.
    //
    // Routing: admin SteamIDs are hidden from the PUBLIC channel - their lifecycle
    // events (joined, left, died, kicked, was-auto-banned) are redirected to the
    // ADMIN channel only. Moderators still see the activity, but the playerbase
    // doesn't see admins coming and going. Non-admin events go to PUBLIC as normal.
    private void PostPlayerEvent(string emoji, string platformId, string action, string reason = null)
    {
        // Bookkeeping for the daily digest (#19). Runs even if no webhook is set, so the
        // operator gets a summary the moment they wire one up.
        TrackEventForDailySummary(action, reason);

        var who = FormatPlayer(platformId);
        var line = string.IsNullOrEmpty(reason)
            ? $"{emoji} {who} {action}"
            : $"{emoji} {who} {action} — {reason}";

        var isAdmin = !string.IsNullOrWhiteSpace(platformId) && IsAdmin(platformId);
        if (isAdmin)
        {
            // Admin event: admin channel only, never public.
            if (!string.IsNullOrWhiteSpace(_settings?.discordWebhookUrlAdmin))
                _ = SendDiscordNow(line, DiscordChannel.Admin);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings?.discordWebhookUrl)) return;
        _ = SendDiscordNow(line, DiscordChannel.Public);
    }

    // -------------- Daily Summary (#19) --------------
    // In-memory tally of events since the last daily flush. Reset on every post.
    private readonly object _summaryLock = new object();
    private DateTime _summarySince = DateTime.UtcNow;
    private int _summaryJoins = 0;
    private int _summaryKicks = 0;
    private int _summaryBans  = 0;
    private readonly Dictionary<string, int> _summaryKickReasons = new(StringComparer.OrdinalIgnoreCase);
    private int _summaryLeaves = 0;

    private void TrackEventForDailySummary(string action, string reason)
    {
        if (string.IsNullOrEmpty(action)) return;
        lock (_summaryLock)
        {
            if (action.IndexOf("joined", StringComparison.OrdinalIgnoreCase) >= 0)
                _summaryJoins++;
            else if (action.IndexOf("auto-banned", StringComparison.OrdinalIgnoreCase) >= 0)
                _summaryBans++;
            else if (action.IndexOf("kicked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _summaryKicks++;
                var key = string.IsNullOrWhiteSpace(reason) ? "other" : reason.Trim();
                _summaryKickReasons.TryGetValue(key, out var n);
                _summaryKickReasons[key] = n + 1;
            }
            else if (action.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0)
                _summaryLeaves++;
        }
    }

    // -------------- Self-test (#17) --------------
    //
    // A suite of smoke tests that catch the common misconfig + environment issues
    // before they bite a real player. Runs once on boot (via EnableSelfTest) and is
    // re-runnable on demand via `sg selftest`. Output is logged + (when enabled)
    // posted as a one-line summary to the admin Discord.

    private sealed class SelfTestResult
    {
        public string Name;
        public bool Pass;
        public string Detail;
    }

    private List<SelfTestResult> RunSelfTest()
    {
        var results = new List<SelfTestResult>();
        SelfTestResult Add(string name, bool pass, string detail)
        {
            var r = new SelfTestResult { Name = name, Pass = pass, Detail = detail ?? "" };
            results.Add(r); return r;
        }

        // 1. Shared secret is set (when requireHmac is on).
        try
        {
            if (_settings.RequireHmac && string.IsNullOrEmpty(_settings.SharedSecret))
            {
                Add("HMAC sharedSecret", false, "requireHmac=true but sharedSecret is empty");
            }
            else if (_settings.RequireHmac)
            {
                Add("HMAC sharedSecret", true, $"len={_settings.SharedSecret.Length}");
            }
            else
            {
                Add("HMAC sharedSecret", true, "requireHmac disabled - skipped");
            }
        }
        catch (Exception ex) { Add("HMAC sharedSecret", false, ex.Message); }

        // 2. HMAC sign-and-verify roundtrip with the configured secret.
        try
        {
            var fake = new ModManifest
            {
                SchemaVersion = "1",
                Challenge     = "selftest-challenge",
                TimestampUtc  = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Mods          = new List<ModManifestEntry>(),
            };
            var canon = fake.CanonicalForHmac();
            var sig   = ModManifest.ComputeHmac(canon, _settings.SharedSecret ?? "");
            var ok    = ModManifest.ConstantTimeEquals(sig, ModManifest.ComputeHmac(canon, _settings.SharedSecret ?? ""));
            Add("HMAC roundtrip", ok, ok ? "sign/verify match" : "sign/verify MISMATCH");
        }
        catch (Exception ex) { Add("HMAC roundtrip", false, ex.Message); }

        // 3. Policy validation - run an empty manifest through ValidateAgainstPolicy.
        //    Expect rejection only if required_mods is non-empty (companion required).
        try
        {
            var emptyManifest = new ModManifest { Mods = new List<ModManifestEntry>() };
            var verdict = ValidateAgainstPolicy(emptyManifest);
            var hasRequired = _requiredMods.Count > 0;
            var expected = hasRequired ? !verdict.Allowed : verdict.Allowed;
            var msg = hasRequired
                ? (verdict.Allowed ? "empty manifest passed but required mods exist!" : $"empty manifest rejected as expected ({verdict.Rule})")
                : (verdict.Allowed ? "empty manifest allowed (no required mods)" : "empty manifest unexpectedly rejected");
            Add("Policy validator", expected, msg);
        }
        catch (Exception ex) { Add("Policy validator", false, ex.Message); }

        // 4. Build-log directory writable.
        try
        {
            Directory.CreateDirectory(BuildLogDir);
            var probe = Path.Combine(BuildLogDir, ".selftest_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            Add("Build-log dir writable", true, BuildLogDir);
        }
        catch (Exception ex) { Add("Build-log dir writable", false, ex.Message); }

        // 5. Modset fingerprint computed.
        try
        {
            var ok = !string.IsNullOrEmpty(_modsetFingerprintLoose) && !string.IsNullOrEmpty(_modsetFingerprintStrict);
            Add("Modset fingerprint", ok, ok ? $"loose={ModsetFingerprint.Short(_modsetFingerprintLoose)} strict={ModsetFingerprint.Short(_modsetFingerprintStrict)}" : "empty");
        }
        catch (Exception ex) { Add("Modset fingerprint", false, ex.Message); }

        // 6. Webhook URL sanity (https + non-empty path). No network call - just
        //    syntactic. Helps catch typos / accidentally-empty-after-quote-strip values.
        Add("Public webhook URL",
            string.IsNullOrEmpty(_settings.discordWebhookUrl) || IsWebhookUrlSane(_settings.discordWebhookUrl),
            string.IsNullOrEmpty(_settings.discordWebhookUrl) ? "not configured (ok)" : "looks valid");
        Add("Admin  webhook URL",
            string.IsNullOrEmpty(_settings.discordWebhookUrlAdmin) || IsWebhookUrlSane(_settings.discordWebhookUrlAdmin),
            string.IsNullOrEmpty(_settings.discordWebhookUrlAdmin) ? "not configured (ok)" : "looks valid");

        // 7. Admin list non-empty (warning if empty - sg commands won't be usable).
        Add("Admins configured", _admins.Count > 0,
            _admins.Count > 0 ? $"{_admins.Count} admin SteamID(s) in admins.yaml" : "admins.yaml is empty - sg commands will be unusable");

        return results;
    }

    private static bool IsWebhookUrlSane(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        // Discord webhooks look like .../webhooks/<id>/<token>.
        if (url.IndexOf("/webhooks/", StringComparison.OrdinalIgnoreCase) < 0) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.IndexOf('.') >= 0;
    }

    // Builds a human-readable report. Used for log output, admin Discord posts, and
    // the `sg selftest` reply.
    private string FormatSelfTestReport(List<SelfTestResult> results)
    {
        var pass = results.Count(r => r.Pass);
        var fail = results.Count - pass;
        var sb = new StringBuilder();
        sb.AppendLine($"[ServerGuard] Self-test  pass={pass}  fail={fail}");
        foreach (var r in results)
        {
            var icon = r.Pass ? "PASS" : "FAIL";
            sb.AppendLine($"  [{icon}]  {r.Name,-26}  {r.Detail}");
        }
        return sb.ToString().TrimEnd();
    }

    private string FormatSelfTestForDiscord(List<SelfTestResult> results)
    {
        var pass = results.Count(r => r.Pass);
        var fail = results.Count - pass;
        var sb = new StringBuilder();
        var headEmoji = fail == 0 ? ":white_check_mark:" : ":rotating_light:";
        sb.AppendLine($"{headEmoji} **Self-test** — {pass} pass / {fail} fail");
        // Only show failing items inline (keep channel readable on the all-pass case).
        if (fail > 0)
        {
            foreach (var r in results.Where(r => !r.Pass))
            {
                sb.AppendLine($"  • **{r.Name}**: {r.Detail}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // -------------- Ping / latency log (#18) --------------
    //
    // Reads Valheim's built-in m_ping field on each peer's ZRpc via reflection and
    // tracks samples per session. Posts a "first ping" line shortly after a player
    // joins (proxy / VPN detection signal) and a session-average line on disconnect.

    public IEnumerator PingLogLoop()
    {
        yield return new WaitForSeconds(15f); // let ZNet settle so first reads are real

        while (true)
        {
            var interval = Math.Max(2, _settings?.PingLogSampleSeconds ?? 5);
            yield return new WaitForSeconds(interval);

            try
            {
                if (_settings == null || !_settings.EnablePingLog) continue;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) continue;
                TickPingLog();
            }
            catch (Exception ex)
            {
                LogS.LogWarning($"[ServerGuard] Ping log tick error: {ex.Message}");
            }
        }
    }

    private void TickPingLog()
    {
        var peers = ZNet.instance.GetPeers();
        if (peers == null) return;

        foreach (var peer in peers)
        {
            if (peer == null || peer.m_rpc == null) continue;

            var steamId = GetPeerPlatformId(peer);
            if (string.IsNullOrWhiteSpace(steamId)) continue;

            float pingMs = ReadPingMs(peer.m_rpc);
            if (pingMs <= 0 || pingMs > 10000) continue; // unreliable / not yet measured

            if (!_pingState.TryGetValue(peer.m_uid, out var s) || s == null)
            {
                s = new PingState();
                _pingState[peer.m_uid] = s;
            }

            s.Samples.Add(pingMs);
            if (s.Samples.Count > 200) s.Samples.RemoveAt(0); // cap memory

            if (!s.FirstPosted && s.Samples.Count >= 2)
            {
                // Skip the very first reading - it's often inflated by login traffic.
                // The second sample is usually steady-state.
                s.FirstPosted = true;
                PostAdminEvent($":satellite: **{FormatPlayer(steamId)}** first ping: **{pingMs:F0} ms**");
            }
        }
    }

    private static float ReadPingMs(object rpc)
    {
        if (rpc == null) return 0;
        try
        {
            if (_rpcPingField == null)
            {
                _rpcPingField = rpc.GetType().GetField("m_ping",
                    System.Reflection.BindingFlags.Instance
                  | System.Reflection.BindingFlags.NonPublic
                  | System.Reflection.BindingFlags.Public);
            }
            if (_rpcPingField == null) return 0;
            var v = _rpcPingField.GetValue(rpc);
            if (v is float f) return f * 1000f;       // seconds -> ms
            if (v is double d) return (float)(d * 1000.0);
        }
        catch { /* fall through */ }
        return 0;
    }

    // Called from Patch_Disconnect when a peer leaves. Posts session-avg ping if we
    // captured any samples.
    internal void FlushPingOnDisconnect(long peerUid, string steamId)
    {
        if (_settings == null || !_settings.EnablePingLog) return;
        if (!_pingState.TryGetValue(peerUid, out var s) || s == null) return;
        _pingState.Remove(peerUid);
        if (s.Samples.Count == 0) return;
        var avg = s.Samples.Sum() / s.Samples.Count;
        PostAdminEvent($":satellite: **{FormatPlayer(steamId)}** session ping avg: **{avg:F0} ms** ({s.Samples.Count} samples)");
    }

    // -------------- Movement-speed sanity check (#6) --------------
    //
    // Polls each connected peer's character ZDO at a fixed interval and computes the
    // 2D horizontal speed since the last sample. Vertical motion is ignored so that
    // falling/jumping doesn't false-positive. Big single-sample displacements (portal
    // travel, teleport stones) reset the strike counter instead of incrementing it,
    // because they're legitimate game mechanics that look identical to teleport hacks.
    //
    // Triggers SpeedHack only after N consecutive over-threshold samples - a single
    // lag spike or position correction won't flag anyone.

    public IEnumerator SpeedCheckLoop()
    {
        // Wait a moment for ZNet to come up so the very first tick doesn't no-op.
        yield return new WaitForSeconds(5f);

        while (true)
        {
            var interval = Mathf.Max(0.25f, (float)_settings.SpeedCheckSampleSeconds);
            yield return new WaitForSeconds(interval);

            try
            {
                if (!_settings.EnableSpeedCheck) continue;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) continue;
                TickSpeedCheck();
            }
            catch (Exception ex)
            {
                LogS.LogWarning($"[ServerGuard] Speed check tick error: {ex.Message}");
            }
        }
    }

    private void TickSpeedCheck()
    {
        var peers = ZNet.instance.GetPeers();
        if (peers == null) return;

        var now           = Time.realtimeSinceStartup;
        var maxSpeed      = (float)_settings.SpeedCheckMaxMetersPerSecond;
        var teleportTol   = (float)_settings.SpeedCheckTeleportToleranceMeters;
        var strikesNeeded = Math.Max(1, _settings.SpeedCheckConsecutiveStrikes);

        foreach (var peer in peers)
        {
            if (peer == null) continue;

            var steamId = GetPeerPlatformId(peer);
            if (string.IsNullOrWhiteSpace(steamId)) continue;
            if (IsAdmin(steamId)) continue;

            // Player character must be spawned. m_characterID is set after the client
            // sends RPC_CharacterID, which happens shortly after PeerInfo completes.
            ZDOID charId;
            try { charId = peer.m_characterID; }
            catch { continue; }
            if (charId == ZDOID.None) continue;

            var zdo = ZDOMan.instance?.GetZDO(charId);
            if (zdo == null) continue;

            Vector3 pos;
            try { pos = zdo.GetPosition(); }
            catch { continue; }

            if (!_speedState.TryGetValue(peer.m_uid, out var state) || state == null)
            {
                state = new SpeedState();
                _speedState[peer.m_uid] = state;
            }

            if (state.HasLastPos)
            {
                var dt = now - state.LastSampleTime;
                if (dt > 0.01f)
                {
                    // Horizontal-only distance: ignore vertical jumps/falls.
                    var dx = pos.x - state.LastPos.x;
                    var dz = pos.z - state.LastPos.z;
                    var dist  = (float)Math.Sqrt(dx * dx + dz * dz);
                    var speed = dist / dt;

                    if (dist > teleportTol)
                    {
                        // Legitimate teleport (portal, stone, /goto by admin earlier) -
                        // a single huge jump. Don't strike; just resync our baseline.
                        state.OverThresholdCount = 0;
                    }
                    else if (speed > maxSpeed)
                    {
                        state.OverThresholdCount++;
                        LogS.LogInfo(
                            $"[ServerGuard] Speed alert {FormatPlayer(steamId)}: " +
                            $"{speed:F1} m/s over {dt:F2}s ({state.OverThresholdCount}/{strikesNeeded})");

                        if (state.OverThresholdCount >= strikesNeeded)
                        {
                            var label = $"{speed:F1} m/s";
                            PostPlayerEvent(":runner:", steamId, "flagged for speed", $"~{label}");
                            AddViolation(steamId, RULE_SPEED_HACK, label);
                            state.OverThresholdCount = 0; // reset so we don't spam every tick
                        }
                    }
                    else
                    {
                        state.OverThresholdCount = 0;
                    }
                }
            }

            state.LastPos        = pos;
            state.LastSampleTime = now;
            state.HasLastPos     = true;
        }
    }

    // -------------- Inventory item validation (#7) --------------
    //
    // Harmony-patches Inventory.AddItem(ItemDrop.ItemData) server-side. The patch
    // only runs on the dedicated server; it inspects every item being added to any
    // Inventory (player inventories, containers, shrines, etc.) and flags entries
    // that are unknown to ObjectDB or whose stack exceeds the configured maximum.
    //
    // Caveats:
    //   * Client-side console commands (e.g. `spawn`) call AddItem locally and only
    //     sync via ZDO. The companion plugin's devcommands gate (#5) is the front
    //     line for those.
    //   * Modded items registered AFTER ObjectDB initialises may briefly look
    //     "unknown." We re-check ObjectDB.instance each call so post-init additions
    //     work, but startup-race items can still false-positive once.
    //   * Defaulted to log-only via InventoryCheckLogOnly = true. Switch to false to
    //     actively block the add.
    //
    // Returns a list of issue strings (empty = OK). Each string is short and
    // formatted for log + Discord display.
    internal List<string> ValidateInventoryItem(ItemDrop.ItemData item)
    {
        var issues = new List<string>();
        if (item == null || item.m_shared == null) return issues;

        var name = item.m_shared.m_name ?? "";
        var trimmed = name.TrimStart('$'); // localisation keys often start with $

        // ObjectDB lookup. Skip when ObjectDB isn't ready (very early boot).
        try
        {
            var odb = ObjectDB.instance;
            if (odb != null)
            {
                // Inventory.AddItem stores items by their m_shared.m_name (a localisation
                // key like "$item_sword"), not by prefab name. ObjectDB indexes by prefab
                // name. We search items list whose ItemDrop.m_itemData.m_shared.m_name
                // matches. Cache lookups in a hot path? Not worth it for this checking
                // frequency (only fires on AddItem calls, not per-tick).
                bool found = false;
                if (odb.m_items != null)
                {
                    foreach (var go in odb.m_items)
                    {
                        if (go == null) continue;
                        var drop = go.GetComponent<ItemDrop>();
                        if (drop == null || drop.m_itemData?.m_shared == null) continue;
                        if (string.Equals(drop.m_itemData.m_shared.m_name, name, StringComparison.Ordinal))
                        {
                            found = true; break;
                        }
                    }
                }
                if (!found)
                {
                    issues.Add($"unknown item '{trimmed}'");
                }
            }
        }
        catch { /* ObjectDB not ready or unexpected layout - skip */ }

        // Stack overflow.
        try
        {
            int maxStack = Math.Max(1, item.m_shared.m_maxStackSize);
            var tol = Math.Max(1.0, _settings.InventoryCheckStackTolerance);
            int allowed = (int)Math.Ceiling(maxStack * tol);
            if (item.m_stack > allowed)
            {
                issues.Add($"stack {item.m_stack} > max {maxStack} for '{trimmed}'");
            }
        }
        catch { }

        return issues;
    }

    // Patch fires for every Inventory.AddItem(ItemDrop.ItemData) call. We only do work
    // on the dedicated server side; clients run their own copy of the game but their
    // adds aren't authoritative for our purposes.
    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new Type[] { typeof(ItemDrop.ItemData) })]
    public static class Patch_Inventory_AddItem
    {
        public static bool Prefix(Inventory __instance, ItemDrop.ItemData item)
        {
            try
            {
                if (Plugin.Instance == null) return true;
                var s = Plugin.Instance._settings;
                if (s == null || !s.EnableInventoryCheck) return true;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return true;
                if (item == null || item.m_shared == null) return true;

                var issues = Plugin.Instance.ValidateInventoryItem(item);
                if (issues == null || issues.Count == 0) return true;

                foreach (var issue in issues)
                {
                    Plugin.LogS.LogWarning($"[ServerGuard] Inventory check: {issue} (logOnly={s.InventoryCheckLogOnly})");
                }

                // Pick the most specific rule to attribute to. If we couldn't even find
                // the item in ObjectDB, IllegalItem; otherwise StackOverflow.
                var rule   = issues[0].StartsWith("unknown item") ? RULE_ILLEGAL_ITEM : RULE_STACK_OVERFLOW;
                var detail = issues[0];

                // No reliable per-peer attribution at this seam (an Inventory isn't tied
                // to a specific peer). Record as anonymous so admins can correlate via
                // log timestamps. AddViolation tolerates empty platformId by treating
                // it as a no-op counter, so this is safe.
                Plugin.LogS.LogWarning($"[ServerGuard] {rule} - {detail}");

                // If logOnly is OFF, block the add by returning false.
                if (!s.InventoryCheckLogOnly) return false;
            }
            catch (Exception ex)
            {
                Plugin.LogS.LogWarning($"[ServerGuard] Inventory check error: {ex.Message}");
            }
            return true;
        }
    }

    // Sleeps until the configured UTC hour, posts, resets, loops.
    private IEnumerator DailySummaryLoop()
    {
        // Wait until the first scheduled fire time so the very first post is bounded
        // to "today's window" instead of dumping everything immediately on boot.
        var firstDelay = (float)SecondsUntilNextFire();
        yield return new WaitForSeconds(firstDelay);

        while (true)
        {
            try { _ = PostDailySummary(scheduled: true); }
            catch (Exception ex) { LogS.LogWarning($"[ServerGuard] Daily summary post failed: {ex.Message}"); }

            // Then a steady 24h cadence. Recompute against the wall clock each iteration
            // so server time drift doesn't accumulate.
            var nextDelay = (float)SecondsUntilNextFire();
            // Belt-and-suspenders: if computation is bogus, fall back to 24h.
            if (nextDelay <= 60f) nextDelay = 24 * 3600f;
            yield return new WaitForSeconds(nextDelay);
        }
    }

    private double SecondsUntilNextFire()
    {
        var hour = Math.Max(0, Math.Min(23, _settings.DailySummaryHourUtc));
        var now  = DateTime.UtcNow;
        var todayFire = new DateTime(now.Year, now.Month, now.Day, hour, 0, 0, DateTimeKind.Utc);
        var next = now < todayFire ? todayFire : todayFire.AddDays(1);
        return Math.Max(60.0, (next - now).TotalSeconds);
    }

    private async Task PostDailySummary(bool scheduled)
    {
        // Snapshot + reset under the lock so events arriving mid-flush count toward the next day.
        int joins, leaves, kicks, bans;
        DateTime since;
        List<KeyValuePair<string, int>> reasons;
        lock (_summaryLock)
        {
            joins   = _summaryJoins;
            leaves  = _summaryLeaves;
            kicks   = _summaryKicks;
            bans    = _summaryBans;
            since   = _summarySince;
            reasons = _summaryKickReasons
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .ToList();

            _summaryJoins  = 0;
            _summaryLeaves = 0;
            _summaryKicks  = 0;
            _summaryBans   = 0;
            _summaryKickReasons.Clear();
            _summarySince  = DateTime.UtcNow;
        }

        if (joins == 0 && leaves == 0 && kicks == 0 && bans == 0)
        {
            // Don't spam an empty channel; only the scheduled flush is allowed to skip silently.
            if (scheduled) return;
        }

        var window = $"{since:yyyy-MM-dd HH:mm} UTC → {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";
        var sb = new StringBuilder();
        sb.AppendLine(":bar_chart: **Daily summary**");
        sb.AppendLine(window);
        sb.AppendLine($"• Joins: **{joins}**");
        sb.AppendLine($"• Leaves: **{leaves}**");
        sb.AppendLine($"• Kicks: **{kicks}**");
        sb.AppendLine($"• Auto-bans: **{bans}**");
        if (reasons.Count > 0)
        {
            sb.AppendLine("Top kick reasons:");
            foreach (var kv in reasons)
            {
                sb.AppendLine($"  – {kv.Key} ({kv.Value})");
            }
        }

        var target = (_settings.DailySummaryChannel ?? "admin").ToLowerInvariant() switch
        {
            "public" => DiscordChannel.Public,
            "both"   => DiscordChannel.Both,
            _        => DiscordChannel.Admin,
        };
        await SendDiscordNow(sb.ToString(), target);
    }

    private void AddViolation(string platformId, string rule, string detail = null)
    {
        var counts = RuleCountsAsViolation(rule);
        var who = FormatPlayer(platformId);

        if (counts)
        {
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
            PostAdminEvent($":warning: **{who}** violated **{rule}** ({map[rule]}/{_settings.ViolationThreshold})"
                + (string.IsNullOrEmpty(detail) ? "" : $" — {detail}"));

            if (_settings.Enforce && map[rule] >= _settings.ViolationThreshold)
            {
                TryBan(platformId, _settings.BanReason);
                if (_settings.EnableMetrics)
                {
                    _metrics.players_banned++;
                    SaveMetrics();
                }
                PostPlayerEvent(":no_entry:", platformId, "was auto-banned", "too many strikes");
                PostAdminEvent($":no_entry: Auto-banned **{who}** (threshold reached)");
            }
        }
        else
        {
            LogS.LogWarning($"[ServerGuard] {who} hit rule '{rule}' (countAsViolation: false - no strike recorded).");
            // Informational rule (countAsViolation=false): still tell admins so they
            // can audit, but mark it informational.
            PostAdminEvent($":eye: **{who}** triggered **{rule}** (informational — not counted)"
                + (string.IsNullOrEmpty(detail) ? "" : $" — {detail}"));
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
            // Mark this peer so the disconnect-hook below doesn't also post a "left" event.
            try { lock (_suppressLogoutFor) { _suppressLogoutFor.Add(peer.m_uid); } } catch { }
            try
            {
                ZNet.instance.Disconnect(peer);
                LogS.LogWarning($"[ServerGuard] Disconnected {who}. Reason: {reason}");
                PostAdminEvent($":door: Disconnected **{who}** — {reason}");
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
                return;
            }

            var internalKick = znet.GetType().GetMethod("InternalKick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(ZNetPeer) }, null);
            if (internalKick != null)
            {
                internalKick.Invoke(znet, new object[] { peer });
                LogS.LogWarning($"[ServerGuard] InternalKick'd {who}. Reason: {reason}");
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
            }
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Ban failed: {ex}");
        }
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

                // Always register the per-peer RPC handlers BEFORE checking admin
                // status. The previous version returned early for admins and never
                // bound the handlers, which meant admins couldn't use `sg` console
                // commands - their companion's RPC arrived at a peer with no listener.
                //
                // 1. Manifest receiver - reply path for the attestation challenge.
                peer.m_rpc.Register<string>("ServerGuard_Manifest", (rpc, json) =>
                {
                    Plugin.Instance.OnManifestReceived(peer, json);
                });

                // Devcommands gate (#5).
                peer.m_rpc.Register<string>("ServerGuard_DevcommandAttempt", (rpc, command) =>
                {
                    Plugin.Instance.OnDevcommandAttemptReceived(peer, command);
                });

                // Animation-cancel gate.
                peer.m_rpc.Register<string>("ServerGuard_AnimationCancelAttempt", (rpc, source) =>
                {
                    Plugin.Instance.OnAnimationCancelReceived(peer, source);
                });

                // Skill-level cap (#10).
                peer.m_rpc.Register<string>("ServerGuard_SkillReport", (rpc, payload) =>
                {
                    Plugin.Instance.OnSkillReportReceived(peer, payload);
                });

                // Death log.
                peer.m_rpc.Register<string>("ServerGuard_PlayerDeath", (rpc, payload) =>
                {
                    Plugin.Instance.OnPlayerDeathReceived(peer, payload);
                });

                // Shout log: the companion reports outgoing shouts (chat can't be seen
                // server-side on current Valheim builds). Payload: "<type>|<text>".
                peer.m_rpc.Register<string>("ServerGuard_Chat", (rpc, payload) =>
                {
                    Plugin.Instance.OnChatReceived(peer, payload);
                });

                // Build log (#14): place events from the companion.
                peer.m_rpc.Register<string>("ServerGuard_BuildPlace", (rpc, payload) =>
                {
                    Plugin.Instance.OnBuildPlaceReceived(peer, payload);
                });

                // Build log (#14): destroy events from the companion. The companion's
                // WearNTear.Destroy patch only fires on the ZDO OWNER, which is usually
                // the client (player nearby). Server-side WearNTear patches catch the
                // server-owned cases (decay, fire). Together both paths cover all
                // destruction without double-logging since a single Destroy() call
                // only ever runs on one machine.
                peer.m_rpc.Register<string>("ServerGuard_BuildDestroy", (rpc, payload) =>
                {
                    Plugin.Instance.OnBuildDestroyReceived(peer, payload);
                });

                // Admin console commands (#16): companion forwards `sg ...` lines.
                // The handler validates IsAdmin before doing anything, so registering
                // it for every peer is safe.
                peer.m_rpc.Register<string>("ServerGuard_AdminCommand", (rpc, command) =>
                {
                    Plugin.Instance.OnAdminCommandReceived(peer, command);
                });

                if (Plugin.Instance.IsAdmin(pid))
                {
                    Plugin.LogS.LogInfo($"[ServerGuard] {Plugin.Instance.FormatPlayer(pid)} is admin - skipping attestation challenge.");
                    if (Plugin.Instance._settings.EnableMetrics)
                    {
                        Plugin.Instance._metrics.admin_bypasses++;
                        Plugin.Instance.SaveMetrics();
                    }
                    // Admins skip attestation, so the "joined" event from OnManifestReceived
                    // never fires for them. Fire it here so admins still show up in the
                    // admin channel (PostPlayerEvent routes admin events away from public).
                    Plugin.Instance.PostPlayerEvent(":shield:", pid, "joined as admin");
                    return;
                }

                if (Plugin.Instance._settings.EnableMetrics)
                {
                    Plugin.Instance._metrics.total_players_checked++;
                    Plugin.Instance.SaveMetrics();
                }

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

    // Fires whenever a peer's connection goes away — clean quit, alt-F4, network drop,
    // or our own TryKick. We post a friendly "left" event to Discord and update the
    // daily-summary counters, except when we ourselves just kicked the peer (in which
    // case the "was kicked" event already covers it).
    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    public static class Patch_Disconnect
    {
        public static void Prefix(ZNetPeer peer)
        {
            try
            {
                if (peer == null) return;
                if (!ZNet.instance || !ZNet.instance.IsServer()) return;
                if (Plugin.Instance == null) return;

                // Suppress if we initiated this disconnect ourselves.
                bool suppress;
                lock (Plugin.Instance._suppressLogoutFor)
                {
                    suppress = Plugin.Instance._suppressLogoutFor.Remove(peer.m_uid);
                }
                if (suppress) return;

                // Drop any pending attestation slot so we don't keep dead state around.
                lock (Plugin.Instance._pendingLock)
                {
                    Plugin.Instance._pending.Remove(peer.m_uid);
                }

                // Drop speed-check baseline; a fresh login should start a fresh window.
                Plugin.Instance._speedState.Remove(peer.m_uid);

                // Drop skill-overflow throttle state.
                Plugin.Instance._skillOverflowState.Remove(peer.m_uid);

                var steamId = Plugin.GetPeerPlatformId(peer);
                if (string.IsNullOrWhiteSpace(steamId)) return;

                var who = Plugin.Instance.FormatPlayer(steamId);
                Plugin.LogS.LogInfo($"[ServerGuard] {who} left the server.");
                Plugin.Instance.PostPlayerEvent(":wave:", steamId, "left");

                // Flush session-avg ping line (#18) to admin channel (if enabled).
                Plugin.Instance.FlushPingOnDisconnect(peer.m_uid, steamId);
            }
            catch (Exception ex)
            {
                Plugin.LogS.LogWarning($"[ServerGuard] Disconnect hook error: {ex.Message}");
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

				if (Plugin.Instance.IsAdmin(steamId)) return;

				if (!Plugin.Instance._registrations.TryGetValue(steamId, out var names) || names == null)
				{
					names = new List<string>();
					Plugin.Instance._registrations[steamId] = names;
				}

				if (names.Any(n => string.Equals(n, charName, StringComparison.Ordinal)))
				{
					Plugin.Instance.SendCheatItemRemovalIfEnabled(peer);
					return;
				}

				int limit = Math.Max(1, Plugin.Instance._settings.CharacterLimit);
				if (names.Count < limit)
				{
					names.Add(charName);
					Plugin.Instance.SaveRegistrations();
					Plugin.LogS.LogInfo($"[ServerGuard] Registered character #{names.Count}/{limit} for {Plugin.Instance.FormatPlayer(steamId)} -> '{charName}'");
					Plugin.Instance.SendCheatItemRemovalIfEnabled(peer);
				}
				else
				{
					Plugin.Instance.AddViolation(steamId, RULE_CHAR_NAME_LIMIT, charName);
					if (Plugin.Instance._settings.Enforce)
					{
						Plugin.Instance.PostPlayerEvent(":door:", steamId, "was kicked", FriendlyReason(RULE_CHAR_NAME_LIMIT));
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

        if (_settings.RequireCompanion)
        {
            AddViolation(steamId, RULE_COMPANION_MISSING);
            if (_settings.Enforce)
            {
                PostPlayerEvent(":door:", steamId, "was kicked", FriendlyReason(RULE_COMPANION_MISSING));
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
                if (_settings.Enforce)
                {
                    PostPlayerEvent(":door:", steamId, "was kicked", FriendlyReason(RULE_HMAC_INVALID));
                    TryKick(peer, $"{_settings.KickMessage} (Malformed manifest)");
                }
                return;
            }
            if (manifest == null)
            {
                LogS.LogWarning($"[ServerGuard] Empty manifest from {who}.");
                AddViolation(steamId, RULE_HMAC_INVALID);
                if (_settings.Enforce)
                {
                    PostPlayerEvent(":door:", steamId, "was kicked", FriendlyReason(RULE_HMAC_INVALID));
                    TryKick(peer, $"{_settings.KickMessage} (Empty manifest)");
                }
                return;
            }

            // 1. Challenge match (defeats cross-peer / cross-session replay).
            if (!ModManifest.ConstantTimeEquals(manifest.Challenge ?? "", pending.Challenge ?? ""))
            {
                LogS.LogWarning($"[ServerGuard] Challenge mismatch from {who}.");
                AddViolation(steamId, RULE_CHALLENGE_MISMATCH);
                if (_settings.Enforce)
                {
                    PostPlayerEvent(":door:", steamId, "was kicked", FriendlyReason(RULE_CHALLENGE_MISMATCH));
                    TryKick(peer, $"{_settings.KickMessage} (Challenge mismatch)");
                }
                return;
            }

            // 2. Timestamp window.
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (Math.Abs(nowUnix - manifest.TimestampUtc) > Math.Max(10, _settings.MaxClockSkewSeconds))
            {
                LogS.LogWarning($"[ServerGuard] Timestamp out of window for {who} (client={manifest.TimestampUtc} server={nowUnix}).");
                AddViolation(steamId, RULE_HMAC_INVALID);
                if (_settings.Enforce)
                {
                    PostPlayerEvent(":door:", steamId, "was kicked", "system clock too far off");
                    TryKick(peer, $"{_settings.KickMessage} (Clock skew exceeds policy)");
                }
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
                    if (_settings.Enforce)
                    {
                        PostPlayerEvent(":door:", steamId, "was kicked", FriendlyReason(RULE_HMAC_INVALID));
                        TryKick(peer, $"{_settings.KickMessage} (Invalid signature)");
                    }
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
                AddViolation(steamId, verdict.Rule, verdict.Detail);
                if (_settings.Enforce)
                {
                    PostPlayerEvent(":door:", steamId, "was kicked", FriendlyReason(verdict.Rule, verdict.Detail));
                    TryKick(peer, $"{_settings.KickMessage} ({verdict.Reason})");
                }
                return;
            }

            // Compute the client's modset fingerprint over the manifest it just sent and
            // compare against the server's. Two indicators: loose (same set of mod keys)
            // and strict (also same binaries). The verdict is purely observational here -
            // an unlisted mod would already have been rejected above; we're just labelling
            // "this player is on the exact curated modpack" vs. "this player has the right
            // mods but a different version" vs. "matches loosely but binaries differ."
            string fpStatus;
            try
            {
                var pairs = (manifest.Mods ?? new List<ModManifestEntry>())
                    .Select(m => new KeyValuePair<string, string>(
                        !string.IsNullOrEmpty(m?.Guid) ? m.Guid : (m?.Name ?? ""),
                        m?.Sha256 ?? ""))
                    .ToList();
                var clientLoose  = ModsetFingerprint.ComputeLoose(pairs);
                var clientStrict = ModsetFingerprint.ComputeStrict(pairs);

                bool looseMatch  = ModManifest.ConstantTimeEquals(clientLoose,  _modsetFingerprintLoose);
                bool strictMatch = ModManifest.ConstantTimeEquals(clientStrict, _modsetFingerprintStrict);

                if (strictMatch)      fpStatus = $"modset ✓ exact ({ModsetFingerprint.Short(clientStrict)})";
                else if (looseMatch)  fpStatus = $"modset ✓ same set, different versions (loose {ModsetFingerprint.Short(clientLoose)})";
                else                  fpStatus = $"modset ⚠ differs from server (client {ModsetFingerprint.Short(clientLoose)} vs server {ModsetFingerprint.Short(_modsetFingerprintLoose)})";
            }
            catch (Exception fpEx)
            {
                fpStatus = $"modset fingerprint check failed: {fpEx.Message}";
            }

            LogS.LogInfo($"[ServerGuard] {who} attested OK ({manifest.Mods?.Count ?? 0} mods, {fpStatus}).");
            PostPlayerEvent(":white_check_mark:", steamId, "joined");
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnManifestReceived error for {FormatPlayer(steamId)}: {ex}");
        }
    }

    // Per-peer state for SkillOverflow violation throttling. The companion reports
    // every 60s; without throttling, a stuck-over-cap skill would fire a violation on
    // every report. We log/post/violate only when (a) the level CROSSES the threshold
    // (was-under -> now-over) or (b) the level KEEPS GROWING above the threshold.
    private class SkillOverflowState
    {
        // skill name -> highest level seen so far. Used to detect new overflows vs.
        // already-reported overflows that haven't changed.
        public Dictionary<string, double> LastReportedLevel = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
    }
    private readonly Dictionary<long, SkillOverflowState> _skillOverflowState = new Dictionary<long, SkillOverflowState>();

    // Handles ServerGuard_SkillReport RPC. The companion plugin sends a pipe-separated
    // snapshot of the local player's skills every ~60s: "Swords:78.5|Bows:42.1|...".
    // We parse and flag any entry whose level exceeds the configured cap + tolerance.
    public void OnSkillReportReceived(ZNetPeer peer, string payload)
    {
        try
        {
            if (peer == null) return;
            if (_settings == null || !_settings.EnableSkillCap) return;
            if (string.IsNullOrWhiteSpace(payload)) return;

            var steamId = GetPeerPlatformId(peer);
            if (IsAdmin(steamId)) return;

            var maxAllowed = _settings.SkillCapMaxLevel + _settings.SkillCapTolerance;
            var who = FormatPlayer(steamId);

            if (!_skillOverflowState.TryGetValue(peer.m_uid, out var state) || state == null)
            {
                state = new SkillOverflowState();
                _skillOverflowState[peer.m_uid] = state;
            }

            // Payload format: skill1:level1|skill2:level2|...
            // Skill names with colons or pipes would break this, so we cap entry length
            // and reject malformed entries silently. Level uses invariant culture decimal.
            var entries = payload.Split('|');
            foreach (var raw in entries)
            {
                var entry = raw?.Trim();
                if (string.IsNullOrEmpty(entry) || entry.Length > 64) continue;

                var idx = entry.IndexOf(':');
                if (idx <= 0 || idx == entry.Length - 1) continue;

                var skill   = entry.Substring(0, idx).Trim();
                var levelStr = entry.Substring(idx + 1).Trim();
                if (string.IsNullOrEmpty(skill) || skill.Length > 32) continue;

                if (!double.TryParse(levelStr, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var level))
                {
                    continue;
                }

                if (level <= maxAllowed)
                {
                    // Skill is under the cap; clear any prior overflow record so a later
                    // ride back over the line fires fresh.
                    state.LastReportedLevel.Remove(skill);
                    continue;
                }

                // Suppress repeated violations on the same skill at the same level.
                if (state.LastReportedLevel.TryGetValue(skill, out var prevLevel)
                    && Math.Abs(prevLevel - level) < 0.05)
                {
                    continue;
                }
                state.LastReportedLevel[skill] = level;

                var detail = $"{skill}={level:F1}";
                LogS.LogWarning($"[ServerGuard] {who} skill cap exceeded: {detail} (cap {maxAllowed:F1})");
                PostPlayerEvent(":books:", steamId, "exceeded skill cap", detail);
                AddViolation(steamId, RULE_SKILL_OVERFLOW, detail);
            }
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnSkillReportReceived error: {ex}");
        }
    }

    // -------------- Build / destroy heatmap (#14) --------------
    //
    // Place events come from the companion (it patches Player.PlacePiece and sends the
    // ServerGuard_BuildPlace RPC). Destroy events are detected purely server-side: we
    // patch WearNTear.Damage to remember each piece's last hit, then WearNTear.Destroy
    // to log the destroy with that attacker.
    //
    // ConditionalWeakTable lets us key a "last hit" record on the WearNTear instance
    // itself; when the instance is GC'd (piece removed), the entry is auto-cleared.
    // No manual cleanup needed and no instance-id reuse collisions.

    private sealed class LastHitBox
    {
        public ZDOID Attacker;        // for player peer lookup
        public string AttackerKind;   // "player" | "creature" | ""
        public string AttackerName;   // hover / prefab name (e.g. "Troll") for non-player attribution
        public DateTime At;
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<WearNTear, LastHitBox> _lastHitOnPiece
        = new System.Runtime.CompilerServices.ConditionalWeakTable<WearNTear, LastHitBox>();

    // Cached reflection handle for HitData.m_attacker (which may be public OR internal
    // depending on Valheim build).
    private static System.Reflection.FieldInfo _hitAttackerField;
    private static ZDOID? GetHitAttacker(HitData hit)
    {
        if (hit == null) return null;
        if (_hitAttackerField == null)
        {
            _hitAttackerField = typeof(HitData).GetField("m_attacker",
                System.Reflection.BindingFlags.Instance
              | System.Reflection.BindingFlags.Public
              | System.Reflection.BindingFlags.NonPublic);
        }
        if (_hitAttackerField == null) return null;
        try { return (ZDOID)_hitAttackerField.GetValue(hit); }
        catch { return null; }
    }

    // Appends one row to today's CSV file. Creates the file (with header) on first
    // write of the day.
    private void LogBuildEvent(string action, string steamId, string charName, string pieceName, Vector3 pos)
    {
        try
        {
            if (_settings == null || !_settings.EnableBuildLog) return;
            Directory.CreateDirectory(BuildLogDir);

            var dateStamp = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(BuildLogDir, $"{dateStamp}.csv");

            var fresh = !File.Exists(path);
            using (var sw = new StreamWriter(path, append: true, Encoding.UTF8))
            {
                if (fresh)
                {
                    sw.WriteLine("timestamp,action,steamId,charName,pieceName,x,y,z");
                }
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", inv);
                // Quote char name to survive commas / special chars. Other fields are
                // safe identifiers / numbers.
                var cn = (charName ?? "").Replace("\"", "\"\"");
                sw.WriteLine(string.Format(inv,
                    "{0},{1},{2},\"{3}\",{4},{5:F1},{6:F1},{7:F1}",
                    ts, action, steamId ?? "", cn, pieceName ?? "", pos.x, pos.y, pos.z));
            }
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] LogBuildEvent failed: {ex.Message}");
        }
    }

    // Payload format from client companion:
    //   prefab | x | y | z [ | attackerKind | attackerLabel ]
    //
    // attackerKind: "player" | "creature" | "self" | "unknown" | ""
    // attackerLabel: display name (creature hover name OR player character name)
    //
    // The optional kind+label fields let the companion tell us when a CREATURE was
    // the destroyer of a client-owned piece (e.g., Troll raid on a player base).
    // For backward compat with older client builds, the trailing pair is optional;
    // if absent, the destroyer defaults to the RPC sender (= local player).
    public void OnBuildDestroyReceived(ZNetPeer peer, string payload)
    {
        try
        {
            if (peer == null) return;
            if (_settings == null || !_settings.EnableBuildLog) return;
            if (string.IsNullOrWhiteSpace(payload)) return;

            var parts = payload.Split('|');
            if (parts.Length < 4) return;

            var pieceName = (parts[0] ?? "").Trim();
            if (pieceName.Length > 64) pieceName = pieceName.Substring(0, 64);

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out var x)) return;
            if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out var y)) return;
            if (!float.TryParse(parts[3], System.Globalization.NumberStyles.Float, inv, out var z)) return;

            var attackerKind  = parts.Length >= 5 ? (parts[4] ?? "").Trim().ToLowerInvariant() : "";
            var attackerLabel = parts.Length >= 6 ? (parts[5] ?? "").Trim() : "";
            if (attackerLabel.Length > 48) attackerLabel = attackerLabel.Substring(0, 48);

            string steamId, charName;
            if (attackerKind == "creature")
            {
                // Creature destroyed it. Empty steamId signals "non-player" to CSV
                // consumers; charName carries the creature display name.
                steamId  = "";
                charName = attackerLabel;
            }
            else if (attackerKind == "player" && !string.IsNullOrEmpty(attackerLabel))
            {
                // A DIFFERENT player destroyed it on the reporting client's machine
                // (raid scenario). Try to resolve their SteamID from registrations.
                var sid = LookupSteamIdByCharName(attackerLabel);
                steamId  = sid ?? "";
                charName = attackerLabel;
            }
            else
            {
                // attackerKind in { "self", "unknown", "", null }: attribute to the
                // RPC sender (= the local player). Hammer-remove and "I broke my own
                // wall with my axe" both land here.
                steamId = GetPeerPlatformId(peer);
                charName = "";
                if (_registrations != null && _registrations.TryGetValue(steamId, out var names) && names != null && names.Count > 0)
                    charName = names[0];
            }

            LogBuildEvent("destroy", steamId, charName, pieceName, new Vector3(x, y, z));
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnBuildDestroyReceived error: {ex}");
        }
    }

    public void OnBuildPlaceReceived(ZNetPeer peer, string payload)
    {
        try
        {
            if (peer == null) return;
            if (_settings == null || !_settings.EnableBuildLog) return;
            if (string.IsNullOrWhiteSpace(payload)) return;

            var parts = payload.Split('|');
            if (parts.Length < 4) return;

            var pieceName = (parts[0] ?? "").Trim();
            if (pieceName.Length > 64) pieceName = pieceName.Substring(0, 64);

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out var x)) return;
            if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out var y)) return;
            if (!float.TryParse(parts[3], System.Globalization.NumberStyles.Float, inv, out var z)) return;

            var steamId = GetPeerPlatformId(peer);
            var charName = "";
            if (_registrations != null && _registrations.TryGetValue(steamId, out var names) && names != null && names.Count > 0)
                charName = names[0];

            LogBuildEvent("place", steamId, charName, pieceName, new Vector3(x, y, z));
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnBuildPlaceReceived error: {ex}");
        }
    }

    public IEnumerator BuildLogCleanupLoop()
    {
        // First run after 5 minutes (so a freshly-booted server doesn't churn the disk
        // before the operator has logged in).
        yield return new WaitForSeconds(300f);
        while (true)
        {
            try { PruneOldBuildLogs(); }
            catch (Exception ex) { LogS.LogWarning($"[ServerGuard] PruneOldBuildLogs failed: {ex.Message}"); }
            // Re-run hourly.
            yield return new WaitForSeconds(3600f);
        }
    }

    private void PruneOldBuildLogs()
    {
        if (_settings == null || !_settings.EnableBuildLog) return;
        if (!Directory.Exists(BuildLogDir)) return;

        var retain = Math.Max(1, _settings.BuildLogRetentionDays);
        var cutoff = DateTime.UtcNow.AddDays(-retain);

        foreach (var file in Directory.GetFiles(BuildLogDir, "*.csv"))
        {
            try
            {
                var fi = new FileInfo(file);
                if (fi.LastWriteTimeUtc < cutoff)
                {
                    fi.Delete();
                    LogS.LogInfo($"[ServerGuard] Pruned old build log {fi.Name} (older than {retain}d).");
                }
            }
            catch { /* skip locked / vanished files */ }
        }
    }

    // Helper: derive a short display name from a Character. Returns the localized
    // hover name (e.g. "Troll", "Bonemass") when available; falls back to the
    // GameObject's prefab name with "(Clone)" stripped.
    private static string GetCharacterDisplayName(Character c)
    {
        if (c == null) return "";
        try
        {
            var hover = c.GetHoverName();
            if (!string.IsNullOrWhiteSpace(hover)) return hover;
        }
        catch { /* fall through */ }
        var raw = c.gameObject?.name ?? "";
        var idx = raw.IndexOf("(Clone)", StringComparison.Ordinal);
        if (idx > 0) raw = raw.Substring(0, idx).Trim();
        return raw;
    }

    // Patches: capture last hit per WearNTear so Destroy can attribute it.
    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Damage))]
    public static class Patch_WearNTear_Damage_Track
    {
        public static void Prefix(WearNTear __instance, HitData hit)
        {
            try
            {
                if (Plugin.Instance == null) return;
                if (Plugin.Instance._settings == null || !Plugin.Instance._settings.EnableBuildLog) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (__instance == null || hit == null) return;

                var attackerZdoid = GetHitAttacker(hit) ?? ZDOID.None;

                // Try to resolve the Character to capture its display name. On the
                // server this works because the world is loaded. For creatures we get
                // "Troll" / "Bonemass" / etc.; for players we get their character name.
                string attackerKind = "";
                string attackerName = "";
                try
                {
                    var ch = hit.GetAttacker();
                    if (ch != null)
                    {
                        if (ch is Player)
                        {
                            attackerKind = "player";
                            attackerName = GetCharacterDisplayName(ch);
                        }
                        else
                        {
                            attackerKind = "creature";
                            attackerName = GetCharacterDisplayName(ch);
                        }
                    }
                }
                catch { /* attacker may not resolve; that's fine */ }

                var box = new LastHitBox
                {
                    Attacker     = attackerZdoid,
                    AttackerKind = attackerKind,
                    AttackerName = attackerName,
                    At           = DateTime.UtcNow,
                };
                Plugin.Instance._lastHitOnPiece.Remove(__instance);
                Plugin.Instance._lastHitOnPiece.Add(__instance, box);
            }
            catch { /* never let the hook throw into Valheim */ }
        }
    }

    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Destroy))]
    public static class Patch_WearNTear_Destroy_Log
    {
        public static void Prefix(WearNTear __instance)
        {
            try
            {
                if (Plugin.Instance == null) return;
                if (Plugin.Instance._settings == null || !Plugin.Instance._settings.EnableBuildLog) return;
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (__instance == null) return;

                var pieceName = __instance.gameObject?.name ?? "unknown";
                // Strip Unity's "(Clone)" suffix for cleaner CSV entries.
                var cloneIdx = pieceName.IndexOf("(Clone)", StringComparison.Ordinal);
                if (cloneIdx > 0) pieceName = pieceName.Substring(0, cloneIdx).Trim();

                Vector3 pos;
                try { pos = __instance.transform.position; }
                catch { pos = Vector3.zero; }

                string destroyerSteamId = "";
                string destroyerName    = "";

                if (Plugin.Instance._lastHitOnPiece.TryGetValue(__instance, out var box) && box != null)
                {
                    Plugin.Instance._lastHitOnPiece.Remove(__instance);

                    // Step 1: try to resolve a connected player peer by the attacker
                    // ZDOID. Works for PvP-style "player breaks server-owned piece".
                    if (box.Attacker != ZDOID.None)
                    {
                        var peers = ZNet.instance.GetPeers();
                        if (peers != null)
                        {
                            foreach (var p in peers)
                            {
                                if (p == null) continue;
                                if (p.m_characterID == box.Attacker)
                                {
                                    destroyerSteamId = Plugin.GetPeerPlatformId(p);
                                    if (Plugin.Instance._registrations != null
                                        && Plugin.Instance._registrations.TryGetValue(destroyerSteamId, out var names)
                                        && names != null && names.Count > 0)
                                    {
                                        destroyerName = names[0];
                                    }
                                    break;
                                }
                            }
                        }
                    }

                    // Step 2: peer lookup failed - fall back to the captured attacker
                    // name. For creatures this records "Troll" / "Bonemass" / etc. in
                    // the charName column, with steamId left empty so admins can
                    // distinguish "destroyed by player" vs "destroyed by creature"
                    // by inspecting whether steamId is set.
                    if (string.IsNullOrEmpty(destroyerSteamId) && string.IsNullOrEmpty(destroyerName)
                        && !string.IsNullOrEmpty(box.AttackerName))
                    {
                        destroyerName = box.AttackerName;
                    }
                }

                Plugin.Instance.LogBuildEvent("destroy", destroyerSteamId, destroyerName, pieceName, pos);
            }
            catch (Exception ex)
            {
                Plugin.LogS?.LogWarning($"[ServerGuard] WearNTear.Destroy hook error: {ex.Message}");
            }
        }
    }

    // -------------- Admin chat commands (#16) --------------
    //
    // The companion plugin intercepts in-game chat lines starting with `/sg` and
    // sends the raw text via ServerGuard_AdminCommand. The server:
    //   1. Verifies the sender is in admins.yaml (defense against forged RPCs).
    //   2. Parses the command and dispatches to a handler.
    //   3. Builds a reply string (multi-line, \n-separated).
    //   4. Sends the reply via ServerGuard_AdminCommandReply back to the same peer.
    //
    // The companion displays the reply lines in the admin's local chat window only,
    // so command output is not broadcast to other players.

    private const int AdminReplyMaxLines = 25;        // safety cap for reply length
    private const int BuildQueryDefaultDays = 7;      // how many days of CSVs to scan by default
    private const int BuildQueryMaxResults = 20;      // max rows returned to chat

    public void OnAdminCommandReceived(ZNetPeer peer, string command)
    {
        try
        {
            if (peer == null) return;
            var senderSteamId = GetPeerPlatformId(peer);
            if (string.IsNullOrWhiteSpace(senderSteamId)) return;

            if (!IsAdmin(senderSteamId))
            {
                LogS.LogWarning($"[ServerGuard] Non-admin {FormatPlayer(senderSteamId)} attempted sg command - ignored.");
                ReplyToAdmin(peer, "You are not an admin. Add your SteamID to admins.yaml on the server.");
                return;
            }

            LogS.LogInfo($"[ServerGuard] Admin command from {FormatPlayer(senderSteamId)}: {command}");

            // Audit trail for admins on the admin channel. Only post for commands
            // that have side effects (mutating commands) - skip read-only queries to
            // keep the channel readable.
            var firstToken = (command ?? "").TrimStart().Split(' ').FirstOrDefault()?.ToLowerInvariant() ?? "";
            var mutating = firstToken == "reload" || firstToken == "pardon" || firstToken == "kick";
            if (mutating)
            {
                PostAdminEvent($":hammer_and_wrench: **{FormatPlayer(senderSteamId)}** ran `sg {command}`");
            }

            var reply = DispatchAdminCommand(command, senderSteamId);
            ReplyToAdmin(peer, reply);
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnAdminCommandReceived error: {ex}");
            try { ReplyToAdmin(peer, $"Error: {ex.Message}"); } catch { }
        }
    }

    private void ReplyToAdmin(ZNetPeer peer, string text)
    {
        if (peer?.m_rpc == null) return;
        try
        {
            peer.m_rpc.Invoke("ServerGuard_AdminCommandReply", text ?? "");
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] Admin reply send failed: {ex.Message}");
        }
    }

    private string DispatchAdminCommand(string raw, string callerSteamId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return CmdHelp();
        var tokens = raw.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return CmdHelp();

        var cmd = tokens[0].ToLowerInvariant();
        var args = tokens.Skip(1).ToArray();

        switch (cmd)
        {
            case "help":       return CmdHelp();
            case "status":     return CmdStatus();
            case "reload":     return CmdReload();
            case "modset":     return CmdModset();
            case "whois":      return CmdWhois(args);
            case "violations": return CmdViolations(args);
            case "pardon":     return CmdPardon(args);
            case "kick":       return CmdKick(args, callerSteamId);
            case "build":      return CmdBuild(args, actionFilter: null,      label: "event");
            case "destroyed":  return CmdBuild(args, actionFilter: "destroy", label: "destroy");
            case "placed":     return CmdBuild(args, actionFilter: "place",   label: "placement");
            case "selftest":   return CmdSelfTest();
            default:           return $"Unknown command `{cmd}`. Try `sg help`.";
        }
    }

    private string CmdHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[ServerGuard] commands (type in the F5 console):");
        sb.AppendLine("  sg status                                - quick health check");
        sb.AppendLine("  sg selftest                              - run the boot-time smoke tests on demand");
        sb.AppendLine("  sg reload                                - reload settings/admins/allowed_mods");
        sb.AppendLine("  sg modset                                - show modset fingerprint");
        sb.AppendLine("  sg whois <steamid|name>                  - player info + recent violations");
        sb.AppendLine("  sg violations [<n>]                      - top N players by violation count");
        sb.AppendLine("  sg pardon <steamid>                      - clear a player's violations");
        sb.AppendLine("  sg kick <steamid> [reason]               - kick a player");
        sb.AppendLine("  sg build at <x> <z> [radius] [days]      - all events near coords (radius 50, days 7)");
        sb.AppendLine("  sg build by <steamid|name> [days]        - all events by a player");
        sb.AppendLine("  sg build today [<n>]                     - last N events today (default 10)");
        sb.AppendLine("  sg destroyed at <x> <z> [radius] [days]  - DESTROYS only, near coords");
        sb.AppendLine("  sg destroyed by <steamid|name> [days]    - DESTROYS only, by a player");
        sb.AppendLine("  sg destroyed today [<n>]                 - last N DESTROYS today");
        return sb.ToString().TrimEnd();
    }

    private string CmdStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[ServerGuard] v1.6.0  enforce={_settings.Enforce}  requireCompanion={_settings.RequireCompanion}  requireHmac={_settings.RequireHmac}");
        sb.AppendLine($"  Allowlist  required={_requiredMods.Count}  allowed={_allowedMods.Count}  banned={_bannedMods.Count}");
        sb.AppendLine($"  Modset     loose={ModsetFingerprint.Short(_modsetFingerprintLoose)}  strict={ModsetFingerprint.Short(_modsetFingerprintStrict)}");
        try
        {
            var peers = ZNet.instance?.GetPeers();
            sb.AppendLine($"  Online     {(peers?.Count ?? 0)} peer(s)");
        }
        catch { }
        sb.AppendLine($"  Violators  {_violations.Count} player(s) with strikes recorded");
        return sb.ToString().TrimEnd();
    }

    private string CmdSelfTest()
    {
        try
        {
            var results = RunSelfTest();
            LogS.LogInfo(FormatSelfTestReport(results));
            // Also push a Discord summary on FAIL (or always if SelfTestPostOnPass).
            var anyFail = results.Any(r => !r.Pass);
            if (anyFail || _settings.SelfTestPostOnPass)
                PostAdminEvent(FormatSelfTestForDiscord(results));
            return FormatSelfTestReport(results);
        }
        catch (Exception ex)
        {
            return $"Self-test crashed: {ex.Message}";
        }
    }

    private string CmdReload()
    {
        try
        {
            LoadSettings();
            LoadAdmins();
            LoadAllowedMods();
            return "[ServerGuard] reloaded settings.yaml, admins.yaml, allowed_mods.yaml.";
        }
        catch (Exception ex)
        {
            return $"Reload failed: {ex.Message}";
        }
    }

    private string CmdModset()
    {
        return
            $"[ServerGuard] modset fingerprint\n" +
            $"  loose  : {_modsetFingerprintLoose}\n" +
            $"  strict : {_modsetFingerprintStrict}\n" +
            $"  short  : loose={ModsetFingerprint.Short(_modsetFingerprintLoose)}  strict={ModsetFingerprint.Short(_modsetFingerprintStrict)}";
    }

    private string CmdWhois(string[] args)
    {
        if (args.Length == 0) return "Usage: sg whois <steamid|name>";
        var query = args[0];
        var resolved = ResolvePlayerQuery(query);
        if (resolved.Count == 0) return $"No SteamID matched `{query}`.";

        var sb = new StringBuilder();
        foreach (var steamId in resolved)
        {
            sb.AppendLine($"  {FormatPlayer(steamId)}");
            if (_violations.TryGetValue(steamId, out var map) && map != null && map.Count > 0)
            {
                foreach (var kv in map.OrderByDescending(kv => kv.Value))
                {
                    var counts = RuleCountsAsViolation(kv.Key) ? "" : " (informational)";
                    sb.AppendLine($"      {kv.Key,-28}  {kv.Value}/{_settings.ViolationThreshold}{counts}");
                }
            }
            else
            {
                sb.AppendLine("      (no recorded violations)");
            }

            // Mark admin status + currently-online status
            var online = false;
            try
            {
                foreach (var p in ZNet.instance?.GetPeers() ?? new List<ZNetPeer>())
                {
                    if (p != null && string.Equals(GetPeerPlatformId(p), steamId, StringComparison.Ordinal))
                    {
                        online = true;
                        break;
                    }
                }
            }
            catch { }
            sb.AppendLine($"      online={(online ? "yes" : "no")}  admin={(IsAdmin(steamId) ? "yes" : "no")}");
        }
        return sb.ToString().TrimEnd();
    }

    // Given a token, return all SteamIDs that match. Token can be a literal SteamID
    // (returned as-is), or a character-name substring (matched against registrations).
    private List<string> ResolvePlayerQuery(string query)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return result;
        query = query.Trim();

        // Numeric -> treat as SteamID literal.
        if (query.All(char.IsDigit) && query.Length >= 8)
        {
            result.Add(query);
            return result;
        }

        // Otherwise: scan registrations for character-name containing the substring.
        if (_registrations == null) return result;
        var qLower = query.ToLowerInvariant();
        foreach (var kv in _registrations)
        {
            if (kv.Value == null) continue;
            foreach (var n in kv.Value)
            {
                if (n != null && n.ToLowerInvariant().Contains(qLower))
                {
                    if (!result.Contains(kv.Key)) result.Add(kv.Key);
                    break;
                }
            }
        }
        return result;
    }

    private string CmdViolations(string[] args)
    {
        int n = 10;
        if (args.Length > 0 && int.TryParse(args[0], out var parsed) && parsed > 0) n = Math.Min(parsed, AdminReplyMaxLines);

        if (_violations.Count == 0) return "No recorded violations.";

        var ranked = _violations
            .Select(kv => new
            {
                SteamId = kv.Key,
                Total   = kv.Value?.Values.Sum() ?? 0,
                Counted = kv.Value?.Where(x => RuleCountsAsViolation(x.Key)).Sum(x => x.Value) ?? 0
            })
            .Where(x => x.Total > 0)
            .OrderByDescending(x => x.Counted)
            .ThenByDescending(x => x.Total)
            .Take(n)
            .ToList();

        if (ranked.Count == 0) return "No recorded violations.";

        var sb = new StringBuilder();
        sb.AppendLine($"[ServerGuard] top {ranked.Count} violators:");
        foreach (var r in ranked)
        {
            sb.AppendLine($"  {FormatPlayer(r.SteamId),-50}  counted={r.Counted}  total={r.Total}");
        }
        return sb.ToString().TrimEnd();
    }

    private string CmdPardon(string[] args)
    {
        if (args.Length == 0) return "Usage: sg pardon <steamid>";
        var resolved = ResolvePlayerQuery(args[0]);
        if (resolved.Count == 0) return $"No SteamID matched `{args[0]}`.";
        if (resolved.Count > 1) return $"Ambiguous - {resolved.Count} players match. Pass an exact SteamID.";

        var steamId = resolved[0];
        if (!_violations.TryGetValue(steamId, out var map) || map == null || map.Count == 0)
        {
            return $"{FormatPlayer(steamId)} had no recorded violations.";
        }
        var total = map.Values.Sum();
        _violations.Remove(steamId);
        SaveViolations();
        return $"Cleared {total} violation entries for {FormatPlayer(steamId)}.";
    }

    private string CmdKick(string[] args, string callerSteamId)
    {
        if (args.Length == 0) return "Usage: sg kick <steamid> [reason]";
        var resolved = ResolvePlayerQuery(args[0]);
        if (resolved.Count == 0) return $"No SteamID matched `{args[0]}`.";
        if (resolved.Count > 1) return $"Ambiguous - {resolved.Count} players match. Pass an exact SteamID.";

        var targetSteamId = resolved[0];
        if (string.Equals(targetSteamId, callerSteamId, StringComparison.Ordinal))
        {
            return "Refusing to kick yourself.";
        }

        // Find the connected peer for this SteamID.
        ZNetPeer target = null;
        try
        {
            foreach (var p in ZNet.instance?.GetPeers() ?? new List<ZNetPeer>())
            {
                if (p != null && string.Equals(GetPeerPlatformId(p), targetSteamId, StringComparison.Ordinal))
                {
                    target = p; break;
                }
            }
        }
        catch { }

        if (target == null) return $"{FormatPlayer(targetSteamId)} is not currently online.";

        var reason = args.Length > 1 ? string.Join(" ", args.Skip(1)) : "Kicked by admin.";
        TryKick(target, reason);
        return $"Kicked {FormatPlayer(targetSteamId)}. Reason: {reason}";
    }

    // -------- Build-log query commands (#14 + #16 integration) --------

    // `actionFilter` restricts results to a specific action column value ("place" or
    // "destroy"). null = no filter (return both kinds). `label` is the user-facing
    // term in the reply summary ("event", "destroy", "placement").
    private string CmdBuild(string[] args, string actionFilter, string label)
    {
        if (args.Length == 0) return $"Usage: sg {(actionFilter == null ? "build" : (actionFilter == "destroy" ? "destroyed" : "placed"))} at|by|today ...  (see sg help)";
        var sub = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        switch (sub)
        {
            case "at":    return CmdBuildAt(rest, actionFilter, label);
            case "by":    return CmdBuildBy(rest, actionFilter, label);
            case "today": return CmdBuildToday(rest, actionFilter, label);
            default:      return $"Unknown subcommand `{sub}`. Try `at`, `by`, or `today`.";
        }
    }

    private string CmdBuildAt(string[] args, string actionFilter, string label)
    {
        if (args.Length < 2) return "Usage: sg build at <x> <z> [radius=50] [days=7]";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!float.TryParse(args[0], System.Globalization.NumberStyles.Float, inv, out var cx)) return $"Bad x: {args[0]}";
        if (!float.TryParse(args[1], System.Globalization.NumberStyles.Float, inv, out var cz)) return $"Bad z: {args[1]}";

        float radius = 50f;
        if (args.Length > 2 && !float.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out radius))
        {
            return $"Bad radius: {args[2]}";
        }

        int days = BuildQueryDefaultDays;
        if (args.Length > 3 && !int.TryParse(args[3], out days)) return $"Bad days: {args[3]}";

        var rows = LoadBuildLogRows(days);
        // No value tuples here: Valheim's Mono runtime doesn't ship System.ValueTuple
        // and any LINQ closure carrying one fails to load with TypeLoadException.
        // Hand-written loop is also clearer for the radius filter.
        var hits = new List<string[]>();
        foreach (var r in rows)
        {
            if (r.Length < 8) continue;
            if (actionFilter != null && !string.Equals(r[1], actionFilter, StringComparison.OrdinalIgnoreCase)) continue;
            float rx, rz;
            if (!TryParseXZ(r[5], r[7], out rx, out rz)) continue;
            if (Distance2D(rx, rz, cx, cz) > radius) continue;
            hits.Add(r);
        }
        hits.Sort((a, b) => string.CompareOrdinal(b[0], a[0])); // most recent first
        if (hits.Count > BuildQueryMaxResults) hits = hits.GetRange(0, BuildQueryMaxResults);

        if (hits.Count == 0) return $"No {label}(s) within {radius:F0}m of ({cx:F0}, {cz:F0}) in the last {days}d.";

        var sb = new StringBuilder();
        sb.AppendLine($"[ServerGuard] {hits.Count} {label}(s) within {radius:F0}m of ({cx:F0}, {cz:F0}) in last {days}d:");
        foreach (var r in hits)
        {
            sb.AppendLine(FormatBuildRowShort(r));
        }
        return sb.ToString().TrimEnd();
    }

    private string CmdBuildBy(string[] args, string actionFilter, string label)
    {
        if (args.Length == 0) return $"Usage: sg {(actionFilter == null ? "build" : (actionFilter == "destroy" ? "destroyed" : "placed"))} by <steamid|name> [days=7]";
        var query = args[0];
        int days = BuildQueryDefaultDays;
        if (args.Length > 1 && !int.TryParse(args[1], out days)) return $"Bad days: {args[1]}";

        var resolved = ResolvePlayerQuery(query);
        var rows = LoadBuildLogRows(days);

        // Match on steamId column OR on charName column.
        var matches = rows.Where(r => r.Length >= 8 && (
            resolved.Any(s => string.Equals(s, r[2], StringComparison.Ordinal))
            || (r[3] ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
        ))
        .Where(r => actionFilter == null || string.Equals(r[1], actionFilter, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(r => r[0])
        .Take(BuildQueryMaxResults)
        .ToList();

        if (matches.Count == 0) return $"No {label}(s) by `{query}` in the last {days}d.";

        var sb = new StringBuilder();
        sb.AppendLine($"[ServerGuard] {matches.Count} {label}(s) by `{query}` in last {days}d:");
        foreach (var r in matches)
        {
            sb.AppendLine(FormatBuildRowShort(r));
        }
        return sb.ToString().TrimEnd();
    }

    private string CmdBuildToday(string[] args, string actionFilter, string label)
    {
        int n = 10;
        if (args.Length > 0 && int.TryParse(args[0], out var parsed) && parsed > 0)
        {
            n = Math.Min(parsed, BuildQueryMaxResults);
        }

        var rows = LoadBuildLogRows(1);
        if (actionFilter != null)
        {
            rows = rows.Where(r => r.Length >= 8 && string.Equals(r[1], actionFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (rows.Count == 0) return $"No {label}(s) today.";

        var tail = rows.Skip(Math.Max(0, rows.Count - n)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"[ServerGuard] last {tail.Count} {label}(s) today:");
        foreach (var r in tail)
        {
            sb.AppendLine(FormatBuildRowShort(r));
        }
        return sb.ToString().TrimEnd();
    }

    // Reads CSV rows from the last `days` daily files. Returns rows in chronological
    // order. Skips header lines. Robust to missing files.
    private List<string[]> LoadBuildLogRows(int days)
    {
        var rows = new List<string[]>();
        if (!Directory.Exists(BuildLogDir)) return rows;

        var today = DateTime.UtcNow.Date;
        for (int i = days - 1; i >= 0; i--)
        {
            var date = today.AddDays(-i);
            var path = Path.Combine(BuildLogDir, $"{date:yyyy-MM-dd}.csv");
            if (!File.Exists(path)) continue;

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch { continue; }

            for (int k = 1; k < lines.Length; k++)   // skip header at index 0
            {
                var fields = ParseCsvLine(lines[k]);
                if (fields.Length >= 8) rows.Add(fields);
            }
        }
        return rows;
    }

    // Parses a CSV line that may include "double-quoted" fields with "" escapes.
    // Only handles the dialect we write - no comments, no embedded newlines.
    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else { inQuotes = false; }
                }
                else { sb.Append(c); }
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else { sb.Append(c); }
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }

    // Plain out-parameter pair instead of a value tuple. Valheim's Mono runtime
    // doesn't load System.ValueTuple, and any compiler-generated closure carrying
    // a value tuple field fails the whole containing Plugin type at load with
    // TypeLoadException.
    private static bool TryParseXZ(string xStr, string zStr, out float x, out float z)
    {
        x = 0f; z = 0f;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (!float.TryParse(xStr, System.Globalization.NumberStyles.Float, inv, out x)) return false;
        if (!float.TryParse(zStr, System.Globalization.NumberStyles.Float, inv, out z)) return false;
        return true;
    }

    private static float Distance2D(float px, float pz, float x, float z)
    {
        var dx = px - x;
        var dz = pz - z;
        return (float)Math.Sqrt(dx * dx + dz * dz);
    }

    // CSV columns: timestamp, action, steamId, charName, pieceName, x, y, z
    private static string FormatBuildRowShort(string[] r)
    {
        // Format: hh:mm:ss  PLACE  Erik         wood_wall       (123, -456)
        var time = r[0].Length >= 19 ? r[0].Substring(11, 8) : r[0];
        var act  = r[1].ToUpperInvariant().PadRight(7);
        var who  = string.IsNullOrEmpty(r[3]) ? (r[2] ?? "") : r[3];
        if (who.Length > 18) who = who.Substring(0, 18);
        who = who.PadRight(18);
        var piece = (r[4] ?? "").PadRight(22);
        var xStr = (r[5] ?? "");
        var zStr = (r[7] ?? "");
        return $"  {time}  {act}  {who}  {piece}  ({xStr}, {zStr})";
    }

    // Handles ServerGuard_PlayerDeath RPC. Companion plugin sends a payload describing
    // the local player's death; the server formats and posts to public Discord.
    //
    // Payload format (pipe-separated, invariant-culture floats):
    //   posX|posY|posZ|attackerKind|attackerLabel|causeHint
    //
    //   attackerKind  : "player" | "creature" | "self" | "environment"
    //   attackerLabel : character name (player) | mob hover name (creature) | "" (env)
    //   causeHint     : dominant damage type, e.g. "Fire", "Spirit", "Blunt", "Slash"
    public void OnPlayerDeathReceived(ZNetPeer peer, string payload)
    {
        try
        {
            if (peer == null) return;
            if (_settings == null || !_settings.EnableDeathLog) return;
            if (string.IsNullOrWhiteSpace(payload)) return;

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

            var victimSteamId = GetPeerPlatformId(peer);
            var victim        = FormatPlayer(victimSteamId);

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
            // y is altitude, less useful in the headline.
            var line = $":skull: **{victim}** died at `[{px:F0}, {pz:F0}]` — {killedBy}";
            LogS.LogInfo($"[ServerGuard] {line}");

            // Admin deaths are hidden from public - route to admin channel instead.
            var target = IsAdmin(victimSteamId) ? DiscordChannel.Admin : DiscordChannel.Public;
            _ = SendDiscordNow(line, target);
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

    // Handles ServerGuard_AnimationCancelAttempt RPC. Called when the companion plugin
    // intercepts an attempt to cancel an attack-recovery animation (emote, sheathe, ...).
    // The cancel is already prevented locally; we just track for admin visibility.
    public void OnAnimationCancelReceived(ZNetPeer peer, string source)
    {
        try
        {
            if (peer == null) return;
            if (!_settings.EnableAnimationCancelGate) return;

            var steamId = GetPeerPlatformId(peer);
            var who     = FormatPlayer(steamId);
            var src     = (source ?? "").Trim();
            if (src.Length > 32) src = src.Substring(0, 32);
            if (string.IsNullOrWhiteSpace(src)) src = "unknown";

            if (IsAdmin(steamId))
            {
                LogS.LogInfo($"[ServerGuard] {who} (admin) animation-cancel via {src} - ignoring.");
                return;
            }

            LogS.LogWarning($"[ServerGuard] {who} animation-cancel blocked client-side (source: {src}).");
            PostPlayerEvent(":no_entry_sign:", steamId, "tried to cancel attack", src);
            AddViolation(steamId, RULE_ANIMATION_CANCEL, src);
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnAnimationCancelReceived error: {ex}");
        }
    }

    // Handles ServerGuard_DevcommandAttempt RPC. Called when the companion plugin
    // intercepts a `devcommands` (or other blocked) command client-side. The cheat is
    // already prevented locally; this handler is purely for visibility + accounting.
    public void OnDevcommandAttemptReceived(ZNetPeer peer, string command)
    {
        try
        {
            if (peer == null) return;
            if (!_settings.EnableDevcommandGate) return;

            var steamId = GetPeerPlatformId(peer);
            var who     = FormatPlayer(steamId);
            var cmd     = (command ?? "").Trim();
            if (cmd.Length > 64) cmd = cmd.Substring(0, 64); // bound any client-supplied string
            if (string.IsNullOrWhiteSpace(cmd)) cmd = "(unknown)";

            // Admin bypass - operators may legitimately use console for moderation.
            if (IsAdmin(steamId))
            {
                LogS.LogInfo($"[ServerGuard] {who} (admin) used `{cmd}` - bypassing devcommand gate.");
                return;
            }

            LogS.LogWarning($"[ServerGuard] {who} attempted blocked command `{cmd}` (companion gate blocked it client-side).");
            PostPlayerEvent(":no_entry_sign:", steamId, "tried to use cheats", $"`{cmd}`");
            AddViolation(steamId, RULE_DEVCOMMAND_ATTEMPT, cmd);
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnDevcommandAttemptReceived error: {ex}");
        }
    }

    private struct PolicyVerdict
    {
        public bool Allowed;
        public string Rule;
        public string Reason; // technical detail for server log + kick screen
        public string Detail; // short label (mod name / guid) for Discord friendly text
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
                var label = hit.Name ?? hit.Guid;
                return new PolicyVerdict { Allowed = false, Rule = RULE_BANNED_MOD, Reason = $"Disallowed mod present: {label}", Detail = label };
            }
        }

        // 2. required_mods - every entry must be present (with hash match if pinned).
        foreach (var r in _requiredMods)
        {
            if (!byKey.TryGetValue(r.Key, out var hit))
            {
                return new PolicyVerdict { Allowed = false, Rule = RULE_REQUIRED_MOD_MISSING, Reason = $"Required mod missing: {r.Key}", Detail = r.Key };
            }
            if (!string.IsNullOrEmpty(r.Sha256) && !string.Equals(r.Sha256, hit.Sha256 ?? "", StringComparison.OrdinalIgnoreCase))
            {
                var label = hit.Name ?? hit.Guid ?? r.Key;
                return new PolicyVerdict { Allowed = false, Rule = RULE_HASH_MISMATCH, Reason = $"Required mod hash mismatch: {r.Key}", Detail = label };
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
                    var label = !string.IsNullOrEmpty(m.Name) ? m.Name : m.Guid;
                    return new PolicyVerdict { Allowed = false, Rule = RULE_DISALLOWED_MOD, Reason = $"Unapproved mod: {label}", Detail = label };
                }
                if (!string.IsNullOrEmpty(rule.Sha256) && !string.Equals(rule.Sha256, m.Sha256 ?? "", StringComparison.OrdinalIgnoreCase))
                {
                    var label = m.Name ?? m.Guid;
                    return new PolicyVerdict { Allowed = false, Rule = RULE_HASH_MISMATCH, Reason = $"Hash pin mismatch: {label}", Detail = label };
                }
            }
        }

        return new PolicyVerdict { Allowed = true };
    }

	private sealed class DiscordLogListener : ILogListener, IDisposable
	{
		private readonly string _webhook;
		private readonly string _prefix;
		private readonly string _allowedSourceName;
		private readonly System.Timers.Timer _flushTimer;
		private readonly Queue<string> _buffer = new();
		private static readonly HttpClient _http = new HttpClient();
		private bool _isFlushing = false;
		private const int MaxDiscordLength = 2000;
		private const int MaxPostLength    = 1800;

		public DiscordLogListener(string webhook, string prefixTag, string allowedSourceName)
		{
			_webhook = webhook?.Trim();
			_prefix  = string.IsNullOrWhiteSpace(prefixTag) ? "[ServerGuard]" : prefixTag.Trim();
			_allowedSourceName = allowedSourceName ?? string.Empty;

			_flushTimer = new System.Timers.Timer(2000);
			_flushTimer.AutoReset = true;
			_flushTimer.Elapsed += (s, e) => FlushIfNeeded();
			_flushTimer.Start();
		}

		public void LogEvent(object sender, LogEventArgs eventArgs)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(_webhook)) return;

				var srcName = eventArgs.Source?.SourceName ?? string.Empty;
				if (!string.Equals(srcName, _allowedSourceName, StringComparison.Ordinal))
					return;

				var lvl = eventArgs.Level.ToString().ToUpperInvariant();
				var msg = eventArgs.Data?.ToString() ?? "";

				var line = $"{_prefix} [{lvl}] {msg}".Trim();
				lock (_buffer)
				{
					_buffer.Enqueue(line);
					if (_buffer.Count > 1000) _buffer.Dequeue();
				}
			}
			catch { }
		}

		private async void FlushIfNeeded()
		{
			if (string.IsNullOrWhiteSpace(_webhook)) return;
			if (_isFlushing) return;

			List<string> batch = null;
			lock (_buffer)
			{
				if (_buffer.Count == 0) return;
				batch = new List<string>(_buffer);
				_buffer.Clear();
			}

			_isFlushing = true;
			try
			{
				var chunk = new StringBuilder();
				foreach (var line in batch)
				{
					var add = line.Length + 1;
					if (chunk.Length + add > MaxPostLength)
					{
						await PostAsync(chunk.ToString());
						chunk.Clear();
					}
					chunk.AppendLine(line.Length > MaxDiscordLength ? line.Substring(0, MaxDiscordLength) : line);
				}
				if (chunk.Length > 0)
					await PostAsync(chunk.ToString());
			}
			catch
			{
			}
			finally
			{
				_isFlushing = false;
			}
		}

		private async Task PostAsync(string content)
		{
			if (string.IsNullOrWhiteSpace(content)) return;
			var payload = new { content };
			var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
			using (var req = new StringContent(json, Encoding.UTF8, "application/json"))
			{
				await _http.PostAsync(_webhook, req);
			}
		}

		public void Dispose()
		{
			try { _flushTimer?.Stop(); _flushTimer?.Dispose(); } catch { }
		}
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

    // ==================== Shout logging ====================
    //
    // Chat can't be observed server-side on current Valheim builds, so the companion
    // reports outgoing shouts over the ServerGuard_Chat RPC. Payload: "<type>|<text>"
    // (Talker.Type.Shout = 2). Names/SteamIDs come from the server-side peer.
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
            _ = SendDiscordNow($":mega: **{charName}** shouted: {text}", DiscordChannel.Public);
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] OnChatReceived error: {ex}");
        }
    }

    // ==================== Cheat item removal ====================
    //
    // Sends the configured prefab-name list to the peer's companion plugin, which
    // removes those items from the player's inventory after spawn. Admins are exempt
    // (this is only called from the non-admin login path).
    private void SendCheatItemRemovalIfEnabled(ZNetPeer peer)
    {
        try
        {
            if (_settings == null || !_settings.EnableCheatItemRemoval) return;
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

    // ==================== Raid event logging ====================

    internal void OnRaidStarted(string name, Vector3 pos)
    {
        if (string.Equals(name, _currentRaidName, StringComparison.Ordinal)) return;
        if (_currentRaidName != null) OnRaidEnded();

        _currentRaidName = name;
        _currentRaidPos  = pos;
        _raidPaused      = false;

        var display = GetRaidDisplayName(name);
        var coord = $"X:{pos.x:F0}, Z:{pos.z:F0}";
        LogS.LogInfo($"[ServerGuard] RAID START | {display} ({name}) at ({coord})");
        _ = SendDiscordNow($":crossed_swords: **{display}** has started! Location: `{coord}`", DiscordChannel.Public);

        if (_raidMonitorCoroutine != null) StopCoroutine(_raidMonitorCoroutine);
        _raidMonitorCoroutine = StartCoroutine(MonitorRaidEvent());
    }

    internal void OnRaidEnded()
    {
        if (_currentRaidName == null) return;

        var display = GetRaidDisplayName(_currentRaidName);
        var coord = $"X:{_currentRaidPos.x:F0}, Z:{_currentRaidPos.z:F0}";
        LogS.LogInfo($"[ServerGuard] RAID END | {display} ({_currentRaidName})");
        _ = SendDiscordNow($":white_check_mark: **{display}** is over! Location was: `{coord}`", DiscordChannel.Public);

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

            var current = RandEventSystem.instance.GetCurrentRandomEvent();
            var active  = RandEventSystem.instance.GetActiveEvent();
            bool isPaused = current != null && active == null;

            var display = GetRaidDisplayName(_currentRaidName);

            if (isPaused && !_raidPaused)
            {
                _raidPaused = true;
                var coord = $"X:{_currentRaidPos.x:F0}, Z:{_currentRaidPos.z:F0}";
                LogS.LogInfo($"[ServerGuard] RAID PAUSED | {display}");
                _ = SendDiscordNow($":pause_button: **{display}** is paused — no players in the event area. Location: `{coord}`", DiscordChannel.Public);
            }
            else if (!isPaused && _raidPaused)
            {
                _raidPaused = false;
                LogS.LogInfo($"[ServerGuard] RAID RESUMED | {display}");
                _ = SendDiscordNow($":arrow_forward: **{display}** has resumed.", DiscordChannel.Public);
            }
        }
        _raidMonitorCoroutine = null;
    }

    // Fires when the server sets a new random event. SetRandomEvent is private, so we
    // resolve it via TargetMethod().
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
}
