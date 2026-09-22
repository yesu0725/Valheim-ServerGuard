# Changelog

## 2.0.1

### New
- **Cheat-check bypass key.** The console command `yesiuseddevcommandsbutiwantmyachievementsanyway` sets a key on the character after which Valheim marks *nothing* that character does as cheated — items spawned in single-player then arrive on your server unmarked, and cheat-taint detection cannot see them. ServerGuard already reported the key once per session; a new setting, `cheatTaintBypassPolicy`, decides what happens next: `log` (the default) keeps the admin-channel post, `kick` also disconnects the player with a message explaining why, until they log in with a character that has never run the command. No strike, nothing is written to their character, owners are exempt. The setting is appended to an existing `settings.yaml` on first boot, with comments; `sg status` shows it on the `CheatTaint` line.

## 2.0.0

**One mod for both sides, and dev commands for your staff.** This is a breaking packaging change — read the upgrade note.

### Changed
- **The server plugin and the client companion are now one mod.** Install `Valheim_ServerGuard` on the dedicated server *and* on every player's game — the same package, the same DLL. It works out which side it is on when it loads: a headless dedicated server runs the server half, a player's game runs the client half.
- **The `Valheim_ServerGuard_Client` package is retired.** Remove it from every profile and modpack before updating; it must not run next to 2.0. Config files are unchanged (`conf/*.yaml` on the server, `client.yaml` on each player).
- **Existing `allowed_mods.yaml` files keep working.** The old client GUID `com.taeguk.valheim.serverguard.client` in `required_mods` is read as the new `com.taeguk.valheim.serverguard`; the server logs a reminder to update the line. If you had the old client DLL hash-pinned, re-pin against the 2.0 DLL — the old hash can never match.
- The kick text for a client without the mod now reads *ServerGuard is not installed on your client* instead of naming the old companion.

### New
- **Dev commands for owners.** Valheim refuses every cheat command on a dedicated-server client, whoever types it. Owners (`conf/owners.yaml`) can now use all of them, exactly as in single-player: type `devcommands`, then `fly`, `god`, `ghost`, `spawn`, `goto`, `heal`, `skiptime`, `tod`, `setworldmodifier`, `randomevent`, and the rest. Owners are also treated as vanilla admins by the server, so nothing needs to be added to `adminlist.txt`. (`enableOwnerDevcommands`, default on.)
- **Dev commands for moderators — the ones you choose.** Moderators (`conf/moderators.yaml`) get exactly the commands in `moderatorDevcommands` (default: `goto`, `pos`, `removedrops`, `stopevent`, `find`). Anything else is refused on their client with a short message, and the attempt is posted to the admin Discord channel — no strike, they're staff. (`enableModeratorDevcommands`, default on.) `fly`, `debugmode`, `spawn`, `itemset`, `nocost`, `noplacementcost` and `location` can never be given to a moderator, whatever the list says.
- **Moderators go through the mod check.** Only owners skip attestation now. A new `moderator_allowed_mods:` section in `allowed_mods.yaml` lists mods only moderators may run (admin tooling), on top of the normal lists.
- **Moderator welcome.** Every time a moderator logs in they get a chat message: a greeting, the dev commands they currently have, where the `sg` tools are, and a reminder to moderate responsibly.
- **Cheat-detection notice.** When cheat-taint detection is on, every player sees a one-time notice panel on their first login after launching the game (not on a relog) explaining what is detected and what the consequence is under your current policy.
- **Map coordinates for staff.** Owners and moderators see the world X/Z under the cursor on the large map, next to the biome name.
- **Speed check default** raised from 15 to 70 m/s. The old default flagged modded mounts and skills; 70 only catches teleport-style movement. Existing servers keep whatever they have set.
- **Cheat-taint detection.** Valheim 1.0 marks everything that came out of a cheat — `spawn`ed items, anything crafted from them, pieces built with `nocost`, creatures hit while in god/fly mode — and keeps the mark in the character file, so it survives a trip through single-player. ServerGuard now uses it: players carrying flagged items are reported to your admin channel (`cheatTaintPolicy: log`, the default), or have them removed (`strip`), or get a strike as well (`violation`). Cheat-flagged builds go into the build log with a new `cheated` column and are double-checked by the server against the piece itself. Debug fly is detected server-side. Owners are exempt; moderators are reported unless you say otherwise. Everything is informational by default — three new rules, `CheatedItem`, `CheatedBuild` and `DebugFly`, all start with `countAsViolation: false`. If your modpack has weapons over 10000 damage, add them to `cheatTaintIgnoredItems` (the game auto-flags those).
- Every dev command that runs on the server (`skiptime`, `sleep`, `randomevent`, world modifiers, ...) is logged with who ran it and posted to the admin channel. Staff see a short `[ServerGuard]` acknowledgement in their console; on connecting they get one line telling them what they have been granted.
- All three settings hot-reload, and so do `owners.yaml` / `moderators.yaml`, so promoting or demoting someone takes effect while they are online.
- `sg status` shows a `DevCmds` line. The new settings are appended to an existing `settings.yaml` on first boot, with comments.
- If you were running *Server Devcommands* only to give staff cheats, you no longer need it.

