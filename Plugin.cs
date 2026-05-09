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
    private static readonly string IgnoreModsYaml    = Path.Combine(ConfDir, "ignore_mods.yaml");
    private static readonly string ModPatternsYaml   = Path.Combine(ConfDir, "mod_patterns.yaml");
    private static readonly string RegistrationsYaml = Path.Combine(ConfDir, "registrations.yaml");
    private static readonly string ViolationsYaml    = Path.Combine(ConfDir, "violations.yaml");
    private static readonly string MetricsYaml       = Path.Combine(ConfDir, "metrics.yaml");

    // -------- YAML Serializer --------
    private static IDeserializer _yamlIn;
    private static ISerializer _yamlOut;

    // -------- In-memory state --------
    private Settings _settings;
    private HashSet<string> _admins = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _ignoredModTokens = new(StringComparer.OrdinalIgnoreCase);
    private ModPatterns _modPatterns;
    private DetectionMetrics _metrics;

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
        public bool EnableAssemblyScanning { get; set; } = true; // Check loaded assemblies
        public bool EnableMetrics        { get; set; } = true;   // NEW: Track detection stats
        public bool UseWhitelistMode     { get; set; } = false;  // NEW: Allowlist instead of blocklist
        public bool RequireAttestation   { get; set; } = false;
        public string KickMessage        { get; set; } = "You cannot join: server security policy violation. Contact an administrator.";
        public string BanReason          { get; set; } = "Auto-banned due to repeated security violations.";
		public int CharacterLimit        { get; set; } = 1;
		public string discordWebhookUrl  { get; set; } = "";
		public string discordChannelLink { get; set; } = "";
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
        LoadIgnoreMods();
        LoadModPatterns();
        LoadRegistrations();
        LoadViolations();
        LoadMetrics();

        // Start file watchers for hot-reload
        StartWatchers();

        // Harmony patches
        _harmony = new Harmony("com.taeguk.valheim.serverguard");
        _harmony.PatchAll();

        var modeStr = _settings.UseWhitelistMode ? "WHITELIST" : "BLOCKLIST";
        LogS.LogInfo($"[ServerGuard] Loaded (v1.3.0). Enforcement: {(_settings.Enforce ? "ON" : "LOG-ONLY")}. Mode: {modeStr}. Assembly scanning: {(_settings.EnableAssemblyScanning ? "ON" : "OFF")}. Metrics: {(_settings.EnableMetrics ? "ON" : "OFF")}");
		
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
				discordWebhookUrl = "",
				discordChannelLink = ""
			};
			var sb = new StringBuilder();
			sb.AppendLine("# ServerGuard settings (v1.3.0)");
			sb.AppendLine("# discordWebhookUrl: paste the FULL Discord Webhook URL from: Channel Settings → Integrations → Webhooks");
			sb.AppendLine("# discordChannelLink: optional, for your reference");
			sb.AppendLine("# enableAssemblyScanning: enable/disable assembly namespace scanning");
			sb.AppendLine("# enableMetrics: enable/disable detection metrics tracking");
			sb.AppendLine("# useWhitelistMode: if TRUE, only mods in ignore_mods.yaml are allowed (allowlist). If FALSE, all mods except ignored are blocked (blocklist)");
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

        if (!File.Exists(IgnoreModsYaml))
        {
            var doc = new IgnoreModsDoc { ignore_mods = new List<string> { "Jotunn", "ServerSync" } };
            var sb = new StringBuilder();
            sb.AppendLine("# Blocklist mode: mods to permit (ignored/allowlisted)");
            sb.AppendLine("# Whitelist mode: mods that are allowed (ONLY these mods permitted)");
            sb.AppendLine(_yamlOut.Serialize(doc));
            File.WriteAllText(IgnoreModsYaml, sb.ToString());
        }

        if (!File.Exists(ModPatternsYaml))
        {
            var doc = new ModPatterns
            {
                rpc_tokens = new List<string>
                {
                    // Framework mods
                    "JVL", "Jotunn",
                    "ServerSync",
                    "BepInEx",
                    "ModVer",
                    "ModInfo",
                    
                    // Quality of life
                    "ValheimPlus",
                    "EquipmentAndQuickSlots",
                    "ImprovedUI",
                    "Quickslots",
                    
                    // Gameplay enhancement
                    "Wonderlands",
                    "Komrade",
                    "EpicLoot",
                    "Seasons",
                    "CustomUI",
                    "CreatureAbilityReworks",
                    "CarryMore",
                    "RackMultiplier",
                    "NoMapBoat",
                    
                    // Building & decoration
                    "PlanBuild",
                    "BuildCamera",
                    "GizmoRotate",
                    
                    // Progression
                    "MaserySystem",
                    "Experience",
                    "SkillTree",
                    
                    // Combat
                    "CombatEvolution",
                    "BalancedArchers",
                    "ArcheryParry",
                    
                    // World modifications
                    "MinimapAssistant",
                    "Minimap",
                    "WorldMapAlternative",
                    
                    // Other common mods
                    "ItemDrawers",
                    "ContainerGui",
                    "MultiUserChest",
                    "BetterNetworking",
                    "Pathfinding"
                },
                assembly_namespaces = new List<string>
                {
                    // Framework
                    "Jotunn",
                    "BepInEx",
                    
                    // QoL/UI
                    "ValheimPlus",
                    "ImprovedUI",
                    "EquipmentAndQuickSlots",
                    
                    // Gameplay
                    "Wonderlands",
                    "Komrade",
                    "EpicLoot",
                    "Seasons",
                    "CustomUI",
                    "CreatureAbilityReworks",
                    
                    // Building
                    "PlanBuild",
                    "BuildCamera",
                    "GizmoRotate",
                    
                    // Progression
                    "MaserySystem",
                    "SkillTree",
                    
                    // Combat
                    "CombatEvolution",
                    "BalancedArchers",
                    
                    // World
                    "MinimapAssistant",
                    "WorldMapAlternative",
                    
                    // Storage
                    "ItemDrawers",
                    "ContainerGui",
                    "MultiUserChest"
                },
                version_keywords = new List<string>
                {
                    "mod",
                    "modded",
                    "custom",
                    "patched",
                    "enhanced",
                    "modified"
                }
            };
            var sb = new StringBuilder();
            sb.AppendLine("# Mod detection patterns (Phase 1 & 2)");
            sb.AppendLine("# See README-MOD-PATTERNS.md for detailed documentation");
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

        if (!File.Exists(MetricsYaml))
        {
            var doc = new DetectionMetrics();
            var sb = new StringBuilder();
            sb.AppendLine("# ServerGuard Detection Metrics (auto-updated)");
            sb.AppendLine(_yamlOut.Serialize(doc));
            File.WriteAllText(MetricsYaml, sb.ToString());
        }

        // Create README for mod patterns
        CreateModPatternsREADME();
    }

    private void CreateModPatternsREADME()
    {
        var readmePath = Path.Combine(ConfDir, "README-MOD-PATTERNS.md");
        if (File.Exists(readmePath)) return;

        var content = @"# Mod Patterns Configuration Guide

## Overview
Valheim ServerGuard uses **hybrid detection** with two phases to identify modded clients:

- **Phase 1:** RPC Token Detection + Version Keyword Scanning
- **Phase 2:** Assembly Namespace Scanning (optional, toggleable)

This file configures detection patterns for both phases.

---

## Modes: Blocklist vs Whitelist

### Blocklist Mode (Default)
- **Setting:** `useWhitelistMode: false`
- **Behavior:** Block all detected mods EXCEPT those in `ignore_mods.yaml`
- **Use Case:** Strict vanilla-only servers with exceptions for quality-of-life mods

```yaml
# ignore_mods.yaml (Blocklist mode)
ignore_mods:
  - Jotunn           # Allow framework
  - ServerSync       # Allow config sync
  - MinimapAssistant # Allow minimap
```

### Whitelist Mode (NEW)
- **Setting:** `useWhitelistMode: true`
- **Behavior:** Allow ONLY mods in `ignore_mods.yaml`; block everything else
- **Use Case:** Community servers with specific approved mod packs

```yaml
# ignore_mods.yaml (Whitelist mode)
ignore_mods:
  - Jotunn
  - ServerSync
  - MinimapAssistant
  - EquipmentAndQuickSlots
```

Switch between modes by editing `settings.yaml`:

```yaml
useWhitelistMode: false  # Blocklist (default)
useWhitelistMode: true   # Whitelist mode
```

---

## Configuration

### rpc_tokens
RPC method names that indicate a mod is present. ServerGuard checks if any registered RPC method contains one of these tokens (case-insensitive substring match).

**Default tokens:**
- `JVL` / `Jotunn` → Jotunn framework
- `ServerSync` → Config sync mod
- `BepInEx` → BepInEx framework
- `ValheimPlus` → ValheimPlus QoL mod
- `PlanBuild` / `BuildCamera` → Building mods
- `EpicLoot` → Loot system mod
- And 20+ more...

**How to add tokens:**
1. Open `mod_patterns.yaml`
2. Find the `rpc_tokens:` section
3. Add new tokens as list items:

```yaml
rpc_tokens:
  - JVL
  - Jotunn
  - MyCustomMod        # Add your token here
  - AnotherModToken
```

**How to find mod tokens:**
- Check the mod's GitHub repository or README
- Look for custom RPC method prefixes
- Test with the mod enabled and check ServerGuard logs

---

### assembly_namespaces
.NET namespace prefixes that indicate a mod assembly is loaded. Phase 2 assembly scanning checks all loaded DLLs for types matching these namespaces.

**Default namespaces:**
- `Jotunn` → Jotunn framework types
- `ValheimPlus` → ValheimPlus namespace
- `PlanBuild` → Building enhancement
- And 15+ more...

**How to add namespaces:**
```yaml
assembly_namespaces:
  - Jotunn
  - ValheimPlus
  - MyMod            # Add your namespace prefix
  - Another.Mod.Type
```

**When Phase 2 runs:**
- Toggled with `enableAssemblyScanning: true/false` in `settings.yaml`
- Runs AFTER Phase 1 if Phase 1 detects nothing
- More expensive (reflection overhead) but catches hidden mods
- Disable for performance: `enableAssemblyScanning: false`

---

### version_keywords
Keywords found in player version strings that suggest a modded client.

**Default keywords:**
- `mod`
- `modded`
- `custom`
- `patched`
- `enhanced`
- `modified`

**Example:**
If a player's client version string is `""0.217.46-modded""`, the keyword `modded` is detected.

**Add custom keywords:**
```yaml
version_keywords:
  - mod
  - custom
  - hacked          # Add new keyword
  - unofficial
```

---

## Best Practices

### 1. Start Conservative
Begin with a small allowlist of trusted mods, expand as needed:

```yaml
# Blocklist mode - start strict
ignore_mods:
  - Jotunn
  - ServerSync
```

### 2. Monitor Metrics
Enable metrics to see detection patterns:

```yaml
# settings.yaml
enableMetrics: true
```

Check `metrics.yaml` periodically:
```yaml
total_mods_detected: 42
phase1_rpc_detections: 35
phase2_assembly_detections: 7
top_detected_mods:
  Jotunn: 15
  ValheimPlus: 12
  ServerSync: 10
```

### 3. Test Before Enforcement
Run in log-only mode first:

```yaml
# settings.yaml
enforce: false  # Only log, don't kick
```

### 4. Use Discord Logging
Configure webhooks to monitor violations in real-time:

```yaml
# settings.yaml
discordWebhookUrl: https://discordapp.com/api/webhooks/...
```

### 5. Whitelist Admins
Add admin Steam IDs to bypass all checks:

```yaml
# admins.yaml
admins:
  - 76561198012345678  # Admin user
  - 76561198087654321  # Mod tester
```

---

## Troubleshooting

### False Positives (Vanilla client detected as modded)
**Problem:** Vanilla clients getting kicked
**Solution:**
1. Check logs for which token/namespace triggered it
2. Adjust version keywords if issue is version-based
3. Run with `enforce: false` to diagnose
4. Consider disabling Phase 2: `enableAssemblyScanning: false`

### False Negatives (Modded client passes through)
**Problem:** Modded clients not detected
**Solution:**
1. Add the mod's RPC token to `rpc_tokens`
2. Add the mod's namespace to `assembly_namespaces`
3. Enable Phase 2 assembly scanning if disabled
4. Check Discord logs for what mods are commonly missed

### Performance Issues
**Problem:** Server laggy after updates
**Solution:**
1. Disable assembly scanning: `enableAssemblyScanning: false`
2. Disable metrics: `enableMetrics: false`
3. Disable Discord logging temporarily
4. Check which phase is causing slowdown in logs

---

## Example Configurations

### Vanilla-Only Server
```yaml
# settings.yaml
useWhitelistMode: false
aggressiveNoModCheck: true
enforce: true

# ignore_mods.yaml
ignore_mods:
  - Jotunn
  - ServerSync
```

### Curated Mod Pack Server
```yaml
# settings.yaml
useWhitelistMode: true
enforce: true

# ignore_mods.yaml (ONLY these allowed)
ignore_mods:
  - Jotunn
  - ServerSync
  - ValheimPlus
  - MinimapAssistant
  - EquipmentAndQuickSlots
```

### Development/Testing Server
```yaml
# settings.yaml
aggressiveNoModCheck: false
enforce: false  # Log only
enableMetrics: true
enableDiscordWebhook: true
```

---

## Updates

When ServerGuard updates `mod_patterns.yaml`, it adds new tokens for recently-detected mods.
You can safely edit the file; hot-reload will apply changes instantly.

**Check for updates:**
- Monitor GitHub releases
- Subscribe to mod community discussions
- Review metrics.yaml for new detected mods

---

For questions or contributions, open an issue on GitHub!
";

        try
        {
            File.WriteAllText(readmePath, content);
            LogS.LogInfo("[ServerGuard] Created README-MOD-PATTERNS.md");
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] Failed to create README: {ex.Message}");
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
            LogS.LogInfo($"[ServerGuard] ignore_mods.yaml loaded ({_ignoredModTokens.Count} tokens) - Mode: {(_settings.UseWhitelistMode ? "WHITELIST" : "BLOCKLIST")}");
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
            
            _modPatterns.rpc_tokens ??= new List<string>();
            _modPatterns.assembly_namespaces ??= new List<string>();
            _modPatterns.version_keywords ??= new List<string>();
            
            LogS.LogInfo($"[ServerGuard] mod_patterns.yaml loaded ({_modPatterns.rpc_tokens.Count} RPC tokens, {_modPatterns.assembly_namespaces.Count} namespaces, {_modPatterns.version_keywords.Count} keywords)");
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

        LogS.LogWarning($"[ServerGuard] {platformId} violated {rule}. Count={map[rule]}/{_settings.ViolationThreshold}");
		_ = SendDiscordNow($":warning: Violation by {platformId} — **{rule}** ({map[rule]}/{_settings.ViolationThreshold})");

        if (_settings.Enforce && map[rule] >= _settings.ViolationThreshold)
        {
            TryBan(platformId, _settings.BanReason);
            if (_settings.EnableMetrics)
            {
                _metrics.players_banned++;
                SaveMetrics();
            }
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
                }

                if (Plugin.Instance._settings.AggressiveNoModCheck)
                {
                    if (Plugin.Instance.DetectLikelyModdedClient(peer, out var reason, out var matchedToken))
                    {
                        bool isAllowed = Plugin.Instance._ignoredModTokens.Contains(matchedToken);

                        // Blocklist mode: allowed = in ignore list
                        // Whitelist mode: allowed = NOT in ignore list means it's blocked (opposite logic)
                        if (Plugin.Instance._settings.UseWhitelistMode)
                        {
                            isAllowed = !isAllowed; // Invert for whitelist mode
                        }

                        if (isAllowed && !string.IsNullOrEmpty(matchedToken))
                        {
                            Plugin.LogS.LogInfo($"[ServerGuard] Detected mod token '{matchedToken}' but it is allowed (ignore list).");
                            if (Plugin.Instance._settings.EnableMetrics)
                            {
                                Plugin.Instance._metrics.allowlist_bypasses++;
                                Plugin.Instance.SaveMetrics();
                            }
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
					Plugin.LogS.LogInfo($"[ServerGuard] Registered character #{names.Count}/{limit} for {steamId} -> '{charName}'");
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
                        if (_modPatterns?.rpc_tokens != null && _modPatterns.rpc_tokens.Count > 0)
                        {
                            foreach (var token in _modPatterns.rpc_tokens)
                            {
                                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    matchedToken = token;
                                    reason = $"RPC token matched: {token}";
                                    LogS.LogWarning($"[ServerGuard] Phase 1 detection (RPC): {reason} (method: {name})");
                                    RecordMetricDetection(token, "RPC");
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
                    matchedToken = detectedMods[0];
                    reason = $"Assembly namespaces detected: {string.Join(", ", detectedMods)}";
                    LogS.LogWarning($"[ServerGuard] Phase 2 detection (Assembly): {reason}");
                    RecordMetricDetection(matchedToken, "Assembly");
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
                            RecordMetricDetection(keyword, "Version");
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

    private List<string> ScanPeerAssemblies(ZNetPeer peer)
    {
        var detectedMods = new List<string>();

        try
        {
            var domain = AppDomain.CurrentDomain;
            var loadedAssemblies = domain.GetAssemblies();

            if (_modPatterns?.assembly_namespaces == null || _modPatterns.assembly_namespaces.Count == 0)
                return detectedMods;

            foreach (var assembly in loadedAssemblies)
            {
                try
                {
                    var types = assembly.GetTypes();

                    foreach (var type in types)
                    {
                        try
                        {
                            var typeName = type.FullName ?? string.Empty;

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
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            LogS.LogWarning($"[ServerGuard] ScanPeerAssemblies error: {ex.Message}");
        }

        return detectedMods;
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
