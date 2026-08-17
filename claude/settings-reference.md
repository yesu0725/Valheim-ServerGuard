# Settings Reference

All fields are in the `Settings` class in `Plugin.cs`. The YAML file is `BepInEx/config/ServerGuard/conf/settings.yaml`. Hot-reloaded via `FileSystemWatcher`.

YAML key naming: `CamelCaseNamingConvention` is applied by YamlDotNet. Field `SharedSecret` → YAML key `sharedSecret`. Fields with `[YamlMember(Alias="...", ApplyNamingConventions=false)]` use the exact alias.

---

## Core enforcement

| C# property | YAML key | Type | Default | Notes |
|---|---|---|---|---|
| `Enforce` | `enforce` | bool | `true` | If false, no one gets kicked — log-only mode |
| `ViolationThreshold` | `violationThreshold` | int | `3` | Strikes before auto-ban |
| `KickMessage` | `kickMessage` | string | (long message) | Shown to kicked player |
| `BanReason` | `banReason` | string | (message) | Shown in ban list |
| `CharacterLimit` | `characterLimit` | int | `1` | Max distinct char names per SteamID |

---

## Client-attestation handshake

| C# property | YAML key | Type | Default | Notes |
|---|---|---|---|---|
| `RequireCompanion` | `requireCompanion` | bool | `true` | Kick vanilla clients |
| `CompanionTimeoutSeconds` | `companionTimeoutSeconds` | int | `10` | Wait for manifest |
| `RequireHmac` | `requireHmac` | bool | `true` | Require signed manifest |
| `SharedSecret` | `sharedSecret` | string | `""` | Auto-generated if empty + requireHmac=true |
| `AllowUnlisted` | `allowUnlisted` | bool | `false` | Allow mods not in allowed list |
| `MaxClockSkewSeconds` | `maxClockSkewSeconds` | int | `120` | Replay window |
| `LogPeerManifest` | `logPeerManifest` | bool | `false` | Log every connecting peer's full manifest |

---

## Discord

| C# property | YAML key | Type | Default | Notes |
|---|---|---|---|---|
| `discordWebhookUrl` | `discordWebhookUrl` | string | `""` | Public channel |
| `discordWebhookUrlAdmin` | `discordWebhookUrlAdmin` | string | `""` | Admin channel |
| `DiscordVerboseMirror` | `discordVerboseMirror` | bool | `false` | Mirror all log lines to admin channel |
| `discordChannelLink` | `discordChannelLink` | string | `""` | Unused display field |

---

## Daily summary

| C# property | YAML key | Type | Default | Notes |
|---|---|---|---|---|
| `DailySummaryEnabled` | `dailySummaryEnabled` | bool | `true` | |
| `DailySummaryHourUtc` | `dailySummaryHourUtc` | int | `0` | 0–23 UTC hour |
| `DailySummaryChannel` | `dailySummaryChannel` | string | `"admin"` | `"public"` / `"admin"` / `"both"` |

---

## Per-rule violation accounting

| C# property | YAML key | Type | Notes |
|---|---|---|---|
| `CountAsViolation` | `countAsViolation` | `Dictionary<string,bool>` | See `claude/features-and-rules.md` for defaults. Missing keys default to `false`. |

Attribute: `[YamlMember(Alias = "countAsViolation", ApplyNamingConventions = false)]`

---

## Anti-cheat feature toggles

### Devcommands gate
| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableDevcommandGate` | `enableDevcommandGate` | bool | `true` |

### Speed check
| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableSpeedCheck` | `enableSpeedCheck` | bool | `true` |
| `SpeedCheckMaxMetersPerSecond` | `speedCheckMaxMetersPerSecond` | double | `15.0` |
| `SpeedCheckSampleSeconds` | `speedCheckSampleSeconds` | double | `1.0` |
| `SpeedCheckConsecutiveStrikes` | `speedCheckConsecutiveStrikes` | int | `3` |
| `SpeedCheckTeleportToleranceMeters` | `speedCheckTeleportToleranceMeters` | double | `60.0` |

### Inventory check
| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableInventoryCheck` | `enableInventoryCheck` | bool | `true` |
| `InventoryCheckLogOnly` | `inventoryCheckLogOnly` | bool | `true` |
| `InventoryCheckStackTolerance` | `inventoryCheckStackTolerance` | double | `1.0` |

### Animation-cancel gate
| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableAnimationCancelGate` | `enableAnimationCancelGate` | bool | `true` |

### Skill cap
| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableSkillCap` | `enableSkillCap` | bool | `true` |
| `SkillCapMaxLevel` | `skillCapMaxLevel` | double | `100.0` |
| `SkillCapTolerance` | `skillCapTolerance` | double | `5.0` |

---

## Forensic logging

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableDeathLog` | `enableDeathLog` | bool | `true` |
| `EnableBuildLog` | `enableBuildLog` | bool | `true` |
| `BuildLogRetentionDays` | `buildLogRetentionDays` | int | `30` |

---

## Self-test

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableSelfTest` | `enableSelfTest` | bool | `true` |
| `SelfTestPostOnPass` | `selfTestPostOnPass` | bool | `false` |

---

## Ping log

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnablePingLog` | `enablePingLog` | bool | `false` |
| `PingLogSampleSeconds` | `pingLogSampleSeconds` | int | `5` |

---

