# 📖 Valheim ServerGuard v1.3.0 - Comprehensive Configuration Guide

## 🎯 What is Valheim ServerGuard?

Valheim ServerGuard is a **BepInEx plugin** that enforces no-mods policies on your Valheim server. It uses **hybrid detection** with:

- **Phase 1:** RPC Token + Version Keyword Detection
- **Phase 2:** Assembly Namespace Scanning (optional)
- **Metrics:** Real-time detection statistics
- **Whitelist/Blocklist Modes:** Flexible enforcement policies

---

## 📋 Quick Start

### 1. Install ServerGuard
- Download the latest release
- Place `Plugin.cs` in `BepInEx/plugins/ServerGuard/`
- Start your server once to generate config files

### 2. Configure
Edit `BepInEx/config/ServerGuard/conf/settings.yaml`:

```yaml
enforce: true                  # Enable enforcement
aggressiveNoModCheck: true     # Enable mod detection
useWhitelistMode: false        # Blocklist mode (default)
enableMetrics: true            # Track statistics
discordWebhookUrl: ""          # Optional: Discord logging
```

### 3. Manage Mods
Edit `BepInEx/config/ServerGuard/conf/ignore_mods.yaml`:

```yaml
# Blocklist mode: mods you ALLOW
ignore_mods:
  - Jotunn
  - ServerSync
```

### 4. Run & Monitor
- Watch `metrics.yaml` for detection statistics
- Check Discord for violations (if webhook configured)
- Adjust patterns in `mod_patterns.yaml` based on results

---

## ⚙️ Configuration Files

### settings.yaml
Main plugin settings and enforcement policy.

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enforce` | bool | true | Enable/disable kicks and bans |
| `ViolationThreshold` | int | 3 | Violations before auto-ban |
| `AggressiveNoModCheck` | bool | true | Enable mod detection |
| `EnableAssemblyScanning` | bool | true | Enable Phase 2 (expensive) |
| `EnableMetrics` | bool | true | Track detection statistics |
| `UseWhitelistMode` | bool | false | Allowlist instead of blocklist |
| `CharacterLimit` | int | 1 | Max characters per SteamID |
| `KickMessage` | string | "..." | Custom kick message |
| `BanReason` | string | "..." | Custom ban reason |
| `discordWebhookUrl` | string | "" | Discord webhook URL |
| `discordChannelLink` | string | "" | Human-friendly channel reference |

---

## 🛡️ Blocklist vs Whitelist Mode

### Blocklist Mode (Default)
```yaml
# settings.yaml
useWhitelistMode: false
```

**Behavior:** Block ALL mods except those listed

```yaml
# ignore_mods.yaml
ignore_mods:
  - Jotunn          # ✅ ALLOWED
  - ServerSync      # ✅ ALLOWED
  # ValheimPlus NOT listed → ❌ BLOCKED
```

**When to use:** Vanilla-only servers with minor QoL exceptions

---

### Whitelist Mode (NEW)
```yaml
# settings.yaml
useWhitelistMode: true
```

**Behavior:** Allow ONLY mods listed (everything else blocked)

```yaml
# ignore_mods.yaml
ignore_mods:
  - Jotunn                    # ✅ ALLOWED
  - ServerSync                # ✅ ALLOWED
  - EquipmentAndQuickSlots    # ✅ ALLOWED
  # ValheimPlus NOT listed → ❌ BLOCKED
```

**When to use:** Curated mod pack servers with specific approved mods

---

## 📊 Metrics & Statistics

### metrics.yaml
Auto-updated detection statistics (if `enableMetrics: true`).

```yaml
total_players_checked: 127
total_mods_detected: 34
phase1_rpc_detections: 28
phase2_assembly_detections: 5
version_keyword_detections: 1
allowlist_bypasses: 8
admin_bypasses: 3
violations_issued: 12
players_banned: 1
top_detected_mods:
  Jotunn: 12
  ValheimPlus: 8
  ServerSync: 7
  PlanBuild: 5
last_updated: 2026-05-07T18:00:00Z
```

**How to use:**
- **track trends** - See which mods appear most
- **tune patterns** - Add frequently-missed mods to `mod_patterns.yaml`
- **analyze false positives** - Identify mods being incorrectly flagged

---

## 🔍 Mod Patterns Configuration

### mod_patterns.yaml

Three detection pattern types work together:

#### 1. RPC Tokens (Phase 1)
```yaml
rpc_tokens:
  - JVL              # Jotunn framework
  - ServerSync       # Config sync
  - ValheimPlus      # QoL mod
  - MyCustomMod      # Your mod token
