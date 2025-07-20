# Valheim AntiCheat Server

A server-side Valheim anti-cheat plugin (BepInEx) that enforces:

- **Admin Whitelist**  
  Specific Steam IDs that bypass all checks.

- **Registered-Character Enforcement**  
  Only characters explicitly registered via `/register_char` or in `anticheat_registered_chars.yaml` may join.

- **Live-Reloadable Configs (YAML)**  
  - **`anticheat_admins.yaml`** – list of admin Steam IDs  
  - **`anticheat_registered_chars.yaml`** – mapping of character names to Steam IDs  
  - **`anticheat_allowed_mods.yaml`** – (optional) list of allowed client-reported mods  
  Changes to any of these files take effect immediately—no server restart required.

- **Violation Tracking & Auto-Ban**  
  Each rule violation increments a counter per player; reaching the threshold (default 3) results in an automatic ban.

- **Extensible Rule Hooks**  
  Easily add custom checks (e.g. speed hacks, teleport distance, inventory audits) via Harmony patches.

---

## Installation

1. Build with `dotnet build -c Release`.  
2. Copy `AntiCheatServer.dll` (and `YamlDotNet.dll`) into `BepInEx/plugins`.  
3. Start your Valheim server.

---

## Basic Usage

- **Register your character** in-game:
/register_char
This writes your current name → SteamID mapping into `anticheat_registered_chars.yaml`.

- **Edit configs** under `BepInEx/config/`:
```yaml
# anticheat_admins.yaml
# Example admin IDs:
# - "76561199000000000"
- "76561199062837584"

# anticheat_registered_chars.yaml
# Example:
# MyCharName: "76561199062837584"
MyCharName: "76561199062837584"

# anticheat_allowed_mods.yaml
# Example:
# - "Jotunn"