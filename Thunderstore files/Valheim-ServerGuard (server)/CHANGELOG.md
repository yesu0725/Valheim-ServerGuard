# Changelog

## 1.6.3

Version-match release. No server-side behaviour changes — the fix in 1.6.3 is in the companion plugin.

### Companion
- Requires companion plugin **v1.6.3**, which fixes `enableArrivalShout: false` blocking every shout, not just the first-spawn one. If you turned that setting on in 1.6.2, your players could not use `/s` at all. Update both plugins.

## 1.6.2

Feature release. Two new `settings.yaml` options, one anti-cheat rule relaxed, and the server start/stop Discord notifications are back.

### New
- **Forced map positions** (`enableForceMapPositions`, default `false`). Overrides every player's "public position" minimap toggle so all players are permanently visible on each other's maps. Enforced server-side — the flag is rewritten as each client's position sync arrives, so a modified client can't opt out. `forceMapPositionsExemptAdmins` (default `false`) lets staff keep their own toggle. Both hot-reload; turning the feature off restores each player's own choice within a couple of seconds.
- **Arrival shout toggle** (`enableArrivalShout`, default `true`). Set to `false` and the companion swallows the vanilla "I have arrived!" shout on first spawn — handy when the server already posts login notifications and the shout is just noise. Players can still shout manually. Hot-reloads to everyone already online, so nobody has to reconnect.

### Changed
- **Sheathing is no longer part of the AnimationCancel rule.** Holstering your weapon mid-attack is ordinary play — weapon swaps, picking up items, opening chests and building all do it — so gating it flagged honest players. Only the emote cancel is checked now. The server also discards `sheathe` reports from companions on 1.6.1 and earlier, so the rule stops applying the moment you update the server, without waiting for every player to update their client.

### Fixed
- **Server start and shutdown Discord notifications are back.** They were dropped when the 1.4.0 and 1.5.0 code lines were merged for 1.6.0.
- **The boot notification is now two messages.** `Server is starting...` fires when the plugin loads; `The server has started, you may now login.` only once the world is loaded and location generation has finished. On a brand-new seed those can be minutes apart — the old single message invited players onto a server that would still refuse them. If generation never completes, no public message is sent and a timeout warning goes to the admin channel instead.
- The shutdown notice now posts synchronously, so it actually reaches Discord before the process exits. A graceful stop is still required — a hard kill or host crash gives the plugin no chance to post.

### Companion
- Requires companion plugin **v1.6.2**. `enableArrivalShout` needs it; the rest is server-side.

## 1.6.1

Version-match release. No server-side behaviour changes — all fixes in 1.6.1 are in the companion plugin.

### Companion
- Requires companion plugin **v1.6.1**, which fixes the Quick Login panel's live player count always showing `?`. See the client changelog.

## 1.6.0

Social/QoL feature drop on top of the 1.4.0 anti-cheat + admin toolkit (everything from 1.4.0 is still here).

### New
- **Raid event alerts.** When a random event / raid begins, pauses (no players in the area), resumes, or ends, ServerGuard posts to the public Discord channel using the **actual in-game event name** (e.g. "The Horde Is Attacking", "You Are Being Hunted") instead of the internal code name, with world coordinates.
- **Player shout logging.** Player shouts (`/s`) are forwarded by the companion and posted to the public Discord channel (chat can no longer be observed server-side, so the companion reports them).
- **Cheat-item removal.** On login, non-admin players have configured cheat items stripped from their inventory (`SwordCheat`, `SledgeCheat` by default). Configure via `enableCheatItemRemoval` and `cheatItems` in `settings.yaml`; admins are exempt.

### Companion
- Requires companion plugin **v1.6.0** (adds the optional title-screen Quick Login panel — see the client changelog).

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
