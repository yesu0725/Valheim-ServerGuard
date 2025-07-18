
# Valheim AntiCheat Server Mod

A server-side mod for Valheim that ensures fair play by checking the following:
- No unauthorized client-side mods
- Character must be registered (prevents use of overpowered local saves)
- Admin SteamIDs can be whitelisted
- Allowed mods can be explicitly ignored
- Violations are tracked and auto-bans are issued after a set number of attempts

## 📦 Features
- Server-only BepInEx mod
- JSON-based configuration
- Easy in-game character registration
- Compatible with FTP server deployments

## 🛠 Installation

1. Unzip this folder into your Valheim server directory.
2. Ensure these paths exist:
   - `BepInEx/plugins/AntiCheatServer/AntiCheatServer.dll`
   - `BepInEx/config/*.json` config files
3. Register characters via in-game console:
   ```
   register_char
   ```
   This binds the currently logged-in character to the SteamID.

## 🧪 Development

To build:
```bash
dotnet build -c Release
```

Or open `AntiCheatServer.sln` in Visual Studio and build the solution.

## 📁 Config Files

- `config.json` - Set max violations before banning.
- `whitelist.json` - Admin SteamIDs exempt from checks.
- `allowed_mods.json` - Client mods allowed on login.
- `registered_characters.json` - Maps character name to SteamID.
- `violations.json` - Tracks each player's violation attempts.

## 🔐 Security Note

This mod does not currently block character file transfers directly. Server-side file hash checks are recommended for deeper validation.
