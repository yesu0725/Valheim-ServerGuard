# Changelog

## 1.7.0

Feature release. Install alongside the 1.7.0 server plugin — this version enforces the server's new console policy, and an older companion will ignore it.

### New
- **Console guard.** The server decides what the F5 console may run: everything (as before), a restricted set, a whitelist, or nothing at all — the console can be locked shut entirely. Blocked commands print a short explanation instead of silently doing nothing. Servers that don't set a policy behave exactly as they did in 1.6.3, and single-player is never affected.
- **Wider command coverage.** The blocked set grew from 27 to roughly 90 commands, split into cheat commands and non-cheat commands that still change shared world state (`nomap`, `noportals`, `setworldmodifier`, `setworldpreset`, `optterrain`, `resetsharedmap`, `printseeds` and others). Chat, emotes, `/s`, `/w`, display settings and the crafting-list commands are untouched.
- **Key binds are cleared while you're on a guarded server.** If your server sets a bind policy, your custom console key binds are removed for that session and the `bind` command is blocked. They come back on their own in single-player — the mod clears the live list, not your saved settings. (A server can choose `wipe`, which does delete them permanently; you'll get a message in the console when binds are removed either way.)
- **Everything is restored when you disconnect.** Leaving a server that locked the console down gives you your console and binds back immediately — no restart needed.

### Fixed
- **Cheat commands added by other mods are now detected.** The check that asks Valheim "is this a cheat command?" was reading the wrong internal table and always came back no, so it only ever caught the hard-coded list. It now reads the real command registry, which means cheat commands registered by any mod are caught too.

## 1.6.3

Bug-fix release. Install alongside the 1.6.3 server plugin.

### Fixed
- **Shouting works again on servers that set `enableArrivalShout: false`.** The 1.6.2 filter was meant to swallow only the automatic "I have arrived!" line on first spawn, but it swallowed every shout — players on those servers couldn't use `/s` at all. It decided by asking "did the respawn update run this frame", and the game runs that update on every frame, so the answer was always yes. It now brackets the actual call instead, and gives up after suppressing one shout per session. Servers that leave `enableArrivalShout` at its default were unaffected either way.

## 1.6.2

Install alongside the 1.6.2 server plugin.

### Changed
- **Sheathing your weapon is no longer blocked mid-attack.** Holstering was treated as an attack-cancel exploit, but weapon swaps, picking up items, opening chests and building all holster the weapon — so honest play was being blocked and reported. Only the emote cancel is blocked now.
- **The "I have arrived!" shout can be turned off by the server.** If your server sets `enableArrivalShout: false`, the companion swallows the automatic first-spawn shout. You can still shout manually. Servers that don't set it keep vanilla behaviour.

## 1.6.1

Bug-fix release. Install alongside the 1.6.1 server plugin.

### Fixed
- **Quick Login player count no longer shows `?`.** The panel's live player count always displayed `Players: ?` even when the server was up and reachable. The server-info query never answered the challenge packet that Valve's query protocol has required since December 2020, so every reply was discarded. The panel now shows the real count (and max players).
- **Player count queries the right port.** It now tries the Steam query port (game port + 1, i.e. 2457 for a default 2456 server) first instead of last, so the count appears immediately instead of after a wasted timeout.
- **No more title-screen stutter.** The query used to run on the main thread and could freeze the menu for several seconds while it waited for a reply. It now runs in the background.
- If the query genuinely can't reach the server (firewall, wrong port, server offline), the panel still shows `Players: ?` — but the log now records which host and port were tried so it can be diagnosed.

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