### Upgrade note
1. Server: replace the DLL (or update the package). Start it once and check the log for `starting as SERVER`.
2. Players: remove `Valheim_ServerGuard_Client`, install `Valheim_ServerGuard`. Their `client.yaml` is untouched.
3. Optionally edit `allowed_mods.yaml` to the new GUID.

## 1.8.1

**Compatibility release for Valheim 1.0.** No code changes — this version exists to state the compatibility and to keep the server and companion version numbers matched.

ServerGuard was verified against **Valheim 1.0.7** (network version 39, Unity 6000.0.75) on BepInEx 5.4.23.5: the server plugin loads cleanly, its self-test passes, and every Valheim method it patches (`ZNet.IsAllowed`, `ZNet.OnNewConnection`, `ZNet.RPC_PeerInfo`, `ZNet.RPC_ServerSyncedPlayerData`, `WearNTear.Damage`/`Destroy`, `Inventory.AddItem`, the raid-event hooks) still exists with the same signature. Mod attestation, the ban layer, the console guard and the privilege tiers all behave as they did on the previous build.

If you are already running 1.8.0, you do not need this update for anything to work — take it only to keep both plugins on the same version number.

Note for modpack authors: Valheim 1.0 changed the `Terminal.ConsoleCommand` constructor, which breaks *other* mods that register console commands (Server Devcommands 1.109 throws `MissingMethodException` on startup). ServerGuard never constructs one and is unaffected.

Version-match release. The server plugin is functionally unchanged from 1.7.0 — all of 1.8.0's work is in the companion plugin, which gains a scrollable **Announcements** box with clickable links on its Quick Login title-screen panel (configured per-client in `client.yaml`, not on the server).

Update both plugins together so the versions stay matched.

## 1.7.0

Feature release. Three new subsystems: owner/moderator privilege tiers, an instant SteamID ban layer, and a console guard. Also fixes several settings that were invisible in `settings.yaml`. Requires companion plugin **v1.7.0** — the console guard is enforced by the companion, so an older client will ignore it.

