# Configuration

All knobs live in `BepInEx/config/ServerGuard/conf/settings.yaml` on the dedicated server. Changes are **hot-reloaded** — save the file and they take effect within ~1 second. No restart needed.

## Core enforcement

| Setting | Default | What it does |
|---|---|---|
| `enforce` | `true` | Master kill-switch. When `false`, ServerGuard logs and posts events but doesn't actually kick anyone. Useful for break-in periods. |
| `violationThreshold` | `3` | Strikes before auto-ban (per-rule). Only rules with `countAsViolation: true` accumulate. |
| `kickMessage` | "You cannot join..." | Shown to kicked players. Append to it if you want to point users at a Discord. |
| `banReason` | "Auto-banned..." | Logged when a player crosses the threshold. |
| `characterLimit` | `1` | Distinct character names a single SteamID may register on this server. |

## Mod attestation

| Setting | Default | What it does |
|---|---|---|
| `requireCompanion` | `true` | Peers without the companion plugin are kicked. Keep ON. |
| `companionTimeoutSeconds` | `10` | How long to wait for the companion's reply before declaring "no companion". |
| `requireHmac` | `true` | The companion's reply must carry a valid HMAC signature. Keep ON. |
| `sharedSecret` | random | The password the companion uses to sign its manifest. Auto-generated on first boot. Every client needs this value in their `client.yaml`. |
| `allowUnlisted` | `false` | When `true`, mods not in `allowed_mods.yaml` are tolerated. Default keeps the allowlist strict. |
| `maxClockSkewSeconds` | `120` | Rejects manifests timestamped more than this far off from server time. |
| `logPeerManifest` | `false` | Verbose: log every connecting peer's full manifest. Useful for harvesting GUIDs during initial setup. |

## Per-rule violation accounting

```yaml
countAsViolation:
  CompanionMissing: false
  HmacInvalid: false
  ChallengeMismatch: false
  RequiredModMissing: false
  DisallowedMod: false
  BannedMod: false
  HashMismatch: false
  CharacterNameLimitExceeded: true
  DevcommandAttempt: true
  SpeedHack: true
  IllegalItem: false
  StackOverflow: false
  AnimationCancel: false
  SkillOverflow: false
```

A rule with `true` increments the player's strike count when triggered. Repeat strikes lead to auto-ban (per `violationThreshold`). A rule with `false` still fires the event (logged + posted) but doesn't add a strike.

**Why connection-attestation rules default to `false`:** connection failures already kick the player at the door. Counting them as strikes would double-punish.

**Why in-game rules default to `true`:** if a player repeatedly tries cheats or shows speed-hacking, you want escalation.

A missing rule defaults to `false` — every new rule is opt-in. Add the line explicitly if you want it to count.

## Discord

| Setting | Default | What it does |
|---|---|---|
| `discordWebhookUrl` | "" | **Public channel.** Player-facing events (`joined`, `left`, `kicked`, `died`). Safe to share with the whole community. |
| `discordWebhookUrlAdmin` | "" | **Admin channel.** Moderation events (violations, config reloads, command audit, daily summary). |
| `discordVerboseMirror` | `false` | If `true`, also forwards every ServerGuard log line to the admin channel. Noisy — debug use only. |
| `dailySummaryEnabled` | `true` | Posts a one-paragraph daily digest. |
| `dailySummaryHourUtc` | `0` | UTC hour the digest fires. |
| `dailySummaryChannel` | `"admin"` | `public`, `admin`, or `both`. |

Admin lifecycle events (`joined`, `left`, `died`) are **hidden** from the public channel and routed to the admin channel automatically. Players don't see admins coming and going.

See **[Discord Integration](Discord-Integration)** for details.

## Anti-cheat features

| Setting | Default | What it does |
|---|---|---|
| `enableDevcommandGate` | `true` | Companion blocks console cheats; server logs the attempts. |
| `enableSpeedCheck` | `true` | Server polls each peer's position and flags impossibly fast movement. |
| `speedCheckMaxMetersPerSecond` | `15.0` | Threshold. Vanilla sprint ~5, longship ~9, raise for modded mounts. |
| `speedCheckSampleSeconds` | `1.0` | Poll interval. |
| `speedCheckConsecutiveStrikes` | `3` | Over-threshold samples in a row needed to flag. |
| `speedCheckTeleportToleranceMeters` | `60.0` | Single big jumps (portal travel) reset the strike counter instead of incrementing. |
| `enableInventoryCheck` | `true` | Server-side check that items added to any inventory are known + within stack limits. |
| `inventoryCheckLogOnly` | `true` | Log violations without blocking the add. Flip to `false` to actively reject. |
| `inventoryCheckStackTolerance` | `1.0` | Multiplier on each item's `m_maxStackSize`. Raise for modpacks that bump stacks. |
| `enableAnimationCancelGate` | `true` | Companion blocks emote/sheathe-based attack-cancel exploit. |
| `enableSkillCap` | `true` | Companion reports skill levels; server flags any above the cap. |
| `skillCapMaxLevel` | `100.0` | Vanilla cap. Raise for modded skill systems. |
| `skillCapTolerance` | `5.0` | Added to max — absorbs float-rounding overshoot. |

See **[Anti-Cheat Features](Anti-Cheat-Features)** for how each one works.

## Forensic logs

| Setting | Default | What it does |
|---|---|---|
| `enableDeathLog` | `true` | Public-channel death messages (cause + location + killer). |
| `enableBuildLog` | `true` | Daily CSV at `BepInEx/config/ServerGuard/build_log/YYYY-MM-DD.csv`. |
| `buildLogRetentionDays` | `30` | Older CSVs are auto-pruned. |

See **[Forensic Logs](Forensic-Logs)**.

## Optional features

| Setting | Default | What it does |
|---|---|---|
| `enableSelfTest` | `true` | Runs a suite of smoke tests at boot. Posts to admin Discord on failure. |
| `selfTestPostOnPass` | `false` | Also post a green-check confirmation when all tests pass. |
| `enablePingLog` | `false` | Tracks each peer's RTT. Posts first ping + session avg to admin channel. Useful for spotting VPN / proxy users. |
| `pingLogSampleSeconds` | `5` | Sample interval. |
| `enableMetrics` | `true` | Internal counters at `BepInEx/config/ServerGuard/metrics.yaml`. |

## Files written by the plugin

| Path | Purpose |
|---|---|
| `conf/settings.yaml` | This file. |
| `conf/admins.yaml` | List of admin SteamIDs (one per line). |
| `conf/allowed_mods.yaml` | Mod allowlist. |
| `modset_fingerprint.txt` | Current modset hash (publish this to your community). |
| `registrations.yaml` | SteamID → character names mapping. |
| `violations.yaml` | Current per-player violation strikes. |
| `metrics.yaml` | Aggregate counters. |
| `build_log/YYYY-MM-DD.csv` | Daily build/destroy events. |
