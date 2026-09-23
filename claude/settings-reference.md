# Settings Reference

All fields are in the `Settings` class in `ServerPlugin.cs`. The YAML file is `BepInEx/config/ServerGuard/conf/settings.yaml`. Hot-reloaded via `FileSystemWatcher`.

The **client** has its own, much smaller config (`ClientSettings` in `ClientPlugin.cs`, file `client.yaml`). It is documented at the bottom of this page — see **Client config (`client.yaml`)**.

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
| `SpeedCheckMaxMetersPerSecond` | `speedCheckMaxMetersPerSecond` | double | `70.0` (2.0; was 15.0) |
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

## Cheat taint detection (2.0)

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableCheatTaintDetection` | `enableCheatTaintDetection` | bool | `true` |
| `CheatTaintPolicy` | `cheatTaintPolicy` | string | `"log"` (`log` / `strip` / `violation`) |
| `CheatTaintBypassPolicy` | `cheatTaintBypassPolicy` | string | `"log"` (`log` / `kick`) |
| `CheatTaintExemptModerators` | `cheatTaintExemptModerators` | bool | `false` |
| `CheatTaintFlagUsedCheats` | `cheatTaintFlagUsedCheats` | bool | `true` |
| `CheatTaintIgnoredItems` | `cheatTaintIgnoredItems` | `List<string>` | `[]` |
| `EnableDebugFlyCheck` | `enableDebugFlyCheck` | bool | `true` |

Policy is normalised by `NormalizedCheatTaintPolicy` (unknown → `log`). The three
rules it feeds (`CheatedItem`, `CheatedBuild`, `DebugFly`) all default to
`countAsViolation: false`. Full mechanism in `claude/features-and-rules.md`, *Cheat
taint family*. `cheatTaintIgnoredItems` exists because Valheim auto-flags any item with
more than 10000 total damage — list modded weapons there instead of disabling the
feature.

`cheatTaintBypassPolicy` (normalised by `NormalizedCheatTaintBypassPolicy`, unknown →
`log`) governs a character carrying the `bypasscheatchecks` unique key — the game then
marks nothing that character does, so all three rules are blind for it. `log` posts once
per session; `kick` also calls `TryKick` on every report that carries the flag (the
disconnect clears the peer's dedup state, so a reconnect with the same character is
refused again). Not a rule: no `AddViolation`, no strike. Owners are exempt via
`CheatTaintExempt` and `TryKick` both; moderators only via `cheatTaintExemptModerators`.
Metric: `cheat_taint_bypass_kicks`.

---

## Staff dev commands (2.0)

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableOwnerDevcommands` | `enableOwnerDevcommands` | bool | `true` |
| `EnableModeratorDevcommands` | `enableModeratorDevcommands` | bool | `true` |
| `ModeratorDevcommands` | `moderatorDevcommands` | `List<string>` | `goto pos removedrops stopevent find` |

`enableOwnerDevcommands`: owners can run **every** dev command on this server, client-side
and server-side, and are treated as vanilla admins by `ZNet` (no `adminlist.txt` entry
needed). `enableModeratorDevcommands` + `moderatorDevcommands`: moderators can run
exactly the listed commands; `devcommands` itself is always allowed to them when the
list is non-empty. Names are lower-cased and a leading `/` is stripped on read
(`ModeratorDevcommandSet`). **`ModeratorReservedCommands`** — `fly debugmode spawn itemset
nocost noplacementcost location` — are dropped from the list on read (with a log line)
and refused by `IsDevcommandAllowed` regardless: moderators can never create items, build
for free or fly. Moderators do **not** need an `adminlist.txt` entry: every command on
their list is either local to their client or authorised by ServerGuard's own prefixes.