```

**What it does:** Checks if player's RPC method names contain these strings

**How to find tokens:**
1. Look at mod's source code on GitHub
2. Search for `RegisterRPC()` or `AddRPC()`
3. Check the method prefix (e.g., "JVL_MyMethod" → token is "JVL")
4. Test: enable mod locally, check ServerGuard logs for detected tokens

**Performance:** ⚡ Very fast (O(n) string matching)

---

#### 2. Assembly Namespaces (Phase 2)
```yaml
assembly_namespaces:
  - Jotunn           # Jotunn.Core.*, Jotunn.Managers.*
  - ValheimPlus      # ValheimPlus.*, ValheimPlus.*
  - MyMod            # MyMod.*, MyMod.Patches.*
```

**What it does:** Scans all loaded .NET assemblies for types in these namespaces

**When it runs:**
- After Phase 1 (only if Phase 1 finds nothing)
- Optional - toggle with `enableAssemblyScanning: true/false`

**When to use:**
- Catching mods that hide their RPC tokens
- Strict vanilla-only enforcement

**Performance:** ⚠️ Expensive (reflection overhead)
- Disable for performance: `enableAssemblyScanning: false`
- Enable for strict enforcement: `enableAssemblyScanning: true`

---

#### 3. Version Keywords (Phase 1)
```yaml
version_keywords:
  - mod
  - modded
  - custom
  - patched
  - enhanced
  - unofficial
```

**What it does:** Checks player version string for these keywords

**Example:**
```
Player version: "0.217.46-modded"      → Keyword "modded" detected ✅
Player version: "0.217.46-custom"      → Keyword "custom" detected ✅
Player version: "0.217.46"             → No keywords detected ✅
```

**Performance:** ⚡ Very fast (string search)

---

## 📝 Default Mod Tokens

### Framework Mods
- `JVL` / `Jotunn` - Mod framework
- `ServerSync` - Config synchronization
- `BepInEx` - BepInEx framework
- `ModVer` / `ModInfo` - Mod detection

### Quality of Life
- `ValheimPlus` - Many QoL features
- `EquipmentAndQuickSlots` - Quick slots
- `ImprovedUI` - UI enhancements
- `Quickslots` - Item quick access

### Building & Decoration
- `PlanBuild` - Plan placement
- `BuildCamera` - Build camera
- `GizmoRotate` - Rotation tool

### Gameplay Enhancement
- `Wonderlands` - World generation
- `Komrade` - Co-op features
- `EpicLoot` - Loot system
- `Seasons` - Season system
- `CustomUI` - UI customization
- `CreatureAbilityReworks` - Combat changes

### Progression
- `MaserySystem` - Mastery progression
- `Experience` - XP system
- `SkillTree` - Skill trees

### And 15+ more...

---

## 🚀 Example Configurations

### Configuration 1: Vanilla-Only Server
Strict - only essential QoL allowed

```yaml
# settings.yaml
enforce: true
aggressiveNoModCheck: true
useWhitelistMode: false
enableAssemblyScanning: true
enableMetrics: true

# ignore_mods.yaml
ignore_mods:
  - Jotunn
  - ServerSync
```

**Result:** 
- ✅ Jotunn/ServerSync clients allowed
- ❌ ValheimPlus, PlanBuild, EpicLoot blocked
- 📊 Metrics track all violations

---

### Configuration 2: Curated Mod Pack Server
Whitelist mode with specific approved mods

```yaml
# settings.yaml
enforce: true
useWhitelistMode: true
enableAssemblyScanning: false

# ignore_mods.yaml (ONLY these allowed)
ignore_mods:
  - Jotunn
  - ServerSync
  - ValheimPlus
  - PlanBuild
  - EquipmentAndQuickSlots
  - MinimapAssistant
```

**Result:**
- ✅ ONLY listed mods allowed
- ❌ Any other mod blocked
- 🎯 Perfect for modded servers with specific packs

---

### Configuration 3: Development/Testing Server
Permissive - log violations without enforcement

```yaml
# settings.yaml
enforce: false
aggressiveNoModCheck: true
enableMetrics: true
enableAssemblyScanning: false
discordWebhookUrl: "your-webhook-url"

