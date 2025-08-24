# Valheim ServerGuard

A server-side Valheim security plugin (BepInEx) that enforces fair play and prevents abuse on community servers.

---

## Features

- **Admin Whitelist**  
  Steam IDs listed in `admins.yaml` bypass all checks.

- **No-Mods Enforcement**  
  Detects clients reporting mod-related RPC tokens (e.g. Jotunn, ServerSync, BepInEx).  
  - Allowed mod tokens can be listed in `ignore_mods.yaml`.  
  - Violations are logged and can result in automatic kicks/bans.

- **Registered-Character Enforcement**  
  Each SteamID may only use the registered character names stored in `registrations.yaml`.  
  - New names are auto-registered until the configured limit is reached.  
  - Exceeding the limit triggers violations and may cause a kick.

- **Character Name Limit**  
  Enforces how many distinct character names a single SteamID may use (default: 1).  
  - Prevents abuse via alt characters.  
  - Configurable in `settings.yaml`.

- **Live-Reloadable Configs (YAML)**  
  - **`settings.yaml`** – main settings (thresholds, enforcement, messages)  
  - **`admins.yaml`** – Steam IDs that are exempt from checks  
  - **`ignore_mods.yaml`** – allowed client mod tokens  
  - **`registrations.yaml`** – per-SteamID character name lists  
  - **`violations.yaml`** – auto-maintained record of rule violations  
  All changes take effect instantly without restarting the server.

- **Violation Tracking & Auto-Ban**  
  Each rule violation increments a counter per player.  
  Reaching the threshold (default: 3) results in an automatic ban.  

- **Hot-Reload Support**  
  File changes are detected automatically via watchers.  
  Updated rules apply immediately.

- **Extensible Rule Hooks**  
  The plugin is structured to allow adding new detection rules (e.g. inventory, movement audits) through Harmony patches.

---

## Installation

1. **Build the plugin:**
   ```bash
   dotnet restore
   dotnet build -c Release
   ```
2. **Copy the following files to your Valheim server’s BepInEx/plugins folder:**
   - `ServerGuard.dll`  
   - `YamlDotNet.dll`  
   - `Newtonsoft.Json.dll`  
3. **Start (or restart) your Valheim server.**

---

## Configuration

All config files live in BepInEx/config/ServerGuard/ and auto-create with helpful comments if missing.

### 1. `settings.yaml`
```yaml
# Main ServerGuard settings
violationThreshold: 3
enforce: true
aggressiveNoModCheck: true
requireAttestation: false
kickMessage: "You cannot join: server security policy violation. Contact an administrator."
banReason: "Auto-banned due to repeated security violations."
characterLimit: 1
```

### 2. `admins.yaml`
```yaml
# List of admin Steam IDs (exempt from checks)
admins:
  - "76561198000000000"
```

### 3. `ignore_mods.yaml`
```yaml
# Allowed mod RPC tokens (clients with these will not be flagged)
ignore_mods:
  - "Jotunn"
  - "ServerSync"
```

### 4. `registrations.yaml`
```yaml
# SteamID -> list of allowed character names
registrations:
  "76561198000000000":
    - MyViking
```

### 5. `violations.yaml`
```yaml
# Maintained automatically – do not edit manually
violations:
  "76561198000000000":
    ClientModded: 1
    CharacterNameLimitExceeded: 2
```

Edit and save these files; the plugin will reload them automatically—no restart needed.

---

## Usage

- **Character Registration:**
  - The first character a SteamID uses is automatically registered.
  - Additional characters up to the configured limit are allowed.
  - Beyond the limit, the player will be kicked (if enforcement is enabled).
- **Admin Bypass:**
  - Admins listed in admins.yaml bypass all security checks.
- **Violation Handling:**
  - Violations increment per player and are stored in violations.yaml.
  - After exceeding the threshold, the player is auto-banned.
  - Kicks and bans use the messages configured in settings.yaml.

---