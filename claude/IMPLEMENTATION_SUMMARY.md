# Implementation Summary — Valheim ServerGuard

This is a technical summary of how ServerGuard works for someone who wants to read or modify the code. For installation/usage docs see [README.md](README.md) and [DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md).

---

## What problem this solves

A vanilla Valheim dedicated server has no way to know which BepInEx plugins a connecting client has loaded. The client never tells the server, and server-side heuristics (sniffing custom RPC names, scanning the server's own AppDomain) are unreliable or outright broken — see "Why heuristic detection was abandoned" below.

ServerGuard v1.3 solves it by making the client tell the server, in a way the server can verify:

- A small **companion plugin** runs on every client.
- On connect, the server challenges the client to send a **manifest** of its loaded plugins.
- The manifest is **signed with HMAC-SHA256** using a secret shared between server and clients.
- The server checks the signature, the freshness, and whether the listed mods are on its allowlist.

Vanilla clients don't have the companion plugin, so they never reply — and get kicked on timeout.

---

## High-level architecture

```
                ┌───────────────────────────────┐
                │ Shared/Manifest.cs            │
                │   ModManifest DTO             │
                │   Canonical-string format     │
                │   HMAC-SHA256 helpers         │
                └────────────┬──────────────────┘
                             │ (linked into both projects)
              ┌──────────────┴─────────────────┐
              │                                │
              ▼                                ▼
┌──────────────────────────┐      ┌────────────────────────────┐
│ Plugin.cs                │      │ ServerGuard.Client/        │
│ (server)                 │      │   ClientPlugin.cs          │
│                          │      │ (client)                   │
│ • Awake                  │      │                            │
│   - boot notification    │      │ • Awake                    │
│ • OnDestroy              │      │ • EnsureConfig             │
│   - shutdown notif.      │      │ • DeferredInit             │
│ • Load*Yaml              │      │ • BuildManifestCache       │
│ • Patch_OnNewConnection  │      │ • ExportAllowedModsSnippet │
│   - register receiver    │      │ • Patch_RegisterClientHandler
│   - send challenge       │      │   - register reply handler │
│   - schedule timeout     │      │ • BuildManifestJson        │
│ • OnManifestReceived     │      │   (rebuilt on every request)
│   - validate HMAC        │      └────────────────────────────┘
│   - check policy         │
│ • Patch_RPC_PeerInfo     │
│   (login notifications,  │
│    character limit)      │
│ • Patch_Disconnect       │
│   (logout notifications) │
│ • OnChatReceived         │
│   - shouts → public      │
│ • OnPlayerDeathReceived  │
│   - deaths → public      │
│   - admin deaths skipped │
│ • Patch_SetRandomEvent   │
│ • Patch_ResetRandomEvent │
│   (raid event logging)   │
│ • SendPublic / SendAdmin │
│ • TryKick / TryBan       │
└──────────────────────────┘
```

Both projects build to a single DLL each (no other side files needed):

| Project | Output |
|---|---|
| [Valheim-ServerGuard.csproj](Valheim-ServerGuard.csproj) | `Valheim-ServerGuard.dll` |
| [ServerGuard.Client/Valheim-ServerGuard-Client.csproj](ServerGuard.Client/Valheim-ServerGuard-Client.csproj) | `Valheim-ServerGuard-Client.dll` |

The csproj for the server has `<Compile Remove="ServerGuard.Client/**/*.cs" />` so the client subdirectory doesn't accidentally compile into the server build.

---

## The handshake, in detail

```
Client                                                  Server
  │                                                       │
  │ ── Steam connection accepted ─────────────────────────►
  │                                                       │
  │   ZNet.OnNewConnection fires on BOTH sides            │
  │                                                       │
  │   Client Postfix:                Server Postfix:      │
  │   peer.m_rpc.Register<string>(   peer.m_rpc.Register<string>(
  │     "ServerGuard_RequestMani-      "ServerGuard_Manifest",
  │      fest", handler)               handler)
  │                                  generate 24 random bytes
  │                                  store {peer→challenge}
  │                                  StartCoroutine(timeout)
  │ ◄── peer.m_rpc.Invoke("ServerGuard_RequestManifest", challenge) ──
  │                                                       │
  │   Handler runs on client:                             │
  │     BuildManifestCache()  // re-enumerate plugins     │
  │     manifest = { schemaVersion, challenge,            │
  │                  timestampUtc, mods }                 │
  │     manifest.hmac = HMACSHA256(canonical, secret)     │
  │     json = serialize(manifest)                        │
  │ ── peer.m_rpc.Invoke("ServerGuard_Manifest", json) ───►
  │                                                       │
  │   Handler runs on server:                             │
  │     pop {peer→challenge}                              │
  │     parse json                                        │
  │     verify challenge == pending.challenge             │
  │     verify |now - timestampUtc| ≤ maxClockSkewSeconds │
  │     verify HMAC                                       │
  │     check banned_mods                                 │
  │     check required_mods                               │
  │     check allowed_mods (if !allowUnlisted)            │
  │     ─ pass: log "attested OK"                         │
  │     ─ fail: AddViolation + TryKick                    │
  │                                                       │
  │   Coroutine fires after companionTimeoutSeconds:      │
  │     if pending entry still present:                   │
  │       AddViolation(CompanionMissing) + TryKick        │
```

### Replay-protection model

Each connect issues a fresh 24-byte random challenge bound to that specific `peer.m_uid`. The HMAC is over `(schemaVersion | challenge | timestampUtc | sortedMods)` — not just over the mod list. Replaying a captured manifest fails because:

- The challenge is one-shot (consumed when the manifest arrives or the timeout fires).
- The timestamp window is bounded by `maxClockSkewSeconds`.

Forging a manifest fails because the attacker doesn't know `sharedSecret`.

Tampering with a real manifest fails because any change to the JSON re-derives a different canonical string and breaks the HMAC.

---

## Configuration files

### Server-side, under `BepInEx/config/ServerGuard/conf/`

| File | Schema | Hot-reload |
|---|---|---|
| `settings.yaml` | C# class `Settings` with nested `CountAsViolation` (camelCase keys via `CamelCaseNamingConvention`) | yes |
| `admins.yaml` | `AdminsDoc { admins: [string] }` | yes |
| `allowed_mods.yaml` | `AllowedModsDoc { required_mods, allowed_mods, banned_mods : [string] }` — explicitly snake_case via `[YamlMember(Alias=…, ApplyNamingConventions=false)]` | yes |
| `registrations.yaml` | `RegistrationsDoc { registrations: { steamId → [characterName] } }` | no (auto-managed) |
| `violations.yaml` | `ViolationsDoc { violations: { steamId → { rule → count } } }` | no (auto-managed) |
| `metrics.yaml` | `DetectionMetrics` counters | no (auto-managed) |

`Settings` now contains a nested `CountAsViolation` class with one `bool` property per rule. Default-generated `settings.yaml` is written as an explicit string template (not via the `OmitDefaults` YAML serializer) so all options are visible on a fresh install.

Hot-reload is implemented in [Plugin.cs](Plugin.cs) via three `FileSystemWatcher` instances with a 200 ms debounce, plus a YAML re-parse on each event.

### Client-side, under `BepInEx/config/ServerGuard/`

| File | Schema |
|---|---|
| `client.yaml` | `ClientSettings { sharedSecret: string }` |
| `mods_for_allowed_mods.yaml` | Generated; copy-paste-ready snippet for the server |

---

## Key design decisions

### Why GUID-keyed allowlist instead of name-keyed

Display names (`Jotunn`, `Better Networking`) are arbitrary and don't survive renames. BepInEx plugin GUIDs are pinned in source via `[BepInPlugin("com.jotunn.jotunn", …)]` and rarely change. The server's matcher accepts both, but every export from the companion plugin is GUID-first because it's strictly safer.

### Why HMAC the canonical form, not the raw JSON

JSON serialization isn't deterministic — property ordering, whitespace, and number formatting can vary between platforms or versions. Building a deterministic canonical string from the structured manifest (`schemaVersion|challenge|timestamp|sortedMods…`) means the server and client always compute the same bytes regardless of how each one serializes its JSON.

The mods list is sorted by GUID (or name fallback) before hashing, so the order in which `Chainloader.PluginInfos` enumerates plugins doesn't affect the HMAC.

### Why the server schedules a timeout coroutine instead of relying on socket-level disconnect

The socket might stay open even if the client never replies (proxy, NAT, slow connection, missing companion). The companion timeout decides "no manifest in N seconds = kick" deterministically, with `companionTimeoutSeconds` configurable.

### Why `requireCompanion: true` is the default

The whole point of v1.3 is to deterministically distinguish vanilla from modded clients. With `requireCompanion: false`, vanilla clients silently pass (no manifest, no rejection) — defeating the security model. The setting exists for emergency fallback only.

### Why the client rebuilds its manifest on every server request

`Awake()` runs during BepInEx's chainloader iteration, so `Chainloader.PluginInfos` is partial. By the time the player actually connects to a server, every plugin is loaded. Rebuilding from `Chainloader.PluginInfos` on every request keeps the manifest accurate without needing a complex "all plugins loaded" event hook.

### Why the disconnect path uses `ZNet.Disconnect(peer)` not `Kick(peer)`

The reflection-based `Kick(ZNetPeer)` lookup found a Valheim method that queued a soft-kick request which the handshake outpaced — players got past the kick and into the world. `ZNet.Disconnect(peer)` is the public method Valheim's own console `kick` command uses, and it tears the connection down synchronously.

---

## Violation tracking

Each policy failure goes through `AddViolation(platformId, rule)`. Before incrementing any counter, `RuleCounts(rule)` consults the `countAsViolation` section of `settings.yaml`:

- If the rule's flag is `true` (the default for all unrecognised rules): the counter in `violations.yaml` is incremented, a Discord warning is sent, and if the count reaches `violationThreshold` the player is auto-banned.
- If the flag is `false`: the event is still logged and a "log-only" Discord note is sent, but the counter is **not** incremented and no ban is triggered.

| Rule | Trigger | `countAsViolation` default |
|---|---|---|
| `CompanionMissing` | No manifest within `companionTimeoutSeconds`. | `false` |
| `HmacInvalid` | HMAC mismatch / parse failure / clock outside skew window. | `false` |
| `ChallengeMismatch` | Manifest's challenge doesn't match what the server issued. | `false` |
| `RequiredModMissing` | A `required_mods` entry is absent from the manifest. | `false` |
| `DisallowedMod` | A manifest mod isn't in `allowed_mods` (when `allowUnlisted: false`), or hash pin mismatch. | `false` |
| `BannedMod` | A `banned_mods` entry is present in the manifest. | `false` |
| `CharacterNameLimitExceeded` | Player exceeded `characterLimit`. Tracked separately in `Patch_RPC_PeerInfo`. | `true` |
| `DevcommandAttempt` | Devcommand usage (reserved). | `true` |
| `SpeedHack` | Movement speed above threshold (reserved). | `true` |
| `IllegalItem` | Illegal item stack detected (reserved). | `true` |
| `StackOverflow` | Stack overflow exploit detected (reserved). | `true` |
| `AnimationCancel` | Animation-cancel exploit (reserved). | `false` |
| `SkillOverflow` | Skill above cap (reserved). | `true` |

The `countAsViolation` defaults are conservative — attestation failures (`CompanionMissing`, `HmacInvalid`, etc.) are log-only by default so a misconfigured client doesn't immediately rack up bans. Gameplay integrity rules (`SpeedHack`, `IllegalItem`, etc.) count by default. All defaults can be overridden in `settings.yaml`.

---

## Discord integration (v1.5)

### Dual-webhook routing

v1.4 splits Discord output into two channels with clear ownership:

| Destination | Method | Events |
|---|---|---|
| Public (`discordWebhookUrl`) | `SendPublic()` | Server boot/shutdown, player joins/leaves, player shouts, player deaths (non-admin), raid start/pause/resume/end |
| Admin (`discordAdminWebhookUrl`) | `SendAdmin()` | Admin login/logout, kicks, bans, violations, rejections, timeouts |

`SendPublic()` respects `maintenanceMode`: when true it reroutes to the admin webhook instead. `SendAdmin()` is unconditional. Both are `async Task` methods on the Plugin instance, called fire-and-forget (`_ = SendPublic(…)`) from event handlers except in `OnDestroy()` where the shutdown notification is sent synchronously (`.GetAwaiter().GetResult()`) before cleanup runs.

**Backward-compat alias:** `Settings` also declares a `discordWebhookUrlAdmin` property (the pre-v1.4 key name). `ResolvedAdminWebhookUrl` returns `discordAdminWebhookUrl` if set, otherwise falls back to `discordWebhookUrlAdmin`. This means old `settings.yaml` files migrate without any manual key rename.

### Server lifecycle notifications

- **Boot** — `SendPublic()` fires at the end of `Awake()`, after all YAML is loaded and Harmony patches are applied.
- **Shutdown** — fired synchronously at the top of `OnDestroy()`, before Harmony is unpatched and before the HTTP client is torn down, so it reliably exits even on crash-level shutdowns.

### Shout logging (`ServerGuard_Chat` RPC)

Current Valheim sends chat per-recipient (not broadcast), so a dedicated server only routes packets — never handles them. The companion client patches `Chat.SendText` and sends a `ServerGuard_Chat` ZRpc to the server when the local player shouts (`Talker.Type.Shout = 2`). The server's `OnChatReceived` handler verifies `type == 2`, bounds the text to 256 chars, resolves name/SteamID from the server-side peer (not the client payload), and calls `SendPublic()`.

### Player join/leave and admin login/logout notifications

- **Player join** — `Patch_RPC_PeerInfo.Postfix` fires after a peer's info is registered. Non-admin players get a `:video_game: joined` message on `SendPublic()`; admins get a `:shield: logged in` message on `SendAdmin()`.
- **Player leave** — `Patch_Disconnect.Prefix` fires before tear-down. Peers with no character name yet (failed attestation, pre-login) are skipped. Admins go to `SendAdmin()`, players to `SendPublic()`.

### Death logging (`ServerGuard_PlayerDeath` RPC)

The server cannot know who killed a dying player — that state lives only on the owning client. The companion patches `Player.OnDeath` as a **Prefix** (before the game clears `m_lastHit`) and sends a `ServerGuard_PlayerDeath` ZRpc to the server.

**Payload format:** `posX|posY|posZ|attackerKind|attackerLabel|causeHint` (pipe-separated, invariant-culture floats).

| `attackerKind` | Meaning | Discord output |
|---|---|---|
| `player` | Killed by another player | `killed by **Name** (SteamID)` — SteamID resolved via `registrations.yaml` |
| `creature` | Killed by a mob | `killed by a **Skeleton**` |
| `self` | Suicide / fall on own weapons | `took their own life` |
| `environment` | No attacker entity | `burned to death` / `froze to death` / `fell to their death` / etc. via `HumanizeDeathCause()` |

Admin deaths are **fully suppressed** — console-logged only, nothing posted to Discord. `attackerLabel` and `causeHint` are bounded to 48 and 24 characters respectively so a malicious client can't flood Discord. Name/SteamID are resolved server-side from the peer, not from the payload.

### Raid event logging (`Patch_SetRandomEvent`, `Patch_ResetRandomEvent`)

`Patch_SetRandomEvent` targets `RandEventSystem.SetRandomEvent(RandomEvent ev, Vector3 pos)` via `TargetMethod()` (private method). Harmony injects `ev` and `pos` directly by parameter name. A deduplication check (`ev.m_name == _currentRaidName`) prevents double-announcing if the method is called multiple times for the same event.

`Patch_ResetRandomEvent` is a Prefix on the public `ResetRandomEvent()`. Prefix (not Postfix) is used so `OnRaidEnded()` fires while the Plugin's tracked `_currentRaidName` is still set.

**Pause detection** runs in a coroutine (`MonitorRaidEvent`) that polls every 5 seconds while an event is active:

```
GetCurrentRandomEvent() != null  →  event is set/running
GetActiveEvent()        == null  →  no players in the event area
→ combined: event is paused (timer frozen)
```

State transitions trigger Discord messages:
- active → paused: `:pause_button:` message with coordinates
- paused → active: `:arrow_forward:` resume message

---

## Why heuristic detection was abandoned

Pre-v1.3 ServerGuard tried to identify modded clients server-side using:

1. **Phase 1 — RPC token sniffing.** Walk the peer's `m_rpc.m_methods` keys looking for substrings like `"Jotunn"`, `"ValheimPlus"`. Worked for mods that register server-bound RPCs; missed everything else.
2. **Phase 1 — Version keyword scanning.** Look for `"modded"` etc. in `peer.m_playerVersion`. Easily defeated by mods that don't taint the version string.
3. **Phase 2 — Assembly namespace scanning.** Iterate `AppDomain.CurrentDomain.GetAssemblies()` looking for namespaces like `Jotunn.*`. **This was scanning the SERVER's AppDomain, not the client's** — the `ZNetPeer peer` argument was never used inside the scan. Since the server itself runs BepInEx, every connection produced a `BepInEx` namespace match and was kicked. False-positive rate: 100%.

Beyond the implementation bugs, the fundamental problem is that none of these methods can enumerate the client's actual plugin set. The only solution is for the client to volunteer that information, with cryptographic protection against forgery — which is what v1.3 does.

The legacy code is gone (`DetectLikelyModdedClient`, `ScanPeerAssemblies`, `mod_patterns.yaml`, `ignore_mods.yaml`). Old YAML files are auto-renamed `*.legacy` on first launch under v1.3 so they don't confuse anyone.

---

## Source map

| File | Contents |
|---|---|
| [Plugin.cs](Plugin.cs) | Server entry point, all server logic, Harmony patches (`Patch_OnNewConnection`, `Patch_RPC_PeerInfo`, `Patch_Disconnect`, `Patch_SetRandomEvent`, `Patch_ResetRandomEvent`), RPC handlers (`OnChatReceived`, `OnPlayerDeathReceived`), all helpers, `SendPublic`/`SendAdmin` |
| [Shared/Manifest.cs](Shared/Manifest.cs) | `ModManifest`, `ModManifestEntry`, canonical-string builder, HMAC helpers, `ConstantTimeEquals` |
| [ServerGuard.Client/ClientPlugin.cs](ServerGuard.Client/ClientPlugin.cs) | Client entry point, manifest builder, deferred init coroutine, first-run export, Harmony patches (`Patch_RegisterClientHandler`, `Patch_Chat_SendText_Report`, `Patch_Player_OnDeath_Report`) |
| [Valheim-ServerGuard.csproj](Valheim-ServerGuard.csproj) | Server build config; auto-detects Valheim install via `$VALHEIM_PATH` |
| [ServerGuard.Client/Valheim-ServerGuard-Client.csproj](ServerGuard.Client/Valheim-ServerGuard-Client.csproj) | Client build config; links `../Shared/Manifest.cs` |
| [BUILD.md](BUILD.md) | How to build both DLLs from source |
| [DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md) | Step-by-step Windows install walkthrough |
| [README.md](README.md) | User-facing documentation (quick setup + advanced reference) |

---

## Version

**1.6.2** — feature + fix release.
- **Forced map positions** (`enableForceMapPositions`, `forceMapPositionsExemptAdmins`). `Patch_ForceMapPositions` postfixes the private `ZNet.RPC_ServerSyncedPlayerData` — the point where the server ingests each client's position sync — and sets `peer.m_publicRefPos = true` via `ApplyForcedMapPosition()`. `ZNet.UpdatePlayerList` then copies that into `PlayerInfo.m_publicPosition` for the broadcast player list. Re-applied on every ~2s sync, so it's authoritative and hot-reloads both ways.
- **Sheathe dropped from the AnimationCancel rule.** `Patch_Humanoid_HideHandItems_BlockDuringAttack` deleted from the client; `OnAnimationCancelReceived` also discards any source in `_animationCancelIgnoredSources` so companions from 1.6.1 and earlier stop generating strikes without every player having to update.
- **Arrival shout toggle** (`enableArrivalShout`). New server→client `ServerGuard_ArrivalShout` RPC pushed on connect (before the admin early-return) and re-broadcast from `LoadSettings()` on hot-reload. The companion brackets `Game.UpdateRespawn` with a frame stamp and drops the Shout raised inside it — no text matching, so it works in every language and won't eat a manual "I have arrived!". `Patch_Chat_SendText_Report` became a `bool` prefix to host both the report and the block (a prefix returning `false` skips the remaining prefixes, so splitting them would have been order-dependent).
- **Server lifecycle notifications restored and split.** They existed in the 1.4.0/1.5.0 lineages and were dropped by the `1850200` merge, which took main's `Awake`/`OnDestroy` wholesale. `Awake` now posts "Server is starting..."; `ServerReadyWatcher()` polls `IsServerReadyForPlayers()` (`ZNet.IsServer()` + `ZoneSystem.LocationsGenerated`) once a second for up to 15 min and posts "The server has started, you may now login."; `PostShutdownNoticeBlocking()` runs first in `OnDestroy` and posts synchronously, because a fire-and-forget `Task` gets killed by process exit.

**1.6.1** — bug-fix release. The Quick Login panel's live player count always rendered as `Players: ?` because the A2S_INFO query never answered Valve's `S2C_CHALLENGE` (`0x41`) packet, which the Steam game-server API has required since December 2020. `RefreshPlayerCount` now performs the challenge handshake via `QueryA2SInfo`/`BuildA2SInfoRequest`/`ParseA2SInfo`, queries the correct port first (`gamePort + 1`), and runs the blocking UDP exchange on a background thread instead of stalling the title screen.

**1.6.0** — merge of the two lineages into one codebase. Keeps the 1.4.0 anti-cheat gates, `sg` admin console, build/death forensics, two-channel Discord routing, and self-test; adds raid event logging with **in-game display names** (via `RaidDisplayNames` map on `Patch_SetRandomEvent`/`Patch_ResetRandomEvent`), player shout logging (`ServerGuard_Chat` ZRpc), cheat-item removal on login (`ServerGuard_RemoveItems` ZRpc; `enableCheatItemRemoval`/`cheatItems`), and the client-side **Quick Login** title-screen panel (direct connect via `m_queuedJoinServer` re-asserted in an `OnCharacterStart` prefix; static `FejdStartup.ServerPassword` skips the password prompt).

**1.5.0** — player shout logging (client-reported via `ServerGuard_Chat` ZRpc; whisper logging removed — architecture limitation), player death logging with attacker attribution (client-reported via `ServerGuard_PlayerDeath` ZRpc, admin deaths suppressed), explicit player join/leave Discord notifications, explicit admin login/logout Discord notifications.

**1.4.0** — dual-webhook Discord routing, maintenance mode, server lifecycle notifications (boot/shutdown), raid event logging with pause detection, `countAsViolation` per-rule counting control, full settings.yaml restoration (all options visible on fresh install), `discordWebhookUrlAdmin` backward-compat alias.

**1.3.0** — first release of the client-attestation architecture.

The version string is set independently in:
- `Plugin.cs` — `[BepInPlugin("com.taeguk.valheim.serverguard", "Valheim ServerGuard", "1.6.2")]` + hardcoded `v1.6.2` in log/config strings
- `ClientPlugin.cs` — `public const string VERSION = "1.6.2";`
- `Valheim-ServerGuard.csproj` — `<Version>1.6.2</Version>`
- `ServerGuard.Client/Valheim-ServerGuard-Client.csproj` — `<Version>1.6.2</Version>`
- `Thunderstore files/Valheim-ServerGuard (server)/manifest.json` — `"version_number": "1.6.2"`
- `Thunderstore files/Valheim-ServerGuard (client)/manifest.json` — `"version_number": "1.6.2"`
- `README.md`, `claude/IMPLEMENTATION_SUMMARY.md`, `DEPLOYMENT_GUIDE.md`, `BUILD.md` — inline version references
- Both Thunderstore `README.md` and `CHANGELOG.md` files

Bump all locations together when releasing. Add a new `## x.y.z` section at the top of each `CHANGELOG.md`; do not rename the previous heading.