# ignore_mods.yaml
ignore_mods: []
```

**Result:**
- 📋 Log all mod detections without kicking
- 📊 Track statistics for tuning
- 🔔 Discord alerts for analysis

---

## 🔧 Customizing Patterns

### Add a New Mod Token

1. **Find the mod's RPC token:**
   - Check GitHub repo for `RegisterRPC()` or `AddRPC()`
   - Example: `rpc.Register("MyMod_DoSomething")`  → token is `MyMod`

2. **Edit `mod_patterns.yaml`:**
   ```yaml
   rpc_tokens:
     - JVL
     - ServerSync
     - MyMod        # ← Add your token
   ```

3. **Save & reload:**
   - Changes auto-reload (watch for "Reloaded: mod_patterns.yaml" in logs)
   - No server restart needed!

4. **Verify:**
   - Check `metrics.yaml` for `top_detected_mods`
   - Confirm new mod appears in statistics

---

### Add a New Assembly Namespace

1. **Find the mod's namespace:**
   - Check GitHub or mod DLL with dotPeek/ILSpy
   - Example: `namespace ValheimPlusModified { ... }` → namespace is `ValheimPlusModified`

2. **Edit `mod_patterns.yaml`:**
   ```yaml
   assembly_namespaces:
     - Jotunn
     - ValheimPlus
     - ValheimPlusModified    # ← Add your namespace
   ```

3. **Enable Phase 2:**
   ```yaml
   # settings.yaml
   enableAssemblyScanning: true
   ```

4. **Test & verify**

---

## 📊 Reading Metrics

### Common Patterns

**Most detections from Phase 1 (RPC)?**
```yaml
phase1_rpc_detections: 28
phase2_assembly_detections: 5
```
→ RPC token detection working well, Phase 2 rarely needed

**High Phase 2 detections?**
```yaml
phase1_rpc_detections: 5
phase2_assembly_detections: 25
```
→ Some mods hide RPC tokens; Phase 2 necessary for enforcement

**New mods appearing?**
```yaml
top_detected_mods:
  UnknownMod: 3
```
→ Add "UnknownMod" to patterns or allowlist

---

## 🐛 Troubleshooting

### False Positive: Vanilla clients kicked
**Symptom:** "You cannot join: server security policy violation"

**Solution:**
1. Run in log-only mode:
   ```yaml
   enforce: false
   ```
2. Check logs for what triggered it
3. If version keyword: disable or customize
   ```yaml
   version_keywords: []  # Disable version checks
   ```
4. If RPC token: remove from patterns or whitelist
5. If assembly: disable Phase 2
   ```yaml
   enableAssemblyScanning: false
   ```

---

### False Negative: Modded client passes through
**Symptom:** Known modded client joins without violation

**Solution:**
1. Check logs for client's RPC tokens
2. Add missing token to `mod_patterns.yaml`
3. Check metrics for the mod name:
   ```yaml
   top_detected_mods:
     HiddenMod: 0  # Not in patterns
   ```
4. If assembly-based: enable Phase 2
   ```yaml
   enableAssemblyScanning: true
   ```

---

### Performance Issues
**Symptom:** Server lag after ServerGuard starts

**Solution:**
1. Disable Phase 2 assembly scanning:
   ```yaml
   enableAssemblyScanning: false
   ```
2. Disable metrics (minor impact):
   ```yaml
   enableMetrics: false
   ```
3. Disable Discord logging temporarily:
   ```yaml
   discordWebhookUrl: ""
   ```

---

## 📝 Admin Whitelist

Admins bypass ALL checks:

```yaml
# admins.yaml
admins:
  - 76561198012345678  # Server owner
  - 76561198087654321  # Mod tester
```

**How to get SteamID:**
1. Visit https://steamid.io/
2. Enter Steam username
3. Copy the "steamID64" value

---

## 🔗 Useful Resources

- **Valheim Modding:** https://github.com/Valheim-Modding
- **Jotunn Framework:** https://github.com/Valheim-Modding/Jotunn
- **BepInEx:** https://github.com/BepInEx/BepInEx
- **ServerGuard Repository:** https://github.com/yesu0725/Valheim-ServerGuard

---

## 💡 Tips & Best Practices

1. **Start Conservative**
   - Begin with strict vanilla-only
   - Gradually allowlist trusted mods
   - Use log-only mode first

2. **Monitor Metrics**
   - Review `metrics.yaml` weekly
   - Track new detected mods
   - Adjust patterns based on trends

3. **Test Changes**
   - Edit patterns while running
   - Changes auto-reload instantly
   - Check logs for confirmation

4. **Use Discord**
   - Get real-time alerts for violations
   - Archive enforcement logs
   - Share stats with admins

5. **Document Your Policy**
   - Post allowed mods list publicly
   - Explain enforcement approach
   - Provide Discord webhook for appeals

---

## ✅ Verification Checklist

- [ ] Plugin installed in `BepInEx/plugins/`
- [ ] Config files auto-generated in `BepInEx/config/ServerGuard/conf/`
- [ ] `settings.yaml` configured for your server policy
- [ ] `ignore_mods.yaml` populated with allowed mods
- [ ] Admin SteamIDs in `admins.yaml`
- [ ] Discord webhook configured (optional)
- [ ] Metrics enabled and tracking (`metrics.yaml` updating)
- [ ] Test with modded client - should trigger violation
- [ ] Test with vanilla client - should pass
- [ ] Check logs for "ServerGuard Loaded" message

---

## 📢 Support

For issues, feature requests, or mod token submissions:
- Open an issue on GitHub
- Include relevant logs from `BepInEx/LogOutput.log`
- Attach metrics from `metrics.yaml`

---

**Last Updated:** May 7, 2026
**Version:** 1.3.0
**Author:** taeguk
