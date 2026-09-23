# Implementation Summary — Valheim ServerGuard

This is a technical summary of how ServerGuard works for someone who wants to read or modify the code. For installation/usage docs see [README.md](../README.md) and [DEPLOYMENT_GUIDE.md](../DEPLOYMENT_GUIDE.md).

---

## What problem this solves

A vanilla Valheim dedicated server has no way to know which BepInEx plugins a connecting client has loaded. The client never tells the server, and server-side heuristics (sniffing custom RPC names, scanning the server's own AppDomain) are unreliable or outright broken — see "Why heuristic detection was abandoned" below.

ServerGuard v1.3 solves it by making the client tell the server, in a way the server can verify:

- The **client half** of the same mod runs on every player's game (pre-2.0 this was a separate "companion" package; 2.0 merged both into one DLL that picks its side at load).
- On connect, the server challenges the client to send a **manifest** of its loaded plugins.
- The manifest is **signed with HMAC-SHA256** using a secret shared between server and clients.
- The server checks the signature, the freshness, and whether the listed mods are on its allowlist.

Vanilla clients don't run ServerGuard, so they never reply — and get kicked on timeout.

The optional **Customs** subsystem builds on that attested connection. The client declares full inventory snapshots; the server compares an arriving character with its last trusted departure baseline and can log or refuse positive deltas. It is off by default and defaults to dry run when enabled.

---

## High-level architecture

```
                ┌───────────────────────────────┐
                │ Shared/Manifest.cs            │
                │   ModManifest DTO             │
                │   Canonical-string format     │
                │   HMAC-SHA256 helpers         │
                └────────────┬──────────────────┘
                             │
                ┌────────────┴──────────────────┐
                │ ServerGuardPlugin.cs          │  ← the only [BepInPlugin]
                │   headless? → ServerPlugin    │
                │   else     → ClientPlugin     │
                │   PatchNested(harmony, half)  │
                └────────────┬──────────────────┘
              ┌──────────────┴─────────────────┐
              │                                │
              ▼                                ▼
┌──────────────────────────┐      ┌────────────────────────────┐
│ ServerPlugin.cs          │      │ ClientPlugin.cs            │
│ (server half)            │      │ (client half)              │
│                          │      │                            │
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
│ • Patch_ZNet_RPC_Remote- │
│   Command / ListContainsId
│   (staff dev commands)   │
│ • SendPublic / SendAdmin │
│ • TryKick / TryBan       │
│ • Customs sessions/store │
└──────────────────────────┘
```

One project, one DLL, installed unchanged on both sides:

| Project | Output |
|---|---|
| [Valheim-ServerGuard.csproj](../Valheim-ServerGuard.csproj) | `Valheim-ServerGuard.dll` |

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
| `moderators.yaml` | `AdminsDoc { admins: [string] }` — the MODERATOR tier (filename kept for upgrade safety) | yes |
| `owners.yaml` | `OwnersDoc { owners: [string] }` — the OWNER tier, exempt from every rule | yes |
| `bans.yaml` | `BansDoc { bans: [BanEntry{ id, reason, expires, added, addedBy }] }` — written by hand (not via `_yamlOut`) so the explanatory header survives every rewrite | yes |
| `allowed_mods.yaml` | `AllowedModsDoc { required_mods, allowed_mods, banned_mods : [string] }` — explicitly snake_case via `[YamlMember(Alias=…, ApplyNamingConventions=false)]` | yes |
| `registrations.yaml` | `RegistrationsDoc { registrations: { steamId → [characterName] } }` | yes (auto-managed; own writes are ignored by the watcher) |
| `violations.yaml` | `ViolationsDoc { violations: { steamId → { rule → count } } }` | yes (auto-managed; own writes are ignored by the watcher) |
| `metrics.yaml` | `DetectionMetrics` counters | no (auto-managed) |

`Settings` now contains a nested `CountAsViolation` class with one `bool` property per rule. Default-generated `settings.yaml` is written as an explicit string template (not via the `OmitDefaults` YAML serializer) so all options are visible on a fresh install.

Hot-reload is implemented in `ServerPlugin.cs` (`StartWatchers`) via one `FileSystemWatcher` per file with a 200 ms debounce, plus a YAML re-parse on each event. `registrations.yaml` and `violations.yaml` are written by the plugin itself on every strike/registration, so their watchers go through `Reload*IfExternallyEdited`, which compares the on-disk text against `_lastWrittenRegistrations` / `_lastWrittenViolations` and skips the reload when the event is just our own save echoing back. `sg reload` re-reads all seven files.

### Client-side, under `BepInEx/config/ServerGuard/`

| File | Schema |
|---|---|
| `client.yaml` | `ClientSettings { sharedSecret: string }` |
| `mods_for_allowed_mods.yaml` | Generated; copy-paste-ready snippet for the server |

### Customs baselines, under `BepInEx/config/ServerGuard/customs/`

Customs is lazy: constructing the store and looking up absent baselines creates no directory. Once needed, each character has `<steamid>/<characterId>.json`; atomic replacement retains `.bak`, parse-corrupt files are quarantined, and `approvals.json` holds one-shot approvals. Unreadable/newer-schema baselines are admitted unjudged and never overwritten. Accepted writes queue in memory, reads prefer queued state, and departure/shutdown force a flush attempt.

## Customs lifecycle and trust boundary

`OnManifestReceived` starts Customs only after attestation succeeds. The server sends `ServerGuard_CustomsRequest`; the client answers with a `declare` snapshot max-merged around first spawn, then full `change`, `checkpoint` and best-effort `logout` snapshots. The server binds every report to the peer's SteamID, request nonce, increasing sequence, server-observed character name and first character ID. A refused/unusable/replayed report cannot advance the baseline, and a newer connection supersedes an older writer for the same character.

Modes are disabled / dry run / enforce. Unknown mode values and global `enforce: false` resolve to dry run. New characters in enforce follow `fresh` / `any` / `approve`; approval is one-shot and expires after 24 hours. Operators use `sg customs [status]`, `inspect`, `approve`, `unapprove` and offline-only `reset`.

This is not a server-authoritative inventory: a client that defeats attestation can lie, and with `requireCompanion: false` a peer that never attests is not inspected. Only peers with a resolvable SteamID64 are inspected. The client reads the player's primary inventory (including extra rows in the same `Inventory`), not separate mod-owned containers; live-game RPC/spawn behavior and AzuExtendedPlayerInventory have not been validated by the Unity-free tests.

The test project `tests/ServerGuard.Tests/ServerGuard.Tests.csproj` links the production `Shared/CustomsProtocol.cs` and `Shared/CustomsLedger.cs` directly. Its 96 tests cover item normalization/deltas, wire bounds, policy verdicts, session replay/rate/character binding, no-poisoning transitions, approvals and real-file store recovery. It does not replace the merged net462 plugin build.

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
| [ServerGuardPlugin.cs](../ServerGuardPlugin.cs) | Only BepInEx entry point; selects server/client half and patches only that half's nested Harmony classes |
| [ServerPlugin.cs](../ServerPlugin.cs) | Server lifecycle, settings, rules, RPC handlers, admin commands and Customs Unity-facing orchestration |
| [ClientPlugin.cs](../ClientPlugin.cs) | Manifest/report senders, client enforcement/UI and Customs spawn capture/report loop |
| [Shared/Manifest.cs](../Shared/Manifest.cs) | `ModManifest`, `ModManifestEntry`, canonical-string builder, HMAC helpers, `ConstantTimeEquals` |
| [Shared/CustomsProtocol.cs](../Shared/CustomsProtocol.cs) | Customs item identity/normalization, limits, request framing and strict report parser |
| [Shared/CustomsLedger.cs](../Shared/CustomsLedger.cs) | Customs policy, session state machine, verdict engine, baseline store and approvals |
| [tests/ServerGuard.Tests](../tests/ServerGuard.Tests) | Unity-free harness linking the two production Customs shared files (96 tests) |
| [Valheim-ServerGuard.csproj](../Valheim-ServerGuard.csproj) | One merged net462 build; resolves local Valheim/BepInEx references |
| [BUILD.md](../BUILD.md) | How to build the merged DLL and run Unity-free tests |
| [DEPLOYMENT_GUIDE.md](../DEPLOYMENT_GUIDE.md) | Step-by-step Windows install walkthrough |
| [README.md](../README.md) | User-facing documentation (quick setup + advanced reference) |
| [wiki/Customs.md](../wiki/Customs.md) | Operator rollout, settings, commands, persistence and known limitations |

---

## Version

**2.0.2** — **`sg unregister` + hot-reload for `registrations.yaml` / `violations.yaml`.** Both files were loaded once at `Awake` and never re-read; `SaveRegistrations` / `SaveViolations` then wrote the in-memory dictionaries back, so a hand-edit on a running server was silently reverted and `sg unban` alone could not free a player over `characterLimit` (`Patch_RPC_PeerInfo` still saw the old names, struck them, and `AddViolation` re-banned at threshold). Now: `CmdUnregister` drops one or all names and saves; `StartWatchers` adds `_watchRegistrations` / `_watchViolations` routed through `ReloadRegistrationsIfExternallyEdited` / `ReloadViolationsIfExternallyEdited`, which compare the on-disk text to `_lastWrittenRegistrations` / `_lastWrittenViolations` and skip the reload when the event is our own save echoing back (these files are written far more often than `bans.yaml`, so an unconditional reload would race the next strike); `CmdReload` re-reads all seven files. `unregister` is in the mutating-command audit list.

**2.0.1** — **`cheatTaintBypassPolicy`.** The `bypasscheatchecks` unique key (set by `yesiuseddevcommandsbutiwantmyachievementsanyway`) switches every vanilla cheat-marking site off for that character, so the whole 2.0 cheat-taint family is blind to it. New setting `cheatTaintBypassPolicy` (`log` default / `kick`), normalised by `NormalizedCheatTaintBypassPolicy`; under `kick`, `OnCheatStateReceived` posts `:no_entry_sign:` once per session and calls `TryKick` on **every** report carrying the flag (the disconnect clears `_cheatTaintState`, so a reconnect on the same character is refused again), returning before the item logic. Not a rule — no `AddViolation`. Owners exempt via `CheatTaintExempt` and `TryKick`; moderators only via `cheatTaintExemptModerators`. Metric `cheat_taint_bypass_kicks`; `sg status` shows `bypass=`; settings template documents it. Prompted by a moderator on the live server who had run the command in single-player.

**2.0.0** — **one mod for both sides, and staff dev commands.** The server plugin and the client companion are merged into a single assembly with a single `[BepInPlugin]` (`ServerGuardPlugin`, GUID `com.taeguk.valheim.serverguard`). `ServerPlugin` (was `Plugin.cs`) and `ClientPlugin` are plain `MonoBehaviour`s; the entry plugin attaches one of them based on `SystemInfo.graphicsDeviceType == Null` (headless ⇒ server), overridable via BepInEx cfg `General.Mode`. Each half patches only its own nested patch classes through `ServerGuardPlugin.PatchNested` — `Harmony.PatchAll()` is gone. The old client GUID is aliased to the new one in `ParseAllowedList` so existing `required_mods` entries survive. `ClientPlugin.VERSION` and every server version string read `ServerGuardPlugin.VERSION`. New feature: `enableOwnerDevcommands` / `enableModeratorDevcommands` / `moderatorDevcommands`. Client: `Patch_Terminal_IsCheatsEnabled` returns `m_cheat` under a grant, `Patch_ConsoleCommand_IsValid` re-validates permitted non-`RemoteCommand` commands without the `IsCheat`/`OnlyServer` terms, and `ShouldBlockConsoleCommand` enforces the moderator list first (category `moderator`, no strike). Server: `Patch_ZNet_RPC_RemoteCommand` runs forwarded commands for granted staff with `Terminal.m_cheat` forced on per call, `Patch_ZNet_ListContainsId` makes owners vanilla admins, `Patch_RandEvent_Console*` covers `randomevent`/`stopevent` for moderators. The policy push gained `devMode|devCsv`. Default moderator list: `goto pos removedrops stopevent find`; `fly debugmode spawn itemset nocost noplacementcost location` can never be granted to moderators. Moderators now **attest like players** (only owners skip), with a new `moderator_allowed_mods:` list in `allowed_mods.yaml`. Moderators get a chat welcome on every login (greeting, live command list, `sg help`, responsibility note); everyone gets a one-per-launch cheat-detection notice popup (`UnifiedPopup`/`WarningPopup`) when the feature is on; staff see cursor world coordinates on the large map (`Patch_Minimap_UpdateBiome_StaffCoords`). `speedCheckMaxMetersPerSecond` default 15 → 70. **Cheat taint detection** (rules `CheatedItem` / `CheatedBuild` / `DebugFly`, all informational by default; `cheatTaintPolicy` log/strip/violation): the client reports Valheim 1.0's `ItemData.m_cheated` inventory items (`ServerGuard_CheatState`), the build report carries `PlacePiece`'s `cheated` flag and the server confirms it against the piece ZDO, and the speed loop reads the player ZDO's `DebugFly`. Thunderstore: the `(client)` package folder is removed; the server folder is now `Thunderstore files/Valheim-ServerGuard/`. Verified: headless boot on Valheim 1.0.7 → `starting as SERVER`, `Applied 14 server-side Harmony patch class(es)`, self-test 8/0.

**1.8.1** — compatibility release for **Valheim 1.0.7** (network version 39, Unity 6000.0.75, BepInEx 5.4.23.5). **No code changes in either plugin**; the version exists to carry the changelog note and keep the pair matched.

Valheim 1.0 needed no fixes. Verified 2026-09-09 by (a) compiling both plugins against the 1.0 assemblies, (b) resolving all ~123 string-based lookups — Harmony patch targets and `GetField`/`GetMethod` names — against `assembly_valheim.dll` with a `MetadataLoadContext` tool and diffing the result against a pre-1.0 baseline, and (c) booting the dedicated server headless and confirming the plugin loads with `Self-test pass=8 fail=0` and no errors. Step (b) is the one that matters: compiling proves almost nothing here, because most Valheim access is by string and fails silently. Keep a pre-update copy of `assembly_valheim.dll` — the 5 members that report "missing" are pre-existing fallback paths (`ZNetPeer.m_platformUserID`, `ZRpc.GetUID`/`m_ping`, …), and without a baseline they read as regressions.

1.0 changes that were checked and are harmless:
- `Player.PlacePiece` gained a trailing `bool cheated`. `Patch_PlacePiece_Report` is a Postfix binding `piece`/`pos` **by name**, and it is the only overload, so Harmony still resolves it. **`cheated` is an unused anti-cheat signal worth adopting** — but binding it would break the patch on pre-1.0, where the parameter doesn't exist.
- `Terminal.ConsoleCommand`'s constructor went 12 → 13 args (added `onlyAdmin`) and gained a `HideBehindDevCommands` field. ServerGuard never constructs one, so it is unaffected — but this breaks *other* mods (Server Devcommands 1.109 throws `MissingMethodException` at startup). `ConsoleCommand.IsCheat` is unchanged, and `ReadBoolMember` already probes `IsCheat` first, so dynamic cheat detection still works.
- `ZoneSystem.m_instance` → `s_instance`; the code uses the `instance` property. `Inventory.AddItem(ItemData)` — the patched overload — survived, though several sibling overloads changed. `Version.m_networkVersion` → `c_networkVersion` (36 → 39), unreferenced.
- TMP kept `enableWordWrapping` alongside Unity 6's new `textWrappingMode`, and `TMP_TextUtilities.FindIntersectingLink(TMP_Text, Vector3, Camera)` is unchanged, so the 1.8.0 announcements panel and its link handler still work.

Not resolved: 46 registered console commands (`setkeyplayer`, `findbiometp`, `nospawn`, `repairall`, …) sit in no console-guard tier. Whether any are *new* in 1.0 could not be established — Steam overwrote the pre-1.0 assembly before a command-list diff was taken. An attempt to classify them by reading the `isCheat` ctor argument out of `Terminal.InitTerminal` IL produced visibly drifting values (`findbiometp` → `7` for a bool), so that output is indicative only and was not acted on. See `claude/console-guard.md` before changing the tiers.

**1.8.0** — client-only feature release; the server plugin is version-matched but functionally unchanged.
- **Quick Login announcements.** New `serverAnnouncements` key in `client.yaml` (a YAML `|` block scalar, so the editing surface is the same file as the rest of the panel settings). `EnsureConfig` gained a migration branch: the file is still only *written* when missing, but an existing one without the key gets `AnnouncementsYamlBlock()` appended, since otherwise nobody upgrading would ever see the option. Empty (the default) omits the header and the box entirely, so the 1.7.0 panel is unchanged.
- **Scroll box.** `BuildAnnouncementsScrollBox` hand-builds the standard `ScrollRect` → viewport → content → text hierarchy (no prefab to clone). The viewport carries a **`RectMask2D`** rather than a `Mask`: no extra material, and it implements `ICanvasRaycastFilter`, so links scrolled out of view aren't clickable through the clip. It also carries a fully transparent `Image` with `raycastTarget` on — without a `Graphic` under the pointer the mouse wheel has nothing to bubble an `IScrollHandler` event up from. Content height can't come from a `ContentSizeFitter`: the cloned menu-button label's own `ContentSizeFitter`/`LayoutElement` are `Destroy()`d but stay alive for the rest of the frame, so `FitAnnouncementContent` defers a frame and measures `preferredHeight` explicitly, twice (first pass establishes the width, second measures against it), falling back to `GetPreferredValues(width, 32767)` if the property reads 0. Height is floored at the viewport height — a content rect smaller than its viewport makes `ScrollRect` place it oddly.
- **Layout.** The panel is no longer a fixed 320×440: with announcements present it grows to `Mathf.Clamp(-contentTop + 266, 560, 720)` so the scroll box always clears `AnnMinViewport` (140px) regardless of logo/description height, and the player count moves from top-flowed to bottom-anchored (`CreateThemedLabelComponent` gained `anchorBottom`/`bottomOffset`) so the box owns the flexible middle.
- **Links.** `[label](url)` is rewritten to TMP `<link="url">` markup by `FormatAnnouncementsRich`; everything else passes through, so `<b>`/`<i>`/`<color>` work too. Clicks are handled by `AnnouncementLinkClicker`, a nested `MonoBehaviour` implementing `IPointerClickHandler` — the project doesn't reference `Unity.TextMeshPro`, so the hit test resolves `TMPro.TMP_TextUtilities.FindIntersectingLink(TMP_Text, Vector3, Camera)` by reflection (matched by parameter *shape*, since the `TMP_Text` type can't be named at compile time) and reads `textInfo.linkInfo[i].GetLinkID()` the same way. **Only `http://` and `https://` are opened** — `client.yaml` ships inside modpacks, so the text isn't necessarily written by the person at the keyboard, and a click must not be able to launch `file://` or a custom scheme handler. A URL that fails the check isn't styled as a link either, so nothing looks clickable that won't be.

