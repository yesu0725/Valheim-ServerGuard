# Changelog

## 1.4.0

Companion update for ServerGuard 1.4.0 server. Required version match — install both at the same time.

### What this version adds (player-facing)
- **Anti-cheat blocks.** Cheat console commands (`devcommands`, `god`, `fly`, `spawn`, etc.) are silently blocked while you're on a multiplayer server. Single-player keeps full cheats.
- **Animation-cancel block.** Emote and sheathe inputs during attack recovery are now blocked client-side. Your attacks play out at their normal speed.
- **Death reporting.** When you die, the companion sends a short report to the server (cause + position) so the admin's death log can show the killer.
- **Build-event reporting.** Pieces you place / destroy / hammer-remove are sent to the server's forensic log.
- **Skill-level reporting.** Periodically reports your skill levels so the server can spot impossibly high values.
- **`sg` admin console commands.** If you're an admin (in the server's `admins.yaml`), open the F5 console and type `sg help` for a moderation toolkit.

### Other
- **Modset fingerprint.** Logged on startup so you can verify it matches the server admin's published fingerprint.
- Several Mono compatibility fixes for current Valheim builds.

### Setup reminder
After installing, launch Valheim once. The companion creates `BepInEx/config/ServerGuard/client.yaml` — paste your server's `sharedSecret` value into it and you're done.

## 1.3.0

Initial public release. Reports your mod list to a ServerGuard-protected server so it can let you in.