## Arrival shout

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableArrivalShout` | `enableArrivalShout` | bool | `true` |

Server setting, client enforcement. `SendArrivalShoutPolicy(peer)` pushes `"1"`/`"0"` over the `ServerGuard_ArrivalShout` RPC on connect (before the admin early-return, so admins get it too); `BroadcastArrivalShoutPolicy()` re-pushes to everyone online from `LoadSettings()` on hot-reload. The companion swallows the shout in its `Chat.SendText` prefix while `Game.UpdateRespawn` is on the stack.

---

## Forced map positions

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableForceMapPositions` | `enableForceMapPositions` | bool | `false` |
| `ForceMapPositionsExemptAdmins` | `forceMapPositionsExemptAdmins` | bool | `false` |

Implemented by `ApplyForcedMapPosition(ZNetPeer)` + `Patch_ForceMapPositions` (postfix on the private `ZNet.RPC_ServerSyncedPlayerData`). Sets `peer.m_publicRefPos = true`, which `ZNet.UpdatePlayerList` copies into `PlayerInfo.m_publicPosition` for the broadcast player list. Re-applied on every client sync (~2s), so it hot-reloads in both directions without a restart.

---

## Ban layer

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableBanLayer` | `enableBanLayer` | bool | `true` |
| `BanLayerKickMessage` | `banLayerKickMessage` | string | `"You are banned from this server."` |
| `BanLayerMirrorToVanilla` | `banLayerMirrorToVanilla` | bool | `true` |

The list itself lives in `conf/bans.yaml`, not in settings.yaml. Full detail in
`claude/ban-layer.md`.

---

## Console guard

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `ConsoleGuardMode` | `consoleGuardMode` | string | `"restricted"` |
| `ConsoleGuardExemptModerators` | `consoleGuardExemptModerators` | bool | `true` |
| `ConsoleGuardBindPolicy` | `consoleGuardBindPolicy` | string | `"purge"` |
| `ConsoleBlockedCommands` | `consoleBlockedCommands` | `List<string>` | `[]` |
| `ConsoleAllowedCommands` | `consoleAllowedCommands` | `List<string>` | `[]` |
| `ConsoleGuardReportAttempts` | `consoleGuardReportAttempts` | bool | `true` |

`consoleGuardMode`: `open` / `restricted` / `whitelist` / `disabled`.
`consoleGuardBindPolicy`: `allow` / `block` / `purge` / `wipe`.

Both are normalised on read (`NormalizedConsoleMode` / `NormalizedBindPolicy`) —
an unrecognised value silently falls back to the default rather than throwing.

Server setting, client enforcement, same pattern as `enableArrivalShout`:
`SendConsolePolicy(peer)` pushes the policy on connect (before the admin
early-return), `BroadcastConsolePolicy()` re-pushes on every settings.yaml,
**moderators.yaml and owners.yaml** hot-reload. The staff-file hooks matter because the
payload carries the recipient's resolved exemption — without them a promoted or
demoted player keeps the console rights they had at connect time.

Owners (`owners.yaml`) are exempt unconditionally; `consoleGuardExemptModerators`
only governs the moderator tier. See `claude/privilege-tiers.md`.

Full detail — including the per-command risk assessment — in `claude/console-guard.md`.

---

## Metrics

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableMetrics` | `enableMetrics` | bool | `true` |

New counters in `metrics.yaml`: `ban_layer_blocks`, `console_blocks`.

---

## Deprecated fields (kept for backward YAML parsing, no runtime effect)

| C# property | Notes |
|---|---|
| `DiscordPublicMode` | Replaced by two-channel system in v1.4.0 |
| `AggressiveNoModCheck` | Pre-v1.3 setting, ignored |
| `EnableAssemblyScanning` | Pre-v1.3 setting, ignored |
| `UseWhitelistMode` | Pre-v1.3 setting, ignored |
| `RequireAttestation` | Pre-v1.3 setting, ignored |

---

## YamlDotNet configuration

```csharp
_yamlIn = new DeserializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)
    .IgnoreUnmatchedProperties()   // old YAML with unknown keys doesn't crash
    .Build();

_yamlOut = new SerializerBuilder()
    .WithNamingConvention(CamelCaseNamingConvention.Instance)
    .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
    .Build();
```

`IgnoreUnmatchedProperties` is critical — it prevents YAML with old or unknown keys from throwing on load. Any new Settings field added automatically gets its default value if absent from the file.

For snake_case keys that would be mangled by CamelCase convention (e.g. `required_mods`), use:
```csharp
[YamlMember(Alias = "required_mods", ApplyNamingConventions = false)]
public List<string> required_mods { get; set; } = new();
```

---

## Hot-reload

`FileSystemWatcher` watches:
- `settings.yaml` → calls `LoadSettings()` → `ReconfigureDiscordAndSummary()`, `BroadcastArrivalShoutPolicy()`, `BroadcastConsolePolicy()`, `SweepBannedPeers()`
- `moderators.yaml` → calls `LoadAdmins()` → `BroadcastConsolePolicy()`
- `owners.yaml` → calls `LoadOwners()` → `BroadcastConsolePolicy()`
- `allowed_mods.yaml` → calls `LoadAllowedMods()` → `RecomputeModsetFingerprint()`
- `bans.yaml` → calls `LoadBans()` → `SweepBannedPeers()`

Debounce: `_lastSeenWrite` dictionary keyed by file path, skips events within 500ms of last write. Prevents double-fire from editors that write twice.
