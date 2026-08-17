# Architecture

## Plugin topology

```
[Valheim Dedicated Server]                 [Player's Valheim Client]
  BepInEx/plugins/                           BepInEx/plugins/
    Valheim-ServerGuard.dll                    Valheim-ServerGuard-Client.dll
    (Plugin.cs — server)                       (ClientPlugin.cs — client)
         |                                              |
         └──────── ZNet RPC ──────────────────────────┘
                 (named string RPCs over ZRpc)
```

Both compile `Shared/Manifest.cs` as a linked source file (not a separate DLL).

---

## BepInEx lifecycle

### Server (`Plugin.cs`)

```
Awake()
  EnsureFoldersAndFiles()     ← create defaults; migrates admins.yaml → moderators.yaml
  LoadSettings()
  TopUpSettingsFile()         ← append options missing from an older settings.yaml
  LoadOwners()
  LoadAdmins()                ← reads moderators.yaml
  LoadBans()
  LoadAllowedMods()
  LoadRegistrations()
  LoadViolations()
  LoadMetrics()
  StartWatchers()             ← FileSystemWatcher hot-reload
  _harmony.PatchAll()
  ReconfigureDiscordAndSummary()
  StartCoroutine(SpeedCheckLoop())
  StartCoroutine(BuildLogCleanupLoop())
  StartCoroutine(PingLogLoop())
  RunSelfTest()
  PostAdminEvent(":rocket: ServerGuard online ...")
  _bootCompleted = true       ← NOW hot-reload notices reach Discord
```

### Client (`ClientPlugin.cs`)

```
Awake()
  EnsureConfig()             ← read/write client.yaml
  _harmony.PatchAll()
  StartCoroutine(DeferredInit())

DeferredInit()               ← runs 2s after Awake (lets all plugins load)
  BuildManifestCache()       ← scan Chainloader.PluginInfos + SHA-256 each DLL
  ExportAllowedModsSnippet() ← write mods_for_allowed_mods.yaml on first run
  StartCoroutine(SkillReportLoop())
  LogInfo("Modset fingerprint loose=... strict=...")
```

The 2-second delay in `DeferredInit` is intentional — `PluginInfos` is incomplete during `Awake` because BepInEx loads plugins alphabetically on the same thread.

---

## ZNet connection flow

```
Server Patch_ZNet_IsAllowed (Postfix on the private ZNet.IsAllowed)
  Called from RPC_PeerInfo BEFORE the peer is accepted. A banned SteamID sets
  __result = false; vanilla then sends ConnectionStatus.ErrorBanned and returns.
  See claude/ban-layer.md.

Server Patch_OnNewConnection (Postfix on ZNet.OnNewConnection)
  0. Ban check on the socket host name (SteamID64 on Steam) — disconnect and
     return before registering anything
  1. Register ALL RPC handlers for this peer
  2. If owner/moderator → PostPlayerEvent(":crown:"/":shield:", pid, "joined as …"); return
  3. Issue challenge → peer.m_rpc.Invoke("ServerGuard_RequestManifest", challenge)
  4. Start AttestationTimeoutCoroutine (kicks if no reply in companionTimeoutSeconds)

Client Patch_RegisterClientHandler (Postfix on ZNet.OnNewConnection)
  Stash server ZRpc as _serverRpc
  Register ServerGuard_RequestManifest handler
  Register ServerGuard_AdminCommandReply handler

Client (on receiving ServerGuard_RequestManifest)
  BuildManifestCache() — rebuild from live PluginInfos
  Sign manifest with HMAC-SHA256 using sharedSecret
  peer.m_rpc.Invoke("ServerGuard_Manifest", json)

Server (on receiving ServerGuard_Manifest)
  Verify challenge + timestamp + HMAC
  ValidateAgainstPolicy(manifest)
  If all pass → PostPlayerEvent(":white_check_mark:", steamId, "joined")
  If any fail → TryKick(peer, FriendlyReason(rule, detail))
```

---

## In-memory state (server)

| Field | Type | Purpose |
|---|---|---|
| `_settings` | `Settings` | Parsed from `settings.yaml` |
| `_admins` | `HashSet<string>` | Moderator SteamIDs from `moderators.yaml` |
| `_owners` | `HashSet<string>` | Owner SteamIDs from `owners.yaml`. `IsAdmin` = moderator ∪ owner |
| `_bans` | `Dictionary<string, BanEntry>` | SteamIDs from `bans.yaml`. Replaced wholesale on reload (lock-free read path) |
| `_requiredMods` | `List<AllowedModEntry>` | From `required_mods:` in `allowed_mods.yaml` |
| `_allowedMods` | `List<AllowedModEntry>` | From `allowed_mods:` |
| `_bannedMods` | `List<AllowedModEntry>` | From `banned_mods:` |
| `_pending` | `Dictionary<long, PendingAttestation>` | Per-peer challenge state |
| `_registrations` | `Dictionary<string, List<string>>` | SteamID → char names |
| `_violations` | `Dictionary<string, Dictionary<string, int>>` | SteamID → rule → count |
| `_speedState` | `Dictionary<long, SpeedState>` | Per-peer speed tracking (by peer.m_uid) |
| `_pingState` | `Dictionary<long, PingState>` | Per-peer ping samples |
| `_suppressLogoutFor` | `HashSet<long>` | Peer UIDs we just kicked (suppress redundant "left") |
| `_skillOverflowState` | `Dictionary<long, ...>` | Per-peer skill overflow throttle |

---

## Config file layout (runtime)

```
BepInEx/config/ServerGuard/
├── README.md                       ← operator quick-start (written on first run)
├── conf/
│   ├── settings.yaml               ← main settings (hot-reload)
│   ├── moderators.yaml             ← MODERATOR SteamIDs (hot-reload; was admins.yaml)
│   ├── owners.yaml                 ← OWNER SteamIDs — exempt from every rule (hot-reload)
│   ├── bans.yaml                   ← SteamID denylist (hot-reload)
│   ├── allowed_mods.yaml           ← mod allowlist (hot-reload)
│   ├── registrations.yaml          ← SteamID → char name map (auto-saved)
│   ├── violations.yaml             ← per-player violation counts (auto-saved)
│   ├── metrics.yaml                ← detection counters (auto-saved)
│   └── modset_fingerprint.txt      ← computed on every allowed_mods reload
└── build_log/
    └── YYYY-MM-DD.csv              ← daily build/destroy log
```

Client:
```
BepInEx/config/ServerGuard/
├── client.yaml                     ← sharedSecret
└── mods_for_allowed_mods.yaml      ← first-run export snippet
```

---

## Key Valheim APIs used

| API | Used for |
|---|---|
| `ZNet.instance.GetPeers()` | Iterate connected peers |
| `ZNetPeer.m_rpc` | Register/invoke RPCs per peer |
| `ZNetPeer.m_uid` | Stable per-connection ID (long) |
| `ZNetPeer.m_characterID` | `ZDOID` of the peer's character |
| `ZDOMan.instance.GetZDO(ZDOID)` | Read character position for speed check |
| `ZNet.instance.Disconnect(peer)` | Kick a peer |
| `ZNet.instance.IsServer()` | Guard: only run server logic on the server |
| `WearNTear.Damage(HitData)` | Track last attacker before destroy |
| `WearNTear.Destroy()` | Log piece destruction |
| `Player.PlacePiece(Piece, Vector3, ...)` | Log piece placement |
| `Player.OnDeath` (protected) | Send death report |
| `Chainloader.PluginInfos` | Build manifest list on client |
| `BepInEx.Logging.Logger.Listeners` | Attach verbose Discord mirror |
