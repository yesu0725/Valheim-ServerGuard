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
  Changes take effect immediately—no restart required.

- **Discord Logging**  
  If `webhook_url` is set in `anticheat_config.yaml`, violations and errors will also be posted to that Discord channel.

- **Violation Tracking & Auto-Ban**  
  Each rule violation increments a counter per player; reaching the threshold (default 3) results in an automatic ban.

- **Extensible Rule Hooks**  
  Easily add custom checks (e.g. speed hacks, teleport distance, inventory audits) via Harmony patches.

---

## Installation

1. Build with:
   ```dotnet restore
   dotnet build -c Release```
2. Copy AntiCheatServer.dll, YamlDotNet.dll, and Newtonsoft.Json.dll into your Valheim server’s BepInEx/plugins folder.
3. Start your Valheim server.

---

## Configuration

- **anticheat_config.yaml**
# Main AntiCheat settings
# Paste your Discord webhook URL here (only one):
# webhook_url: "https://discord.com/api/webhooks/ABCDEFG123456/abcdefgHIJKLMNOP"
webhook_url: ""

- **anticheat_admins.yaml**
# List of admin Steam IDs (exempt from checks)
# - "76561198000000000"
[]

- **anticheat_registered_chars.yaml**
# Registered characters mapping: characterName: SteamID
# Dummy example:
# MyHero: "76561198000000000"
{}

- **anticheat_allowed_mods.yaml**
# Allowed mods (optional, for mod-reporting clients)
# - "EpicLoot"
[]

---

## Usage

- **Register your character in-game:**
/register_char
This writes your current name → SteamID mapping into `anticheat_registered_chars.yaml`.

- **Edit configs** under BepInEx/config/ as above.
Violations and critical errors will appear in both your server log and (if configured) your Discord channel.

---

## Development

1. Clone the repo and edit Plugin.cs or README.md.
2. Build and test locally:
```dotnet restore
dotnet build -c Release```