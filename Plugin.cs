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

[BepInPlugin("com.taeguk.valheim.serverguard", "Valheim ServerGuard", "1.3.0")]
public class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance;
    internal static ManualLogSource LogS;
    private Harmony _harmony;
	
	// -------- NEW: Discord log listener --------
    private DiscordLogListener _discordListener;

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

    // File watchers (hot-reload)
    private FileSystemWatcher _watchSettings, _watchAdmins, _watchAllowed;
    private readonly Dictionary<string, DateTime> _lastSeenWrite = new();

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
        public string discordWebhookUrl  { get; set; } = "";
        public string discordChannelLink { get; set; } = "";

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
            $"[ServerGuard] Loaded (v1.3.0). " +
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
		
		// Start log forwarding if webhook is present
		if (!string.IsNullOrWhiteSpace(_settings.discordWebhookUrl))
		{
			try
			{
				var allowedSource = LogS?.SourceName ?? "Valheim ServerGuard";

				BepInEx.Logging.Logger.Listeners.Add(
					_discordListener = new DiscordLogListener(_settings.discordWebhookUrl, "[ServerGuard]", allowedSource)
				);

				LogS.LogInfo($"[ServerGuard] Discord logging enabled for source '{allowedSource}'.");
			}
			catch (Exception ex)
			{
				LogS.LogWarning($"[ServerGuard] Failed to enable Discord logging: {ex.Message}");
			}
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
	
	private async Task SendDiscordNow(string text)
    {
        try
        {
            var url = _settings?.discordWebhookUrl;
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
            LogS.LogWarning($"[ServerGuard] SendDiscordNow failed: {ex.Message}");
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
            sb.AppendLine("# ServerGuard settings (v1.3.0)");
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
            sb.AppendLine("# Discord:");
            sb.AppendLine("#   discordWebhookUrl      - full Discord Webhook URL for live event forwarding.");
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

    private void AddViolation(string platformId, string rule)
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

        var who = FormatPlayer(platformId);
        LogS.LogWarning($"[ServerGuard] {who} violated {rule}. Count={map[rule]}/{_settings.ViolationThreshold}");
		_ = SendDiscordNow($":warning: Violation by {who} — **{rule}** ({map[rule]}/{_settings.ViolationThreshold})");

        if (_settings.Enforce && map[rule] >= _settings.ViolationThreshold)
        {
            TryBan(platformId, _settings.BanReason);
            if (_settings.EnableMetrics)
            {
                _metrics.players_banned++;
                SaveMetrics();
            }
			_ = SendDiscordNow($":no_entry: Auto-banned {who}. Reason: {_settings.BanReason}");
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
                _ = SendDiscordNow($":boot: Disconnected {who}. Reason: {reason}");
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
                _ = SendDiscordNow($":boot: Disconnected {who}. Reason: {reason}");
                return;
            }

            var internalKick = znet.GetType().GetMethod("InternalKick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(ZNetPeer) }, null);
            if (internalKick != null)
            {
                internalKick.Invoke(znet, new object[] { peer });
                LogS.LogWarning($"[ServerGuard] InternalKick'd {who}. Reason: {reason}");
                _ = SendDiscordNow($":boot: Kicked {who}. Reason: {reason}");
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
				_ = SendDiscordNow($":no_entry: Auto-banned {who}. Reason: {reason}");
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

				if (Plugin.Instance.IsAdmin(steamId)) return;

				if (!Plugin.Instance._registrations.TryGetValue(steamId, out var names) || names == null)
				{
					names = new List<string>();
					Plugin.Instance._registrations[steamId] = names;
				}

				if (names.Any(n => string.Equals(n, charName, StringComparison.Ordinal)))
				{
					return;
				}

				int limit = Math.Max(1, Plugin.Instance._settings.CharacterLimit);
				if (names.Count < limit)
				{
					names.Add(charName);
					Plugin.Instance.SaveRegistrations();
					Plugin.LogS.LogInfo($"[ServerGuard] Registered character #{names.Count}/{limit} for {Plugin.Instance.FormatPlayer(steamId)} -> '{charName}'");
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
        _ = SendDiscordNow($":hourglass: No manifest from {label} in {seconds}s. Companion plugin missing or unreachable.");

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
                _ = SendDiscordNow($":no_entry_sign: Rejected {who} - {verdict.Rule}: {verdict.Reason}");
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
}
