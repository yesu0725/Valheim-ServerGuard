# Implementation Summary — Valheim ServerGuard v1.3

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
│ • EnsureFolders          │      │ • Awake                    │
│ • Load*Yaml              │      │ • EnsureConfig             │
│ • Patch_OnNewConnection  │      │ • DeferredInit             │
│   - register receiver    │      │ • BuildManifestCache       │
│   - send challenge       │      │ • ExportAllowedModsSnippet │
│   - schedule timeout     │      │ • Patch_RegisterClientHandler
│ • OnManifestReceived     │      │   - register reply handler │
│   - validate HMAC        │      │ • BuildManifestJson        │
│   - check policy         │      │   (rebuilt on every request)
│ • Patch_RPC_PeerInfo     │      └────────────────────────────┘
│   (character limit)      │
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
| `settings.yaml` | C# class `Settings` (camelCase keys via `CamelCaseNamingConvention`) | yes |
| `admins.yaml` | `AdminsDoc { admins: [string] }` | yes |
| `allowed_mods.yaml` | `AllowedModsDoc { required_mods, allowed_mods, banned_mods : [string] }` — explicitly snake_case via `[YamlMember(Alias=…, ApplyNamingConventions=false)]` | yes |
| `registrations.yaml` | `RegistrationsDoc { registrations: { steamId → [characterName] } }` | no (auto-managed) |
| `violations.yaml` | `ViolationsDoc { violations: { steamId → { rule → count } } }` | no (auto-managed) |
| `metrics.yaml` | `DetectionMetrics` counters | no (auto-managed) |

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

Each policy failure increments a counter in `violations.yaml` keyed by `(steamId, rule)`. When any single rule's count crosses `violationThreshold` (default 3), `TryBan` calls `ZNet.Ban(steamId)`.

| Rule | Trigger |
|---|---|
| `CompanionMissing` | No manifest within `companionTimeoutSeconds`. |
| `HmacInvalid` | HMAC mismatch / parse failure / clock outside skew window. |
| `ChallengeMismatch` | Manifest's challenge doesn't match what the server issued. |
| `RequiredModMissing` | A `required_mods` entry is absent from the manifest. |
| `DisallowedMod` | A manifest mod isn't in `allowed_mods` (when `allowUnlisted: false`), or hash pin mismatch. |
| `BannedMod` | A `banned_mods` entry is present in the manifest. |
| `CharacterNameLimitExceeded` | Player exceeded `characterLimit`. Tracked separately in `Patch_RPC_PeerInfo`. |

Each violation also fires a Discord webhook event (if configured) with the rule and current count.

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
| [Plugin.cs](Plugin.cs) | Server entry point, all server logic, the two Harmony patches, all helpers |
| [Shared/Manifest.cs](Shared/Manifest.cs) | `ModManifest`, `ModManifestEntry`, canonical-string builder, HMAC helpers, `ConstantTimeEquals` |
| [ServerGuard.Client/ClientPlugin.cs](ServerGuard.Client/ClientPlugin.cs) | Client entry point, manifest builder, deferred init coroutine, first-run export, the one Harmony patch |
| [Valheim-ServerGuard.csproj](Valheim-ServerGuard.csproj) | Server build config; auto-detects Valheim install via `$VALHEIM_PATH` |
| [ServerGuard.Client/Valheim-ServerGuard-Client.csproj](ServerGuard.Client/Valheim-ServerGuard-Client.csproj) | Client build config; links `../Shared/Manifest.cs` |
| [BUILD.md](BUILD.md) | How to build both DLLs from source |
| [DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md) | Step-by-step Windows install walkthrough |
| [README.md](README.md) | User-facing documentation (quick setup + advanced reference) |

---

## Version

**1.3.0** — first release of the client-attestation architecture. The version is set independently in:
- `Plugin.cs` — `[BepInPlugin("com.taeguk.valheim.serverguard", "Valheim ServerGuard", "1.3.0")]`
- `ClientPlugin.cs` — `public const string VERSION = "1.3.0";`
- `Valheim-ServerGuard.csproj` — `<Version>1.3.0</Version>`
- `ServerGuard.Client/Valheim-ServerGuard-Client.csproj` — `<Version>1.3.0</Version>`

Bump all four together when releasing.