**1.7.0** — feature release. Three new subsystems.
- **Privilege tiers.** New `conf/owners.yaml` (`OwnersDoc`, hot-reloaded, fails **closed** on parse error). `IsAdmin` keeps its old meaning and is redefined as `IsModerator || IsOwner`, so every pre-existing call site is unchanged and owners inherit all staff bypasses by being a superset; `RoleOf` returns `owner`/`moderator`/`player`. The owner bypass is enforced at choke points rather than per rule: `AddViolation` returns early (which is what makes "exempt from every rule" true by construction — every rule funnels through it), `TryKick` refuses, `IsBannedId` returns false before the lookup, `AddBan` refuses to write, plus explicit exemptions in `ApplyForcedMapPosition` and `SendCheatItemRemovalIfEnabled`. `moderators.yaml` deliberately keeps its filename — renaming it would silently drop every existing server's staff list on upgrade. See `claude/privilege-tiers.md`.
- **SteamID ban layer** (`enableBanLayer`, `banLayerKickMessage`, `banLayerMirrorToVanilla`; list in `conf/bans.yaml`, hot-reloaded). Vanilla applies `banlist.txt` from `ZNet.UpdateBanList`, which only runs when `m_banlistTimer > 5f` and then calls `InternalKick` — hence the observed "banned player plays for a few seconds first". The primary gate is `Patch_ZNet_IsAllowed`, a **postfix** on the private `ZNet.IsAllowed(hostName, playerName)` called from `RPC_PeerInfo` *before* the peer is accepted; setting `__result = false` makes vanilla send `ConnectionStatus.ErrorBanned` (8) and return, so no character is spawned. Postfix rather than prefix so vanilla decides first and we only ever flip allow→deny (a `permittedlist.txt` whitelist keeps working). A second gate sits at the top of `Patch_OnNewConnection` reading `peer.m_socket.GetHostName()` — the SteamID64 on Steam sockets — which fires before any PeerInfo round-trip; it calls `DisconnectAsBanned` and returns before registering handlers. `SweepBannedPeers()` is the third layer, applying a new ban to peers already online. `LoadBans` fails **open** on a parse error (keeps the last good list) so a malformed file can't lock a server out; `BanEntry.IsExpired` fails **closed** on an unparseable `expires`. `TryBan` (the `violationThreshold` auto-ban) now routes through `AddBan`. `sg ban` / `sg unban` / `sg bans`; new metric `ban_layer_blocks`. See `claude/ban-layer.md`.
- **Console guard** (`consoleGuardMode`, `consoleGuardExemptModerators`, `consoleGuardBindPolicy`, `consoleBlockedCommands`, `consoleAllowedCommands`, `consoleGuardReportAttempts`). Server config pushed to the companion over the new `ServerGuard_ConsolePolicy` RPC on connect and re-broadcast on settings.yaml, moderators.yaml **and** owners.yaml reload (the payload carries the recipient's resolved exemption). Client enforcement: `ShouldBlockConsoleCommand` in the existing `Patch_TryRunCommand`; `Patch_Console_IsConsoleEnabled` forces `__result = false` under `disabled` mode, which kills F5 and the gamepad chord in one hook because `Console.Update` early-returns on that check; `Patch_ZNet_Shutdown_ResetPolicy` restores defaults on disconnect. The blocklist grew from 27 entries to ~90, split into `CheatCommands` / `RiskyCommands` / `BindCommands`, with `VanillaAdminCommands` documented but deliberately unblocked. New rule `ConsoleCommandBlocked` (default `false` in `countAsViolation`) separates non-cheat blocks from real cheat attempts so `bind` doesn't produce a public "tried to use cheats" post; new metric `console_blocks`.
- **Key binds.** `bind` persists to `PlatformPrefs["ConsoleBindings"]`, `Chat.Awake` reloads it, and `Chat.Update` — **not** `Console.Update` — dispatches binds. So a bind fires without the console being open or even openable, and it survives across sessions: a player can set one offline and arrive with it armed. `ApplyBindPolicy()` clears `Terminal.m_binds`, and `Patch_Terminal_UpdateBinds` re-clears from a postfix on `Terminal.updateBinds` — provably complete, because that method is the only writer of `m_binds` in `assembly_valheim`. `wipe` additionally clears `m_bindList` and re-invokes `updateBinds()` to overwrite the pref (self-terminating: the second pass finds both collections empty).
  - The `skipAllowedCheck: true` that `Chat.Update` passes is **not** an escalation, contrary to how it reads: in `ConsoleCommand.IsValid` the `IsCheat` test runs first and is not covered by that flag, and the test the flag does cover (`isAllowedCommand`) compiles to `return true`. So the flag is currently a no-op. The real bind threat is running **non-cheat** and mod-registered commands on a keypress.
- **Scope note on the cheat tier.** `Terminal.IsCheatsEnabled()` is `m_cheat && ZNet.instance && ZNet.instance.IsServer()`, so on a dedicated-server client it is *always* false — vanilla already refuses every `IsCheat` command there, typed or bound, and the debug-hotkey block in `Player.Update` (gated on the same call) is dead. Verified by IL scan: `ConsoleCommand.RunAction` has exactly one caller, `Terminal.TryRunCommand`, so our prefix is a complete gate over every registered command regardless of dispatcher (typed, chat, bind, `Player.Update`, `ZNet.InternalCommand`, CLI). Of the debug hotkeys, `K`/`L` dispatch through `TryRunCommand("killenemies"/"removedrops")` and would be caught anyway; `B`/`Z` call `Player.ToggleNoPlacementCost`/`ToggleDebugFly` directly and would not. The `CheatCommands` tier is therefore defence-in-depth — it earns its place against mods that re-enable devcommands server-side (Server_devcommands), commands mods register without `IsCheat`, and listen-server hosts. The tier doing load-bearing work on an ordinary client is `RiskyCommands`.
- **Fixed: `IsRegisteredCheatCommand` never worked.** It picked the first static `IDictionary` field on `Terminal`, which is `m_testList` (`Dictionary<string,string>`), not `commands` (`Dictionary<string, Terminal.ConsoleCommand>`). Every `IsCheat` lookup returned false, so only the hardcoded list was ever enforced. `ResolveTerminalCommands` now matches on generic argument types and caches the result.

**1.6.3** — bug-fix release. The 1.6.2 arrival-shout gate suppressed *every* shout when `enableArrivalShout: false`, because it decided membership with a frame stamp (`_respawnUpdateFrame == Time.frameCount`) and `Game` calls `UpdateRespawn` on **every** frame — so "UpdateRespawn ran this frame" is always true and matched anything the player typed. `Patch_Game_UpdateRespawn_ArrivalShout` now sets a plain `_inRespawnUpdate` bool in a Prefix and clears it in a Postfix, a true bracket around the call. A Postfix does not run if the original throws, so the flag could still latch on; the new `_arrivalShoutConsumed` one-shot bounds that to a single lost shout instead of a session-long mute, and both flags reset in `OnArrivalShoutPolicyReceived` (which fires on every connect). Server-side code is unchanged — 1.6.3 exists on the server only to keep the version match.

**1.6.2** — feature + fix release.
- **Forced map positions** (`enableForceMapPositions`, `forceMapPositionsExemptAdmins`). `Patch_ForceMapPositions` postfixes the private `ZNet.RPC_ServerSyncedPlayerData` — the point where the server ingests each client's position sync — and sets `peer.m_publicRefPos = true` via `ApplyForcedMapPosition()`. `ZNet.UpdatePlayerList` then copies that into `PlayerInfo.m_publicPosition` for the broadcast player list. Re-applied on every ~2s sync, so it's authoritative and hot-reloads both ways.
- **Sheathe dropped from the AnimationCancel rule.** `Patch_Humanoid_HideHandItems_BlockDuringAttack` deleted from the client; `OnAnimationCancelReceived` also discards any source in `_animationCancelIgnoredSources` so companions from 1.6.1 and earlier stop generating strikes without every player having to update.
- **Arrival shout toggle** (`enableArrivalShout`). New server→client `ServerGuard_ArrivalShout` RPC pushed on connect (before the admin early-return) and re-broadcast from `LoadSettings()` on hot-reload. The companion brackets `Game.UpdateRespawn` and drops the Shout raised inside it — no text matching, so it works in every language and won't eat a manual "I have arrived!". (1.6.2 shipped this bracket as a frame stamp, which matched every frame; see 1.6.3.) `Patch_Chat_SendText_Report` became a `bool` prefix to host both the report and the block (a prefix returning `false` skips the remaining prefixes, so splitting them would have been order-dependent).
- **Server lifecycle notifications restored and split.** They existed in the 1.4.0/1.5.0 lineages and were dropped by the `1850200` merge, which took main's `Awake`/`OnDestroy` wholesale. `Awake` now posts "Server is starting..."; `ServerReadyWatcher()` polls `IsServerReadyForPlayers()` (`ZNet.IsServer()` + `ZoneSystem.LocationsGenerated`) once a second for up to 15 min and posts "The server has started, you may now login."; `PostShutdownNoticeBlocking()` runs first in `OnDestroy` and posts synchronously, because a fire-and-forget `Task` gets killed by process exit.

**1.6.1** — bug-fix release. The Quick Login panel's live player count always rendered as `Players: ?` because the A2S_INFO query never answered Valve's `S2C_CHALLENGE` (`0x41`) packet, which the Steam game-server API has required since December 2020. `RefreshPlayerCount` now performs the challenge handshake via `QueryA2SInfo`/`BuildA2SInfoRequest`/`ParseA2SInfo`, queries the correct port first (`gamePort + 1`), and runs the blocking UDP exchange on a background thread instead of stalling the title screen.

**1.6.0** — merge of the two lineages into one codebase. Keeps the 1.4.0 anti-cheat gates, `sg` admin console, build/death forensics, two-channel Discord routing, and self-test; adds raid event logging with **in-game display names** (via `RaidDisplayNames` map on `Patch_SetRandomEvent`/`Patch_ResetRandomEvent`), player shout logging (`ServerGuard_Chat` ZRpc), cheat-item removal on login (`ServerGuard_RemoveItems` ZRpc; `enableCheatItemRemoval`/`cheatItems`), and the client-side **Quick Login** title-screen panel (direct connect via `m_queuedJoinServer` re-asserted in an `OnCharacterStart` prefix; static `FejdStartup.ServerPassword` skips the password prompt).

**1.5.0** — player shout logging (client-reported via `ServerGuard_Chat` ZRpc; whisper logging removed — architecture limitation), player death logging with attacker attribution (client-reported via `ServerGuard_PlayerDeath` ZRpc, admin deaths suppressed), explicit player join/leave Discord notifications, explicit admin login/logout Discord notifications.

**1.4.0** — dual-webhook Discord routing, maintenance mode, server lifecycle notifications (boot/shutdown), raid event logging with pause detection, `countAsViolation` per-rule counting control, full settings.yaml restoration (all options visible on fresh install), `discordWebhookUrlAdmin` backward-compat alias.

**1.3.0** — first release of the client-attestation architecture.

The version string is set in:
- `ServerGuardPlugin.cs` — `public const string VERSION = "2.0.2";` (the only literal in code; `ServerPlugin`'s log/config strings and `ClientPlugin.VERSION` read it)
- `Valheim-ServerGuard.csproj` — `<Version>2.0.2</Version>`
- `Thunderstore files/Valheim-ServerGuard/manifest.json` — `"version_number": "2.0.2"`
- `README.md`, `CLAUDE.md`, `claude/IMPLEMENTATION_SUMMARY.md`, `wiki/Home.md`, `wiki/Discord-Integration.md` — inline version references
- The Thunderstore `README.md` and `CHANGELOG.md`

Bump all locations together when releasing. Add a new `## x.y.z` section at the top of `CHANGELOG.md`; do not rename the previous heading.