### New
- **Two staff tiers: owner and moderator.** `conf/owners.yaml` is a new list — normally just you. An owner is exempt from **every** rule in the mod, unconditionally, with no setting to turn that off: never kicked, never banned (an entry in `bans.yaml` matching an owner is ignored), never given a violation strike, never speed-checked or skill-capped, never subject to the character limit, cheat-item removal, forced map positions or the console guard. `admins.yaml` becomes `conf/moderators.yaml` — moderators keep every bypass the old "admin" tier had, but they remain bannable and kickable. Owners don't need to be listed as moderators as well.
- **`admins.yaml` migrates automatically.** On the first boot after updating, your SteamIDs are copied into `moderators.yaml` and the old file is renamed to `admins.yaml.legacy`. Nothing to do, nothing lost. If the old file can't be parsed the migration stops and leaves it alone rather than guessing, and says so in the log.
- **Instant SteamID bans** (`enableBanLayer`, default `true`). Valheim applies its own ban list on a five-second timer, which is why a banned player still loads in and gets a few seconds of play before being removed. ServerGuard keeps a separate list in `conf/bans.yaml` and checks it inside the connection handshake — the connection is refused before a character is ever spawned. `banLayerMirrorToVanilla` (default `true`) also writes each ban into Valheim's `banlist.txt` so it survives ServerGuard being uninstalled. Note the reverse doesn't apply: the in-game `unban` command clears only `banlist.txt`, so it can't quietly lift a ServerGuard ban.
- **Ban admin commands.** `sg ban <steamid> [for <N>d|h|m] [reason]`, `sg unban <steamid>`, `sg bans [n]`. The target doesn't have to be online or ever to have connected — a full 17-digit SteamID can be banned pre-emptively. Banning yourself or an admin is refused. Bans take effect immediately for anyone already in the world.
- **`bans.yaml` hot-reloads.** Hand-edit it and the change lands within a second, including disconnecting anyone online who now matches. If the file fails to parse, the last good list stays in force rather than the server locking everyone out — the error goes to the log and the admin channel.
- **Console guard** (`consoleGuardMode`, default `restricted`). Four modes: `open` (no gating), `restricted` (blocks cheat commands, anything Valheim flags as a cheat including other mods' commands, and a curated list of non-cheat commands that still mutate shared world state), `whitelist` (only `consoleAllowedCommands` permitted), `disabled` (the F5 console cannot be opened at all). `consoleGuardExemptModerators` (default `true`) keeps moderators unrestricted — worth leaving on, since `sg` commands are typed into that console. Owners are always exempt regardless.
- **Key-bind control** (`consoleGuardBindPolicy`, default `purge`). A player could bind a command to a key in single-player and arrive on your server with it loaded. Two details made that worse than it sounds: Valheim runs binds from the chat update loop, so the console never has to be open — or even openable — for one to fire; and bind-dispatched commands skip Valheim's own "not valid in the current context" check. Binds are now cleared while a player is connected and the `bind` command is refused. `wipe` also erases them from the player's disk; `block` and `allow` are available if you want something looser.
- **`consoleBlockedCommands` / `consoleAllowedCommands`** let you extend or replace the built-in lists without a code change.
- **New violation rule `ConsoleCommandBlocked`** (default: does *not* count toward auto-ban) for non-cheat console blocks. These post to the admin channel only — a curious player typing `bind` shouldn't show up in the public channel as a cheater. Genuine cheat attempts still use `DevcommandAttempt` and still post publicly.
- **New metrics counters** `ban_layer_blocks` and `console_blocks`.

### Fixed
- **Options that default to off were missing from `settings.yaml` entirely.** The file was generated by a serializer configured to omit any value still at its default, so every setting that defaults to `false`, `0` or an empty list was never written out — `enableForceMapPositions`, `forceMapPositionsExemptAdmins`, `enablePingLog`, `allowUnlisted`, `logPeerManifest`, `selfTestPostOnPass`, `discordVerboseMirror` and `dailySummaryHourUtc`. The features worked; their switches were simply invisible, which is indistinguishable from the feature not existing if you're reading the file to find them. Fresh installs now list every option, and **existing servers get the missing ones appended on next boot**, under a dated header, with the values already in effect. Your current settings, ordering and comments are untouched — the top-up only adds keys that aren't there.
- **`countAsViolation` was matching nothing on older config files.** The lookup is case-sensitive in practice — the settings loader replaces the dictionary and the case-insensitive comparer is lost — while `settings.yaml` was generated with camelCase rule names (`devcommandAttempt`) and the code looks up PascalCase (`DevcommandAttempt`). Every lookup missed and fell back to "doesn't count", so **no rule counted toward the auto-ban threshold** and nothing in the log said so. Rule names are now matched case-insensitively regardless of how your file spells them.
- **`metrics.yaml` looked empty.** Counters still at zero were omitted by the same serializer setting, so a quiet server wrote a file containing only a timestamp. All counters are now always written.
- **`discordAdminWebhookUrl` was silently ignored.** The correct key is `discordWebhookUrlAdmin`; the loader skips unknown keys without complaining, so a server using the other word order had **no admin channel at all** — no violation alerts, no reload notices, no admin audit trail, no daily summary — with nothing in the log explaining why. The legacy spelling is now accepted (and a warning asks you to rename it). The settings top-up above also writes the correct key with your URL.

### Changed
- **Auto-bans from `violationThreshold` now go through the ban layer**, so a player who trips the threshold is refused instantly on their next connection attempt instead of getting in and being swept out. With `enableBanLayer: false` the old vanilla-only behaviour is preserved.
- `sg status` now reports ban-layer and console-guard state plus owner/moderator counts; `sg whois` reports the player's tier and any active ban.
- `sg reload` also reloads `owners.yaml` and `bans.yaml`.
- Join notifications distinguish owner (👑) from moderator (🛡️).

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
