# Changelog

## 1.6.0

Companion update for ServerGuard 1.6.0 server. Required version match — install both at the same time.

### New: Quick Login panel (optional, off by default)
- Adds a panel to the **title screen** so you can join your server with **one click** — no server browser, no IP entry, no password prompt.
- Clicking **Connect** takes you to character selection; after you pick a character you connect straight to the configured server. (You can also click **Start Game** first, pick a character, then **Connect** — the panel stays on screen.)
- Shows the server **logo** (PNG/JPG), **name**, **description**, and a **live player count** (queried while you sit on the menu).
- Uses the game's own font and button styling.
- Enable and configure it in `BepInEx/config/ServerGuard/client.yaml` (`quickLoginEnabled`, `serverAddress`, `serverPort`, `serverPassword`, `serverName`, `serverDescription`, `serverLogoPath`). Place the logo image in `BepInEx/config/ServerGuard/`.

### New: player-facing
- **Shout reporting.** When you `/s` shout, the companion forwards it so the server can post it to the public Discord channel.
- **Cheat-item removal.** If the server enables it, configured cheat items (e.g. `SwordCheat`, `SledgeCheat`) are removed from your inventory on login unless you're an admin.

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
