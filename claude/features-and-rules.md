# Features and Rules

## Rule constants

Defined at the top of `Plugin.cs`:

```csharp
RULE_COMPANION_MISSING       = "CompanionMissing"
RULE_HMAC_INVALID            = "HmacInvalid"
RULE_CHALLENGE_MISMATCH      = "ChallengeMismatch"
RULE_REQUIRED_MOD_MISSING    = "RequiredModMissing"
RULE_DISALLOWED_MOD          = "DisallowedMod"
RULE_BANNED_MOD              = "BannedMod"
RULE_HASH_MISMATCH           = "HashMismatch"
RULE_CHAR_NAME_LIMIT         = "CharacterNameLimitExceeded"
RULE_DEVCOMMAND_ATTEMPT      = "DevcommandAttempt"
RULE_CONSOLE_COMMAND         = "ConsoleCommandBlocked"
RULE_SPEED_HACK              = "SpeedHack"
RULE_ILLEGAL_ITEM            = "IllegalItem"
RULE_STACK_OVERFLOW          = "StackOverflow"
RULE_ANIMATION_CANCEL        = "AnimationCancel"
RULE_SKILL_OVERFLOW          = "SkillOverflow"
```

`ALL_RULES` array mirrors these for default-seeding the `countAsViolation` map.

---

## countAsViolation defaults

```yaml
countAsViolation:
  CompanionMissing:           false   # already kicked at door — no double-strike
  HmacInvalid:                false   # wrong password, already kicked
  ChallengeMismatch:          false   # wrong password, already kicked
  RequiredModMissing:         false   # already kicked
  DisallowedMod:              false   # already kicked
  BannedMod:                  false   # already kicked
  HashMismatch:               false   # already kicked
  CharacterNameLimitExceeded: true    # in-game behavioural — escalate
  DevcommandAttempt:          true    # in-game behavioural — escalate
  ConsoleCommandBlocked:      false   # non-cheat console blocks — informational
  SpeedHack:                  true    # in-game behavioural — escalate
  IllegalItem:                false   # audit first, opt-in when confident
  StackOverflow:              false   # audit first, opt-in when confident
  AnimationCancel:            false   # audit first, opt-in when confident
  SkillOverflow:              false   # audit first, opt-in when confident
```

`RuleCountsAsViolation(rule)` returns **false** for missing keys — every new rule is opt-in.

---

## Feature reference

### CompanionMissing / attestation gate
- **Setting:** `requireCompanion` (default `true`)
- **Trigger:** No `ServerGuard_Manifest` RPC within `companionTimeoutSeconds` (default 10)
- **Kicks:** Yes (when `enforce: true`)
- **Client involvement:** Client must receive `ServerGuard_RequestManifest` and reply
- **FriendlyReason:** `"missing the required companion mod"`

### HmacInvalid / ChallengeMismatch
- **Settings:** `requireHmac`, `sharedSecret`, `maxClockSkewSeconds`
- **Trigger:** HMAC verification fails OR challenge in reply doesn't match issued one
- **FriendlyReason:** `"wrong password"` (intentionally vague for end users)

### RequiredModMissing
- **Setting:** `required_mods:` in `allowed_mods.yaml`
- **Trigger:** A mod in required list wasn't in client manifest
- **FriendlyReason:** `"missing a required mod (GUID)"`

### DisallowedMod
- **Setting:** `allowUnlisted: false` (default) + `allowed_mods:` list
- **Trigger:** Client manifest contains a mod not in required or allowed lists
- **FriendlyReason:** `"had a mod that isn't allowed (GUID)"`

### BannedMod
- **Setting:** `banned_mods:` in `allowed_mods.yaml`
- **Trigger:** Client manifest contains any mod in the banned list
- **FriendlyReason:** `"had a banned mod (GUID)"`

### HashMismatch
- **Setting:** `|sha256` suffix on entries in `allowed_mods.yaml`
- **Trigger:** Mod present but DLL hash doesn't match pinned sha256
- **FriendlyReason:** `"mod file doesn't match the server's copy (GUID)"`