Travels to the client as the two trailing `ServerGuard_ConsolePolicy` fields
(`devMode|devCsv`), pushed and re-pushed exactly like the console guard, so a change
to any of the three keys — or to `owners.yaml` / `moderators.yaml` — takes effect for
online staff immediately. Listing `debugmode` for moderators also hands them the
debug hotkeys (Z fly, B free build). Full mechanism in `claude/console-guard.md`,
*Staff dev commands*.

---

## Customs inventory baseline

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableCustoms` | `enableCustoms` | bool | `false` |
| `CustomsMode` | `customsMode` | string | `"dryrun"` |
| `CustomsNewCharacters` | `customsNewCharacters` | string | `"fresh"` |
| `CustomsExemptModerators` | `customsExemptModerators` | bool | `false` |
| `CustomsIgnoredItems` | `customsIgnoredItems` | `List<string>` | `[]` |
| `CustomsArrivalTimeoutSeconds` | `customsArrivalTimeoutSeconds` | int | `60` |
| `CustomsCheckpointSeconds` | `customsCheckpointSeconds` | int | `120` |
| `CustomsDebounceSeconds` | `customsDebounceSeconds` | int | `5` |
| `CustomsMaxItemRecords` | `customsMaxItemRecords` | int | `256` |

Effective mode comes from `CustomsPolicy.Resolve`: disabled when `enableCustoms` is false; `enforce` only when both `customsMode: enforce` and the global `enforce` switch are true; `off`/`disabled` explicitly disable it; every other value fails safe to `dryrun`. In dry run Customs judges, logs and learns from accepted readable snapshots but never disconnects. In enforce, an arrival with a positive inventory delta is refused unless it has a live operator approval.

`customsNewCharacters` applies only when no baseline exists in enforce: `fresh` admits only an empty inventory after ignored prefabs are removed; `any` admits and establishes whatever arrives; `approve` admits only with a live `sg customs approve`. Unknown values fall back to `fresh`. Approvals are one-shot, expire after 24 hours, and are stored in `customs/approvals.json`.

Owners are never inspected. Moderators are inspected unless `customsExemptModerators` is true. Peers without a resolvable 17-digit SteamID are not inspected because there is no stable account key for their baseline. `customsIgnoredItems` compares prefab names case-insensitively and removes those records from both arrival deltas and the `fresh` emptiness check.

The declaration deadline starts when the character enters the world, with a minimum timeout of 5 seconds; after 15 minutes without a character it starts anyway. Request values sent to clients are clamped: checkpoint 30–3600 seconds, debounce 1–120 seconds, records 32–4096. `customsMaxItemRecords` controls both the request and server parser limit.

Settings, moderator and owner hot-reloads call `CustomsRequestReconcile()`. Newly in-scope online peers are enrolled without judging the current inventory, out-of-scope peers receive a stop request, and timing/record-limit changes are re-pushed. Switching dry run/enforce does not re-judge an admitted player; it applies when the next arrival declaration is handled. See `features-and-rules.md` and `wiki/Customs.md` for rollout and persistence behavior.

---

## Metrics

| C# property | YAML key | Type | Default |
|---|---|---|---|
| `EnableMetrics` | `enableMetrics` | bool | `true` |

Feature counters include `ban_layer_blocks` and `console_blocks`. Customs adds `customs_arrivals`, `customs_flagged`, `customs_refused` and `customs_unusable` to `metrics.yaml`. Its only violation rule is `UndeclaredItems`, which defaults to `countAsViolation: false` because an enforce refusal already disconnects the player.

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
- `settings.yaml` → calls `LoadSettings()` → `ReconfigureDiscordAndSummary()`, `BroadcastArrivalShoutPolicy()`, `BroadcastConsolePolicy()`, `SweepBannedPeers()`, `CustomsRequestReconcile()`
- `moderators.yaml` → calls `LoadAdmins()` → `BroadcastConsolePolicy()`, `CustomsRequestReconcile()`
- `owners.yaml` → calls `LoadOwners()` → `BroadcastConsolePolicy()`, `CustomsRequestReconcile()`
- `allowed_mods.yaml` → calls `LoadAllowedMods()` → `RecomputeModsetFingerprint()`
- `bans.yaml` → calls `LoadBans()` → `SweepBannedPeers()`

Debounce: `_lastSeenWrite` dictionary keyed by file path, skips events within 500ms of last write. Prevents double-fire from editors that write twice.

---

## Client config (`client.yaml`)

Class `ClientSettings` in `ClientPlugin.cs`. File:
`BepInEx/config/ServerGuard/client.yaml` on each player's install. Read once in
`EnsureConfig()` during `Awake` — **not** hot-reloaded; the player relaunches Valheim.
Same `CamelCaseNamingConvention` + `IgnoreUnmatchedProperties()` as the server.

| C# property | YAML key | Type | Default | Notes |
|---|---|---|---|---|
| `SharedSecret` | `sharedSecret` | string | `""` | Must match the server's `sharedSecret` verbatim. Empty = manifest sent unsigned (server rejects unless `requireHmac: false`). |
| `QuickLoginEnabled` | `quickLoginEnabled` | bool | `false` | Master switch for the title-screen panel. Also requires a non-empty `serverAddress`. |
| `ServerAddress` | `serverAddress` | string | `""` | Hostname or IP. |
| `ServerPort` | `serverPort` | int | `2456` | Game port. The A2S player-count query goes to **port + 1**. |
| `ServerPassword` | `serverPassword` | string | `""` | Plain text. Applied via the static `FejdStartup.ServerPassword` so the in-game prompt is skipped. |
| `ServerName` | `serverName` | string | `""` | Panel heading. |
| `ServerDescription` | `serverDescription` | string | `""` | Single label under the name, fixed 64 px tall. |
| `ServerLogoPath` | `serverLogoPath` | string | `""` | PNG/JPG **filename** relative to `BepInEx/config/ServerGuard/`. Loaded via `ImageConversion.LoadImage` by reflection. |
| `ServerAnnouncements` | `serverAnnouncements` | string | `""` | *(1.8.0)* Multi-line text for the scrollable **Announcements** box under the description. Empty = header and box omitted entirely. |

### `serverAnnouncements` in detail

Intended to be written as a YAML **literal block scalar** so line breaks survive as typed:

```yaml
serverAnnouncements: |
  <b>Server events</b>
  Bosses every Saturday, 20:00 UTC.

  Join our [Discord](https://discord.gg/example) for the schedule.
```

- `[label](url)` is rewritten to TMP `<link="url">` markup by `FormatAnnouncementsRich`.
  Everything else passes through, so `<b>`, `<i>`, `<color=#…>` work.
- **Only `http://` and `https://` are opened** (`IsOpenableUrl`). A link that fails the
  check is rendered as plain label text — not styled as a link at all — so nothing
  *looks* clickable that won't be. Rationale: `client.yaml` usually ships inside a
  modpack, so the text isn't necessarily written by the person at the keyboard, and a
  click must not be able to launch `file://` or a custom scheme handler.
- Clicks resolve through `AnnouncementLinkClicker` → `TMP_TextUtilities.FindIntersectingLink`
  (by reflection — the project doesn't reference `Unity.TextMeshPro`) → `Application.OpenURL`.

### Template + migration

`EnsureConfig()` only *writes* `client.yaml` when it is missing. Because a new key
would otherwise never appear for upgrading players, there is one migration branch:
if the file exists but contains no `serverAnnouncements` (case-insensitive), the
commented block from `AnnouncementsYamlBlock()` is **appended** once. Existing
values and comments are untouched. The shipped default is `serverAnnouncements: ""`
with the block-scalar example in comments — deliberately *not* a live example, so
enabling Quick Login never surfaces a placeholder Discord link.

Add a key → add it to `ClientSettings`, to the fresh-file template in `EnsureConfig`,
and to the migration check if upgrading players must be able to discover it.
