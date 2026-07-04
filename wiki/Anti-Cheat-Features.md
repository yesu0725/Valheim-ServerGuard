# Anti-Cheat Features

ServerGuard ships with several rules that detect and (optionally) escalate to auto-ban. Each rule fires events to the admin Discord channel and accumulates strikes if its `countAsViolation` flag is `true`.

## How violations escalate

```
event → log line → admin Discord post → strike (if rule counted)
                                          → auto-ban (if strikes ≥ threshold)
```

`violationThreshold` (default 3) sets how many strikes for the same rule before auto-ban fires. Rules with `countAsViolation: false` still log + post events but never lead to ban.

When auto-banned, the player is also kicked. Pardon via `sg pardon <steamid>`.

## Rules

### CompanionMissing

The player connected without the companion plugin (or it failed to respond in time).

- **Triggered by:** No `ServerGuard_Manifest` RPC reply within `companionTimeoutSeconds`.
- **Default:** Not counted (already kicked at the door).
- **Setting:** `requireCompanion: true` (keep ON).

### HmacInvalid / ChallengeMismatch

The companion's reply failed signature verification. Usually means the player's `client.yaml` has a wrong `sharedSecret` (common new-player issue).

- **Default:** Not counted.
- **Public Discord wording:** "wrong password".

### RequiredModMissing

A mod listed in `required_mods` wasn't in the player's manifest.

- **Default:** Not counted.

### DisallowedMod

The player ran a mod not in `required_mods` or `allowed_mods` (and `allowUnlisted: false`).

- **Default:** Not counted.

### BannedMod

The player ran a mod listed in `banned_mods`.

- **Default:** Not counted.

### HashMismatch

A required mod's DLL hash didn't match the pinned `|sha256` suffix. The player has the right *mod* but a different *version*.

- **Default:** Not counted.

### CharacterNameLimitExceeded

A SteamID tried to register more distinct character names than `characterLimit` allows. Stops alt-character abuse on single-character servers.

- **Default:** Counted.
- **Setting:** `characterLimit: 1` (raise for free-character servers).

### DevcommandAttempt

The player typed a cheat-flagged console command (or `devcommands` itself). Blocked client-side by the companion; reported here.

- **Default:** Counted.
- **Setting:** `enableDevcommandGate: true`.
- **Notes:** Blocked even when this toggle is `false` — the toggle only controls server-side accounting.

### SpeedHack

The player moved faster than `speedCheckMaxMetersPerSecond` for `speedCheckConsecutiveStrikes` consecutive samples.

- **Default:** Counted.
- **Settings:** `enableSpeedCheck`, `speedCheckMaxMetersPerSecond` (default 15), `speedCheckSampleSeconds` (default 1), `speedCheckConsecutiveStrikes` (default 3), `speedCheckTeleportToleranceMeters` (default 60).
- **False-positive defenses:** Vertical motion is ignored (jumping/falling doesn't count). Big single-sample jumps (portal/stone) reset the strike counter. Lag spikes need to sustain for N seconds before flagging.
- **Tuning:** Modded mounts and skills may legitimately push speed higher — raise the threshold rather than disabling.

### IllegalItem / StackOverflow

Server-side check on `Inventory.AddItem`:
- `IllegalItem` — item name not in ObjectDB (catches spawned junk or mods the server doesn't run).
- `StackOverflow` — stack exceeds `m_maxStackSize * inventoryCheckStackTolerance`.

- **Default:** Both not counted.
- **Settings:** `enableInventoryCheck`, `inventoryCheckLogOnly` (default `true` — just log, don't reject), `inventoryCheckStackTolerance` (default 1.0).
- **Caveat:** Catches items flowing through server-authoritative paths. Client-side `spawn` cheats are caught by `DevcommandAttempt` instead.

### AnimationCancel

The player tried to cancel an attack-recovery animation via emote or sheathe — the classic Valheim attack-spam exploit.

- **Default:** Not counted.
- **Setting:** `enableAnimationCancelGate: true`.
- **Notes:** The companion blocks the cancel client-side (the emote/sheathe silently fails). This toggle controls server-side accounting.

### SkillOverflow

The companion's periodic skill report contained a level above `skillCapMaxLevel + skillCapTolerance`.

- **Default:** Not counted.
- **Settings:** `enableSkillCap`, `skillCapMaxLevel` (default 100), `skillCapTolerance` (default 5).
- **Tuning:** Some modded skill systems legitimately allow higher caps — raise the max.

## Tuning recommendations

| If you want… | Do this |
|---|---|
| Soft launch / log-only mode | `enforce: false`. All rules log and post events but nobody gets kicked. |
| Strict server, fast escalation | All `countAsViolation: true`, `violationThreshold: 2`. |
| Lenient anti-cheat | Default values. Most rules are informational; only `DevcommandAttempt`, `SpeedHack`, `CharacterNameLimitExceeded` escalate. |
| Hash-pinned modpack | Use `<GUID>|<sha256>` for everything in `allowed_mods.yaml`. Players running modified DLLs get `HashMismatch`. |

## Trust model — why client-side enforcement still works

Several rules are enforced by the companion plugin (DevcommandAttempt, AnimationCancel, SkillOverflow). A player could in theory modify the companion to disable these checks. Two defenses:

1. **`requireCompanion: true`** — no companion at all = kicked.
2. **Hash-pinned `required_mods`** — modifying the companion changes its DLL hash → `HashMismatch` kick.

The `mods_for_allowed_mods.yaml` exported by the companion is hash-pinned by default, so this happens automatically.

## See also

- **[Configuration](Configuration)** — every anti-cheat setting.
- **[Discord Integration](Discord-Integration)** — where violation events appear.
- **[Admin Commands](Admin-Commands)** — `sg whois`, `sg violations`, `sg pardon`.
