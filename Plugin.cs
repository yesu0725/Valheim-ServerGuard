using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Timers;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

[BepInPlugin("com.taeguk.valheim.serverguard", "Valheim ServerGuard", "1.2.0")]
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
    private static readonly string IgnoreModsYaml    = Path.Combine(ConfDir, "ignore_mods.yaml");
    private static readonly string ModPatternsYaml   = Path.Combine(ConfDir, "mod_patterns.yaml");
    private static readonly string RegistrationsYaml = Path.Combine(ConfDir, "registrations.yaml");
    private static readonly string ViolationsYaml    = Path.Combine(ConfDir, "violations.yaml");

    // -------- YAML Serializer --------
    private static IDeserializer _yamlIn;
    private static ISerializer _yamlOut;

    // -------- In-memory state --------
    private Settings _settings;
    private HashSet<string> _admins = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _ignoredModTokens = new(StringComparer.OrdinalIgnoreCase);
    private ModPatterns _modPatterns;

    // SteamID -> CharacterID
    private Dictionary<string, List<string>> _registrations = new(StringComparer.OrdinalIgnoreCase);

    // SteamID -> rule -> attempts
    private Dictionary<string, Dictionary<string, int>> _violations = new(StringComparer.OrdinalIgnoreCase);

    // Rule keys
    private const string RULE_MODDED = "ClientModded";
    private const string RULE_CHAR_NOT_REGISTERED = "CharacterNotRegistered";
	private const string RULE_CHAR_NAME_MISMATCH = "CharacterNameMismatch";
	private const string RULE_CHAR_NAME_LIMIT    = "CharacterNameLimitExceeded";

    // File watchers (hot-reload)
    private FileSystemWatcher _watchSettings, _watchAdmins, _watchIgnore, _watchModPatterns;
    private readonly Dictionary<string, DateTime> _lastSeenWrite = new();

    // -------------- Data Models --------------
    private class Settings
    {
        public int  ViolationThreshold   { get; set; } = 3;   // attempts before auto-ban
        public bool Enforce              { get; set; } = true;
        public bool AggressiveNoModCheck { get; set; } = true;
        public bool EnableAssemblyScanning { get; set; } = true; // NEW: check loaded assemblies
        public bool RequireAttestation   { get; set; } = false;
        public string KickMessage        { get; set; } = "You cannot join: server security policy violation. Contact an administrator.";
        public string BanReason          { get; set; } = "Auto-banned due to repeated security violations.";
		public int CharacterLimit        { get; set; } = 1; // how many different character names a SteamID is allowed to use
		public string discordWebhookUrl  { get; set; } = ""; // Paste full Discord webhook URL here
		public string discordChannelLink { get; set; } = ""; // Optional: human-friendly channel URL for reference
    }

    private class AdminsDoc
    {
        public List<string> admins { get; set; } = new();
    }

    private class IgnoreModsDoc
    {
        public List<string> ignore_mods { get; set; } = new();
    }

    private class ModPatterns
    {
        public List<string> rpc_tokens { get; set; } = new();
        public List<string> assembly_namespaces { get; set; } = new();
        public List<string> version_keywords { get; set; } = new();
    }

    private class RegistrationsDoc
	{
		// steam_id -> list of character names
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
        LoadIgnoreMods();
        LoadModPatterns();
        LoadRegistrations();
        LoadViolations();

        // Start file watchers for hot-reload
        StartWatchers();

        // Harmony patches
        _harmony = new Harmony("com.taeguk.valheim.serverguard");
        _harmony.PatchAll();

        LogS.LogInfo($"[ServerGuard] Loaded (YAML). Enforcement: {(_settings.Enforce ? "ON" : "LOG-ONLY")}. Assembly scanning: {(_settings.EnableAssemblyScanning ? "ON" : "OFF")}");
		
		// Start log forwarding if webhook is present
		if (!string.IsNullOrWhiteSpace(_settings.discordWebhookUrl))
		{
			try
			{
				// This is the ONLY source name we will forward
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
		// Unpatch Harmony
		try
		{
			_harmony?.UnpatchSelf();
		}
		catch (Exception ex)
		{
			LogS?.LogWarning($"[ServerGuard] UnpatchSelf failed: {ex.Message}");
		}

		// Detach & dispose Discord listener
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

		// Stop file watchers
		try
		{
			StopWatchers();
		}
		catch (Exception ex)
		{
			LogS?.LogWarning($"[ServerGuard] StopWatchers failed: {ex.Message}");
		}

		// Persist state
		try
		{
			SaveAll();
		}
		catch (Exception ex)
		{
			LogS?.LogWarning($"[ServerGuard] SaveAll failed: {ex.Message}");
		}
	}
	
	// -------- NEW: direct Discord post helper --------
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
				discordWebhookUrl = "",
				discordChannelLink = ""
			};
			var sb = new StringBuilder();
			sb.AppendLine("# ServerGuard settings");
			sb.AppendLine("# discordWebhookUrl: paste the FULL Discord Webhook URL from: Channel Settings → Integrations → Webhooks");
			sb.AppendLine("# discordChannelLink: optional, for your reference (e.g., https://discord.com/channels/<server>/<channel>)");
			sb.AppendLine("# enableAssemblyScanning: enable/disable assembly namespace scanning for mod detection");
			sb.AppendLine(_yamlOut.Serialize(defaults));
			File.WriteAllText(SettingsYaml, sb.ToString());
		}

        if (!File.Exists(AdminsYaml))
        {
            var doc = new AdminsDoc { admins = new List<string>() /* add your SteamID here */ };
            var sb = new StringBuilder();
            sb.AppendLine("# Admin whitelist: one SteamID (or platform ID) per entry");
            sb.AppendLine(_yamlOut.Serialize(doc));
            File.WriteAllText(AdminsYaml, sb.ToString());
        }

        if (!File.Exists(IgnoreModsYaml))
        {
            var doc = new IgnoreModsDoc { ignore_mods = new List<string> { "Jotunn", "ServerSync" } };
            var sb = new StringBuilder();
            sb.AppendLine("# Ignore list for client-side mod tokens you permit");
            sb.AppendLine("# Example entries: Jotunn, ServerSync");
            sb.AppendLine(_yamlOut.Serialize(doc));
            File.WriteAllText(IgnoreModsYaml, sb.ToString());
        }

        if (!File.Exists(ModPatternsYaml))
        {
            var doc = new ModPatterns
            {
                rpc_tokens = new List<string>
                {
                    "JVL", "Jotunn",
                    "ServerSync",
                    "BepInEx",
                    "ValheimPlus",
                    "Wonderlands",
                    "Komrade",
                    "EpicLoot",
                    "Seasons",
                    "CustomUI",
                    "ModVer",
                    "ModInfo"
                },
                assembly_namespaces = new List<string>
                {
                    "Jotunn",
                    "ValheimPlus",
                    "Wonderlands",
                    "Komrade",
                    "EpicLoot",
                    "Seasons",
                    "CustomUI"
                },
                version_keywords = new List<string>
                {
                    "mod",
                    "modded",
                    "custom",
                    "patched"
                }
            };
            var sb = new StringBuilder();
            sb.AppendLine("# Mod detection patterns (Phase 1 & 2)");
            sb.AppendLine("# rpc_tokens: RPC method name tokens that indicate mods");
            sb.AppendLine("# assembly_namespaces: Assembly namespaces to flag as modded");
            sb.AppendLine("# version_keywords: Keywords in version strings to flag mods");
            sb.AppendLine(_yamlOut.Serialize(doc));
            File.WriteAllText(ModPatternsYaml, sb.ToString());
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
    }

    // -------------- YAML Load / Save --------------
    private void LoadSettings()
    {
        try
        {
            _settings = _yamlIn.Deserialize<Settings>(File.ReadAllText(SettingsYaml)) ?? new Settings();
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

    private void LoadIgnoreMods()
    {
        try
        {
            var text = File.ReadAllText(IgnoreModsYaml);
            var doc = _yamlIn.Deserialize<IgnoreModsDoc>(text) ?? new IgnoreModsDoc();
            _ignoredModTokens = new HashSet<string>((doc.ignore_mods ?? new List<string>()).Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
            LogS.LogInfo($"[ServerGuard] ignore_mods.yaml loaded ({_ignoredModTokens.Count} tokens)");
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Failed to load ignore_mods.yaml: {ex.Message}");
            _ignoredModTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void LoadModPatterns()
    {
        try
        {
            var text = File.ReadAllText(ModPatternsYaml);
            _modPatterns = _yamlIn.Deserialize<ModPatterns>(text) ?? new ModPatterns();
            
            // Ensure lists are initialized
            _modPatterns.rpc_tokens ??= new List<string>();
            _modPatterns.assembly_namespaces ??= new List<string>();
            _modPatterns.version_keywords ??= new List<string>();
            
            LogS.LogInfo($"[ServerGuard] mod_patterns.yaml loaded ({_modPatterns.rpc_tokens.Count} RPC tokens, {_modPatterns.assembly_namespaces.Count} namespaces)");
        }
        catch (Exception ex)
        {
            LogS.LogError($"[ServerGuard] Failed to load mod_patterns.yaml: {ex.Message}");
            _modPatterns = new ModPatterns();
        }
    }

    private void LoadRegistrations()
	{
		try
		{
			var text = File.ReadAllText(RegistrationsYaml);
			// Try v2 (list-based)
			var doc = _yamlIn.Deserialize<RegistrationsDoc>(text);
			if (doc?.registrations != null && doc.registrations.Count > 0)
			{
				_registrations = doc.registrations;
			}
			else
			{
				// Try v1 (string-based) -> migrate
				var legacy = _yamlIn.Deserialize<Dictionary<string, Dictionary<string, string>>>(text);
				// legacy shape expected: { registrations: { "steamId": "charName", ... } }
				if (legacy != null && legacy.TryGetValue("registrations", out var mapV1) && mapV1 != null)
				{
					var v2 = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
					foreach (var kv in mapV1)
					{
						if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
							v2[kv.Key] = new List<string> { kv.Value.Trim() };
					}
					_registrations = v2;
					SaveRegistrations(); // write back in new format
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

    private void SaveAll()
    {
        SaveRegistrations();
        SaveViolations();
    }

    // -------------- Helpers --------------
    private static string GetPeerPlatformId(object znetPeer)
	{
		try
		{
			// --- 1) direct on peer ---
			// a) field m_platformUserID (ulong)
			var fPlat = znetPeer.GetType().GetField("m_platformUserID",
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (fPlat != null)
			{
				var val = fPlat.GetValue(znetPeer);
				if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
			}

			// b) method GetPlatformUserID()
			var mGetPlat = znetPeer.GetType().GetMethod("GetPlatformUserID",
				BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (mGetPlat != null)
			{
				var val = mGetPlat.Invoke(znetPeer, null);
				if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
			}

			// --- 2) from socket ---
			var fSock = znetPeer.GetType().GetField("m_socket", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			var socket = fSock?.GetValue(znetPeer);
			if (socket != null)
			{
				// common field: m_peerID
				var fPeerId = socket.GetType().GetField("m_peerID",
					BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
				if (fPeerId != null)
				{
					var val = fPeerId.GetValue(socket);
					if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
				}

				// common methods
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

				// common properties
				var pSteamId = socket.GetType().GetProperty("SteamID",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (pSteamId != null)
				{
					var val = pSteamId.GetValue(socket, null);
					if (TryNormalizeSteamId(val, out var sid) && IsValidSteamId(sid)) return sid;
				}

				// sometimes nested struct with m_SteamID (Steamworks)
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

				// last string-ish fallback (sometimes host name is the 64‑bit ID)
				var mHost = socket.GetType().GetMethod("GetHostName",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (mHost != null)
				{
					var host = Convert.ToString(mHost.Invoke(socket, null));
					var fromHost = ExtractSteamIdFromString(host);
					if (IsValidSteamId(fromHost)) return fromHost;
				}

				// absolute last resort: scan socket.ToString()
				var any = ExtractSteamIdFromString(socket.ToString());
				if (IsValidSteamId(any)) return any;
			}

			// --- 3) absolute last resort: scan peer.ToString() ---
			var sPeer = ExtractSteamIdFromString(znetPeer.ToString());
			if (IsValidSteamId(sPeer)) return sPeer;
		}
		catch
		{
			// ignore and fall through
		}

		return "UNKNOWN";
	}

	// Accept ulong/long/string/structs. Also look for m_SteamID/Value field inside structs.
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

		// struct with m_SteamID
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

		// common property name
		var pVal = t.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (pVal != null)
		{
			var v = pVal.GetValue(raw, null);
			if (v != null && ulong.TryParse(v.ToString(), out var u3) && u3 != 0UL)
			{
				normalized = u3.ToString(); return true;
			}
		}

		// last chance: try ToString() scan
		var fromString = ExtractSteamIdFromString(raw.ToString());
		if (IsValidSteamId(fromString)) { normalized = fromString; return true; }

		return false;
	}

	private static string ExtractSteamIdFromString(string s)
	{
		if (string.IsNullOrEmpty(s)) return null;
		// scan for a 17-digit numeric run
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

        LogS.LogWarning($"[ServerGuard] {platformId} violated {rule}. Count={map[rule]}/{_settings.ViolationThreshold}");
		_ = SendDiscordNow($":warning: Violation by {platformId} — **{rule}** ({map[rule]}/{_settings.ViolationThreshold})");

        if (_settings.Enforce && map[rule] >= _settings.ViolationThreshold)
        {
            TryBan(platformId, _settings.BanReason);
			_ = SendDiscordNow($":no_entry: Auto-banned {platformId}. Reason: {_settings.BanReason}");
        }
    }

    private void TryKick(object znetPeer, string reason)
    {
        try
        {
            var znet = typeof(ZNet).GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            if (znet == null) return;

            var kickPeer = znet.GetType().GetMethod("Kick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(ZNetPeer) }, null);
            if (kickPeer != null)
            {
                kickPeer.Invoke(znet, new object[] { znetPeer as ZNetPeer });
                LogS.LogWarning($"[ServerGuard] Kicked peer. Reason: {reason}");
				_ = SendDiscordNow($":boot: Kicked peer. Reason: {reason}");
                return;
            }

            var pid = GetPeerPlatformId(znetPeer);
            var kickId = znet.GetType().GetMethod("Kick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(string) }, null);
            if (kickId != null)
            {
                kickId.Invoke(znet, new object[] { pid });
                LogS.LogWarning($"[ServerGuard] Kicked {pid}. Reason: {reason}");
				_ = SendDiscordNow($":boot: Kicked {pid}. Reason: {reason}");
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
                LogS.LogWarning($"[ServerGuard] Auto-banned {platformId}. Reason: {reason}");
				_ = SendDiscordNow($":no_entry: Auto-banned {platformId}. Reason: {reason}");
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
                if (!ZNet.instance || !ZNet.instance.IsServer()) return;

                var pid = Plugin.GetPeerPlatformId(peer);
                var pname = Plugin.GetPeerPlayerName(peer);
                Plugin.LogS.LogInfo($"[ServerGuard] Incoming connection: {pname} ({pid})");

                if (Plugin.Instance.IsAdmin(pid))
                {
                    Plugin.LogS.LogInfo($"[ServerGuard] {pid} is admin – skipping checks.");
                    return;
                }

                if (Plugin.Instance._settings.AggressiveNoModCheck)
                {
                    if (Plugin.Instance.DetectLikelyModdedClient(peer, out var reason, out var matchedToken))
                    {
                        if (!string.IsNullOrEmpty(matchedToken) &&
                            Plugin.Instance._ignoredModTokens.Contains(matchedToken))
                        {
                            Plugin.LogS.LogInfo($"[ServerGuard] Detected mod token '{matchedToken}' but it is allowed (ignore list).");
                        }
                        else
                        {
                            Plugin.Instance.AddViolation(pid, RULE_MODDED);
                            if (Plugin.Instance._settings.Enforce)
                            {
                                Plugin.Instance.TryKick(peer, $"{Plugin.Instance._settings.KickMessage} (No-mods policy)");
                                return;
                            }
                        }
                    }
                }
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

				var steamId  = Plugin.GetPeerPlatformId(peer); // must be 17-digit
				var charName = Plugin.GetPeerPlayerName(peer)?.Trim();

				// Only act if we truly have a SteamID + name
				if (!IsValidSteamId(steamId)) { Plugin.LogS.LogWarning("[ServerGuard] PeerInfo without valid SteamID; deferring."); return; }
				if (string.IsNullOrWhiteSpace(charName) || string.Equals(charName, "Unknown", StringComparison.OrdinalIgnoreCase)) return;

				if (Plugin.Instance.IsAdmin(steamId)) return;

				// --- character-limit enforcement using list storage ---
				// We have: 'steamId' (validated 17-digit), 'charName' (trimmed), and admin bypass already done.
				if (!Plugin.Instance._registrations.TryGetValue(steamId, out var names) || names == null)
				{
					names = new List<string>();
					Plugin.Instance._registrations[steamId] = names;
				}

				// If this name is already allowed, we're done
				if (names.Any(n => string.Equals(n, charName, StringComparison.Ordinal)))
				{
					return; // OK
				}

				// Not in the list yet — check limit
				int limit = Math.Max(1, Plugin.Instance._settings.CharacterLimit);
				if (names.Count < limit)
				{
					names.Add(charName);
					Plugin.Instance.SaveRegistrations();
					Plugin.LogS.LogInfo($"[ServerGuard] Registered character #{names.Count}/{limit} for {steamId} -> '{charName}'");
				}
				else
				{
					// Over the limit: violation + (optional) kick
					Plugin.Instance.AddViolation(steamId, RULE_CHAR_NAME_LIMIT);
					if (Plugin.Instance._settings.Enforce)
					{
						Plugin.Instance.TryKick(peer, $"{Plugin.Instance._settings.KickMessage} (Character limit {limit} reached: {string.Join(", ", names)})");
					}
					else
					{
						Plugin.LogS.LogWarning($"[ServerGuard] {steamId} exceeded character limit ({limit}). Tried '{charName}'. Allowed: {string.Join(", ", names)}");
					}
				}
			}
			catch (Exception ex)
			{
				Plugin.LogS.LogError($"[ServerGuard] RPC_PeerInfo error: {ex}");
			}
		}
	}


    // -------------- Compatibility: resolve peer from ZRpc --------------
    private static ZNetPeer ResolvePeerFromRpc(ZNet znet, ZRpc rpc)
    {
        if (znet == null || rpc == null) return null;

        // Try GetPeer(ZRpc)
        var mZrpc = typeof(ZNet).GetMethod("GetPeer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(ZRpc) }, null);
        if (mZrpc != null)
        {
            return (ZNetPeer)mZrpc.Invoke(znet, new object[] { rpc });
        }

        // Fallback: GetPeer(long uid)
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

        // Could not resolve
        Plugin.LogS.LogWarning("[ServerGuard] ResolvePeerFromRpc: unable to resolve peer from ZRpc.");
        return null;
    }

    // -------------- PHASE 1: Enhanced RPC Token Detection + PHASE 2: Assembly Scanning --------------
    private bool DetectLikelyModdedClient(ZNetPeer peer, out string reason, out string matchedToken)
    {
        reason = null;
        matchedToken = null;

        try
        {
            // ========== PHASE 1: RPC Token Detection (Enhanced) ==========
            var rpcField = peer.GetType().GetField("m_rpc", BindingFlags.Instance | BindingFlags.NonPublic);
            var rpc = rpcField?.GetValue(peer);
            if (rpc != null)
            {
                var mMethodsF = rpc.GetType().GetField("m_methods", BindingFlags.Instance | BindingFlags.NonPublic);
                var methods = mMethodsF?.GetValue(rpc) as IDictionary<string, Delegate>;
                if (methods != null)
                {
                    foreach (var name in methods.Keys)
                    {
                        // Check against dynamic mod patterns
                        if (_modPatterns?.rpc_tokens != null && _modPatterns.rpc_tokens.Count > 0)
                        {
                            foreach (var token in _modPatterns.rpc_tokens)
                            {
                                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    matchedToken = token;
                                    reason = $"RPC token matched: {token}";
                                    LogS.LogWarning($"[ServerGuard] Phase 1 detection (RPC): {reason} (method: {name})");
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            // ========== PHASE 2: Assembly Namespace Scanning ==========
            if (_settings.EnableAssemblyScanning)
            {
                var detectedMods = ScanPeerAssemblies(peer);
                if (detectedMods.Count > 0)
                {
                    matchedToken = string.Join(", ", detectedMods);
                    reason = $"Assembly namespaces detected: {matchedToken}";
                    LogS.LogWarning($"[ServerGuard] Phase 2 detection (Assembly): {reason}");
                    return true;
                }
            }

            // ========== PHASE 1 (continued): Version String Inspection ==========
            var versionF = peer.GetType().GetField("m_playerVersion", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var verObj = versionF?.GetValue(peer);
            var verStr = verObj?.ToString() ?? string.Empty;
            
            if (!string.IsNullOrEmpty(verStr))
            {
                if (_modPatterns?.version_keywords != null && _modPatterns.version_keywords.Count > 0)
                {
                    foreach (var keyword in _modPatterns.version_keywords)
                    {
                        if (verStr.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            reason = $"Version string contains keyword '{keyword}': {verStr}";
                            matchedToken = keyword;
                            LogS.LogWarning($"[ServerGuard] Phase 1 detection (Version): {reason}");
                            return true;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] DetectLikelyModdedClient error: {ex.Message}");
        }

        return false;
    }

    // ========== NEW: Phase 2 Assembly Scanning Helper ==========
    private List<string> ScanPeerAssemblies(ZNetPeer peer)
    {
        var detectedMods = new List<string>();

        try
        {
            // Get the AppDomain to scan all loaded assemblies
            var domain = AppDomain.CurrentDomain;
            var loadedAssemblies = domain.GetAssemblies();

            if (_modPatterns?.assembly_namespaces == null || _modPatterns.assembly_namespaces.Count == 0)
                return detectedMods;

            foreach (var assembly in loadedAssemblies)
            {
                try
                {
                    var assemblyName = assembly.GetName().Name ?? string.Empty;
                    var types = assembly.GetTypes();

                    foreach (var type in types)
                    {
                        try
                        {
                            var typeName = type.FullName ?? string.Empty;

                            // Check against mod namespace patterns
                            foreach (var nsPattern in _modPatterns.assembly_namespaces)
                            {
                                if (typeName.IndexOf(nsPattern, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    if (!detectedMods.Contains(nsPattern))
                                    {
                                        detectedMods.Add(nsPattern);
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // Ignore individual type scanning errors
                        }
                    }
                }
                catch
                {
                    // Ignore individual assembly scanning errors
                }
            }
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] ScanPeerAssemblies error: {ex.Message}");
        }

        return detectedMods;
    }
	
	// ---------------- Discord Log Listener ----------------
	private sealed class DiscordLogListener : ILogListener, IDisposable
	{
		private readonly string _webhook;
		private readonly string _prefix;
		private readonly string _allowedSourceName; // only forward logs from this source
		private readonly System.Timers.Timer _flushTimer;
		private readonly Queue<string> _buffer = new();
		private static readonly HttpClient _http = new HttpClient();
		private bool _isFlushing = false;
		private const int MaxDiscordLength = 2000;     // hard limit
		private const int MaxPostLength    = 1800;     // leave headroom for formatting

		public DiscordLogListener(string webhook, string prefixTag, string allowedSourceName)
		{
			_webhook = webhook?.Trim();
			_prefix  = string.IsNullOrWhiteSpace(prefixTag) ? "[ServerGuard]" : prefixTag.Trim();
			_allowedSourceName = allowedSourceName ?? string.Empty;

			_flushTimer = new System.Timers.Timer(2000); // flush every 2s
			_flushTimer.AutoReset = true;
			_flushTimer.Elapsed += (s, e) => FlushIfNeeded();
			_flushTimer.Start();
		}

		public void LogEvent(object sender, LogEventArgs eventArgs)
		{
			try
			{
				if (string.IsNullOrWhiteSpace(_webhook)) return;

				// ✅ Filter: only accept logs from THIS plugin's ManualLogSource
				var srcName = eventArgs.Source?.SourceName ?? string.Empty;
				if (!string.Equals(srcName, _allowedSourceName, StringComparison.Ordinal))
					return;

				var lvl = eventArgs.Level.ToString().ToUpperInvariant();
				var msg = eventArgs.Data?.ToString() ?? "";

				var line = $"{_prefix} [{lvl}] {msg}".Trim();
				lock (_buffer)
				{
					_buffer.Enqueue(line);
					if (_buffer.Count > 1000) _buffer.Dequeue(); // cap buffer
				}
			}
			catch { /* best-effort logging, ignore */ }
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
				// swallow — never crash on logging
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

    // -------------- Hot-reload via FileSystemWatcher --------------
    private void StartWatchers()
    {
        _watchSettings = MakeWatcher(SettingsYaml, () => LoadSettings());
        _watchAdmins   = MakeWatcher(AdminsYaml,   () => LoadAdmins());
        _watchIgnore   = MakeWatcher(IgnoreModsYaml, () => LoadIgnoreMods());
        _watchModPatterns = MakeWatcher(ModPatternsYaml, () => LoadModPatterns());
    }

    private void StopWatchers()
    {
        try { _watchSettings?.Dispose(); } catch { }
        try { _watchAdmins?.Dispose(); } catch { }
        try { _watchIgnore?.Dispose(); } catch { }
        try { _watchModPatterns?.Dispose(); } catch { }
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

        Timer t = new Timer(debounceMs);
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