### CharacterNameLimitExceeded
- **Setting:** `characterLimit` (default 1)
- **Trigger:** `Patch_RPC_PeerInfo` — SteamID already has `characterLimit` registered names, and a new one arrives
- **FriendlyReason:** `"tried to use too many characters"`
- **countAsViolation default:** `true`

### DevcommandAttempt
- **Setting:** `enableDevcommandGate` (default `true`)
- **Trigger:** Client's `Patch_TryRunCommand` blocks a command classified `cheat` — the `CheatCommands` set, or anything registered in `Terminal.commands` with `IsCheat = true` (so other mods' cheat commands are caught dynamically)
- **Client side:** ALWAYS blocked client-side regardless of this setting
- **Server side:** This setting only controls whether it's logged/posted/counted
- **FriendlyReason:** `"tried to use cheats ('god')"`
- **countAsViolation default:** `true`
- **Command list:** see `claude/console-guard.md` — the tiers moved out of this file in 1.7.0 because they now run to ~90 entries with a per-command rationale

### ConsoleCommandBlocked
- **Setting:** `consoleGuardMode` / `consoleGuardBindPolicy` / `consoleBlockedCommands` (+ `enableDevcommandGate` and `consoleGuardReportAttempts` gate the reporting)
- **Trigger:** Client's `Patch_TryRunCommand` blocks a command classified `risky`, `bind`, or `notallowed` — i.e. a non-cheat command that still mutates shared server state, leaks information, or is a key bind
- **Discord:** admin channel only (never public — a curious player typing `bind` isn't a cheater)
- **FriendlyReason:** `"used a restricted console command ('bind (bind)')"`
- **countAsViolation default:** `false`
- **See:** `claude/console-guard.md` for the full per-command risk assessment

### SpeedHack
- **Settings:** `enableSpeedCheck`, `speedCheckMaxMetersPerSecond` (15.0), `speedCheckSampleSeconds` (1.0), `speedCheckConsecutiveStrikes` (3), `speedCheckTeleportToleranceMeters` (60.0)
- **Trigger:** N consecutive poll samples above threshold (horizontal XZ only — vertical ignored)
- **Teleport safety:** Single jump > 60m resets strike counter instead of incrementing
- **countAsViolation default:** `true`

### IllegalItem
- **Setting:** `enableInventoryCheck`, `inventoryCheckLogOnly` (default `true`)
- **Trigger:** `Patch_Inventory_AddItem` — item name not in ObjectDB
- **Note:** No per-peer attribution (Inventory isn't tied to a peer at this seam)
- **countAsViolation default:** `false`

### StackOverflow
- **Setting:** `enableInventoryCheck`, `inventoryCheckStackTolerance` (default 1.0)
- **Trigger:** `Patch_Inventory_AddItem` — stack count > `maxStackSize * tolerance`
- **countAsViolation default:** `false`

### AnimationCancel
- **Setting:** `enableAnimationCancelGate` (default `true`)
- **Trigger:** Client blocks emote during `p.InAttack()`, reports via `ServerGuard_AnimationCancelAttempt`
- **Client side:** ALWAYS blocked client-side
- **Server side:** This setting controls accounting only
- **countAsViolation default:** `false`
- **Excluded sources:** `sheathe` — dropped from the rule (weapon swaps, looting and building all holster the weapon, so it flagged honest players). The client patch is gone; the server also drops any `sheathe` report via `_animationCancelIgnoredSources`, so companions from 1.6.1 and earlier can't resurrect it.

### SkillOverflow
- **Settings:** `enableSkillCap`, `skillCapMaxLevel` (100.0), `skillCapTolerance` (5.0)
- **Trigger:** Skill report from client contains any skill > `maxLevel + tolerance` (105.0 by default)
- **Report interval:** Every 60s after 15s initial delay
- **countAsViolation default:** `false`

---

## Non-rule features (informational only, no violation rule)

### Death log
- **Setting:** `enableDeathLog` (default `true`)
- **Trigger:** `ServerGuard_PlayerDeath` RPC from client on `Player.OnDeath`
- **Output:** Public Discord channel (or admin if victim is admin)
- **No countAsViolation** — pure forensic log

### Build/Destroy log
- **Settings:** `enableBuildLog` (default `true`), `buildLogRetentionDays` (default 30)
- **Trigger:** `ServerGuard_BuildPlace` and `ServerGuard_BuildDestroy` RPCs + server-side `WearNTear` patches
- **Output:** Daily CSV at `build_log/YYYY-MM-DD.csv`
- **No Discord** — pure forensic log

### Ping log
- **Settings:** `enablePingLog` (default `false`), `pingLogSampleSeconds` (default 5)
- **Trigger:** Server reads `ZRpc.m_ping` via reflection every N seconds
- **Output:** Admin Discord — first ping shortly after join, session avg on disconnect

### Self-test
- **Settings:** `enableSelfTest` (default `true`), `selfTestPostOnPass` (default `false`)
- **Trigger:** On boot + `sg selftest` command
- **8 checks:** HMAC secret configured, HMAC roundtrip, policy validator, build-log dir writable, modset fingerprint computed, public webhook URL sane, admin webhook URL sane, admins configured
- **Output:** BepInEx log always; admin Discord on failure (or on pass if `selfTestPostOnPass: true`)

### Arrival shout suppression
- **Setting:** `enableArrivalShout` (default `true` = vanilla)
- **Trigger:** Client `Chat.SendText` prefix returns `false` for a Shout raised while `Game.UpdateRespawn` is on the stack
- **Why bracket UpdateRespawn:** it's the only vanilla caller that shouts on its own, so no text matching is needed — works in every language and never eats a manual "I have arrived!"
- **Policy delivery:** `ServerGuard_ArrivalShout` RPC on connect + on settings hot-reload
- **Requires the companion** — a vanilla client still shouts

### Server lifecycle notifications
- **Settings:** none — they follow `discordWebhookUrl`, like raid alerts
- **Public posts:** "Server is starting..." (`Awake`), "The server has started, you may now login." (`ServerReadyWatcher`), "Server is shutting down." (`OnDestroy`)
- **Readiness test:** `IsServerReadyForPlayers()` = `ZNet.IsServer()` + `ZoneSystem.LocationsGenerated`, polled every 1s for up to 15 min
- See `claude/discord-routing.md` for why the shutdown post is synchronous

### Forced map positions
- **Settings:** `enableForceMapPositions` (default `false`), `forceMapPositionsExemptAdmins` (default `false`)
- **Trigger:** `Patch_ForceMapPositions` postfix on the private `ZNet.RPC_ServerSyncedPlayerData`, i.e. every client position sync (~2s per peer)
- **Effect:** `ApplyForcedMapPosition()` sets `peer.m_publicRefPos = true`, so `ZNet.UpdatePlayerList` marks that player's `PlayerInfo.m_publicPosition` and broadcasts the position to every client's map
- **Server-authoritative:** the client's own minimap toggle (and any client lying about it) is overwritten on the next sync
- **No Discord, no violation rule** — a server policy, not a detection

### Daily summary
- **Settings:** `dailySummaryEnabled` (default `true`), `dailySummaryHourUtc` (default 0), `dailySummaryChannel` (`"admin"`)
- **Trigger:** UTC hour timer, auto-started from `ReconfigureDiscordAndSummary()` on first valid webhook
- **Counts tracked:** joins, leaves, kicks (with top-5 reasons), auto-bans

---

## Violation escalation flow

```
Event fires
  → AddViolation(platformId, rule, detail)
    → RuleCountsAsViolation(rule)?
      YES: increment _violations[steamId][rule]
           PostAdminEvent(":warning: violated rule X (N/threshold)")
           if count >= violationThreshold:
             TryBan(platformId)
             PostPlayerEvent(":no_entry:", "was auto-banned")
             PostAdminEvent(":no_entry: Auto-banned ...")
      NO:  PostAdminEvent(":eye: triggered rule X (informational — not counted)")
```

Pardon: `sg pardon <steamid>` — removes all violations for that SteamID from `_violations` and `violations.yaml`.
