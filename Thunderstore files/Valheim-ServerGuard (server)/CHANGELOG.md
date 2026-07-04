# Changelog

## 1.4.0

Big feature drop — anti-cheat, admin tools, and Discord cleanup.

### New: anti-cheat
- **Devcommands gate.** Players can't type `devcommands`, `god`, `fly`, `spawn`, etc. on your server. Vanilla cheat commands are blocked client-side and reported to you.
- **Animation-cancel gate.** Blocks the classic emote / sheathe attack-cancel exploit (used to spam secondary attacks faster than vanilla allows).
- **Movement-speed sanity check.** Flags players moving impossibly fast across the ground.
- **Inventory validation.** Flags unknown items and over-sized stacks server-side.
- **Skill-level cap.** Catches players with skill levels above the cap (default 100 + tolerance).

### New: forensic tools
- **Build/destroy heatmap.** Every piece place / destroy is logged to a daily CSV with attribution, including creature destroys (Troll smashes your base = logged as "Troll").
- **Death log.** When a player dies, an entry is posted to your public Discord with the cause (creature name, PvP killer with SteamID, or environmental cause like "drowned"/"fell").
- **Modset fingerprint.** Each server publishes a short hash (e.g. `8ce8906e`) that uniquely identifies its modpack — players can verify they're connecting with the matching pack.

### New: admin console commands
Press **F5** to open the console, then type `sg help`. You'll get a moderation toolkit without leaving the game:
- `sg status`, `sg reload`, `sg modset`, `sg selftest`
- `sg whois <name>`, `sg violations`, `sg pardon`, `sg kick`
- `sg build at <x> <z>` / `by <name>` / `today` — query the heatmap
- `sg destroyed at|by|today` and `sg placed at|by|today` — filter to destroys or placements only

### Discord channel split
- **Public channel** = community-friendly events only: `joined`, `left`, `kicked`, `died`. Safe to share with all players.
- **Admin channel** (new) = curated moderation events: violations, config reloads, admin command audit, daily summary. Set `discordWebhookUrlAdmin` in `settings.yaml`.
- **Admins are hidden from the public channel.** Their join/leave/death events go to the admin channel only.
- **Daily summary** posts a one-paragraph digest each UTC midnight (joins, leaves, kicks, bans, top kick reasons).

### New: ping / latency log
Optional admin-only feature: posts each player's first ping after join and their session-average on disconnect. Helps spot VPN / proxy users. Default off.

### Other improvements
- **Self-test on boot.** Smoke-tests config (HMAC, webhook URLs, file permissions). Alerts the admin channel on any failure.
- **Player death cause messages.** Now show creature names, killer SteamID for PvP, or environmental cause (burned / drowned / fell / etc.).
- **Per-rule "counts as violation"** toggle so you can tune which rules can lead to auto-ban vs which are just informational.
- **Hot-reload** of all config files — edit `settings.yaml` / `admins.yaml` / `allowed_mods.yaml` and the server picks it up within a second.
- **Mod-set fingerprint mismatch detection.** When a player connects with the right mods but different versions, the admin channel notes it.

### Bug fixes
- Admin connection event now shows up properly.
- Hammer-removed pieces now log correctly with attribution.
- Build positions now record the real world coords (not the prefab origin).
- Several Mono compatibility fixes for current Valheim builds.

## 1.3.0

Initial public release. Mod allowlist with HMAC-signed attestation, per-peer auto-ban for repeat violations, hot-reload of configs, Discord webhook integration.
