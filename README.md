# Valheim AntiCheat Server

A server-side Valheim anti-cheat plugin (BepInEx) that enforces:

- **Admin Whitelist**  
  Specific Steam IDs that bypass all checks.

- **Registered-Character Enforcement**  
  Only characters explicitly registered via `/register_char` or in `anticheat_registered_chars.yaml` may join.

- **Live-Reloadable Configs (YAML)**  
  - **`anticheat_config.yaml`** – main settings, including Discord webhook  
  - **`anticheat_admins.yaml`** – list of admin Steam IDs  
  - **`anticheat_registered_chars.yaml`** – mapping of character names to Steam IDs  
  - **`anticheat_allowed_mods.yaml`** – (optional) allowed client-reported mods  
  Changes take effect immediately—no server restart required.

- **Discord Logging**  
  If `webhook_url` is set in `anticheat_config.yaml`, violations and errors will also be posted to that Discord channel.

- **Violation Tracking & Auto-Ban**  
  Each rule violation increments a counter per player; reaching the threshold (default 3) results in an automatic ban.

- **Extensible Rule Hooks**  
  Easily add custom checks (e.g. speed hacks, teleport distance, inventory audits) via Harmony patches.

---

## Installation

1. Build your plugin:
   ```bash
   dotnet restore
   dotnet build -c Release
   ```
2. Copy the following DLLs into your Valheim server’s `BepInEx/plugins` folder:
   - `AntiCheatServer.dll`  
   - `YamlDotNet.dll`  
   - `Newtonsoft.Json.dll`  
3. Start your Valheim server.

---

## Configuration

All config files live in `BepInEx/config/` and auto-create with helpful comments if missing.

### 1. `anticheat_config.yaml`
```yaml
# Main AntiCheat settings
# Paste your Discord webhook URL here (only one):
# webhook_url: "https://discord.com/api/webhooks/XXXXXXXX/XXXXXXXXXXXXXXXXXXXXXXXX"
webhook_url: ""
```

### 2. `anticheat_admins.yaml`
```yaml
# List of admin Steam IDs (exempt from checks)
# Example:
# - "76561198000000000"
[]
```

### 3. `anticheat_registered_chars.yaml`
```yaml
# Registered characters mapping: characterName: SteamID
# Dummy example:
# MyHero: "76561198000000000"
{}
```

### 4. `anticheat_allowed_mods.yaml`
```yaml
# Allowed mods (optional, for mod-reporting clients)
# Example:
# - "EpicLoot"
[]
```

Edit and save these files; the plugin will reload them automatically—no restart needed.

---

## Usage

- **Register your character in-game:**
  ```text
  /register_char
  ```
  This writes your current `characterName: SteamID` mapping into `anticheat_registered_chars.yaml`.

- **Edit configs** under `BepInEx/config/` as shown above.

Violations and critical errors will appear in both your server log and (if configured) your Discord channel.

---

## Development

1. Clone the repo and edit `Plugin.cs` or `README.md`.  
2. Build and test locally:
   ```bash
   dotnet restore
   dotnet build -c Release
   ```
3. Commit and push your changes:
   ```bash
   git add .
   git commit -m "docs: update README with Discord webhook feature"
   git push origin main
   ```

---
