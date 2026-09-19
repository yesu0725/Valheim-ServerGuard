# Architecture

## Plugin topology

```
[Valheim Dedicated Server]                 [Player's Valheim Client]
  BepInEx/plugins/                           BepInEx/plugins/
    Valheim-ServerGuard.dll                    Valheim-ServerGuard.dll   (the SAME file)
    ServerGuardPlugin.Awake                    ServerGuardPlugin.Awake
      headless → AddComponent<ServerPlugin>      GUI → AddComponent<ClientPlugin>
         |                                              |
         └──────── ZNet RPC ──────────────────────────┘
                 (named string RPCs over ZRpc)
```

Since 2.0 there is one assembly and one `[BepInPlugin]` (`ServerGuardPlugin`, GUID
`com.taeguk.valheim.serverguard`). `ServerPlugin` and `ClientPlugin` are plain
`MonoBehaviour`s; the entry plugin attaches exactly one of them to its own GameObject
(`Chainloader.ManagerObject`, `DontDestroyOnLoad`), so their `Awake` runs immediately
inside the entry plugin's `Awake` and `StartCoroutine` / `OnDestroy` behave as they
did when each was its own plugin.

### Side selection

```
mode = BepInEx cfg  General.Mode  (auto | server | client; default auto)
headless = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
runServer = mode == "server" || (mode != "client" && headless)
```

A dedicated server runs `-batchmode -nographics` and has no graphics device; a
player's game always has one, *including* when hosting a listen server — so a host
gets the client half, exactly as before the merge (the server half has always been
for dedicated servers only). The two halves never coexist in one process.

### Selective patching

`Harmony.PatchAll()` is never called. Each half creates its own `Harmony` instance
(`<GUID>.server` / `<GUID>.client`) and hands it to `ServerGuardPlugin.PatchNested`,
which walks the nested types of that half (any depth) and runs
`CreateClassProcessor(t).Patch()` on every type carrying a `HarmonyAttribute`.
`Harmony.PatchAll(Type)` would not do — it processes only that one type and ignores
nested classes. Consequence: **every patch class must be nested inside `ServerPlugin`
or `ClientPlugin`**; a top-level one is applied by nobody. The server logs
`Applied N server-side Harmony patch class(es)` (14 as of 2.0.0); the client logs the
same for its side.

---

## BepInEx lifecycle

### Entry (`ServerGuardPlugin.cs`)

```
Awake()
  Instance / Log = Logger
  Config.Bind("General", "Mode", "auto")
  decide side (above)
  gameObject.AddComponent<ServerPlugin>()  or  <ClientPlugin>()   ← their Awake runs now
```

### Server (`ServerPlugin.cs`)

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
  PatchNested(_harmony, typeof(ServerPlugin))
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
  PatchNested(_harmony, typeof(ClientPlugin))
  StartCoroutine(DeferredInit())

DeferredInit()               ← runs 2s after Awake (lets all plugins load)
  BuildManifestCache()       ← scan Chainloader.PluginInfos + SHA-256 each DLL
  ExportAllowedModsSnippet() ← write mods_for_allowed_mods.yaml on first run
  StartCoroutine(SkillReportLoop())
  LogInfo("Modset fingerprint loose=... strict=...")
```

The 2-second delay in `DeferredInit` is intentional — `PluginInfos` is incomplete during `Awake` because BepInEx loads plugins alphabetically on the same thread.

#### Quick Login panel (title screen)

Separate from the lifecycle above — it is driven by a Harmony postfix, not `Awake`:

```
FejdStartup.SetupGui  [postfix]
  BuildQuickLoginPanel(menu)          ← only if quickLoginEnabled && serverAddress set
    parent = m_characterSelectScreen.parent   (persists across menu ↔ char-select)
    logo → name → description                  (top-anchored, flow downward)
    [Announcements header + BuildAnnouncementsScrollBox]   ← 1.8.0, only if text non-empty
    player count                               (bottom-anchored when announcements present)
    AddConnectButton  ← cloned vanilla menu button, for theme/font/sfx
    StartCoroutine(RefreshPlayerCount)        ← A2S_INFO on gamePort+1, background thread
```

Every label is a clone of a vanilla menu-button `TextMeshProUGUI`, configured **by
reflection** (`SetTmpProperty` / `SetTmpEnum` / `GetTmpProperty`) — the project
deliberately does not reference `Unity.TextMeshPro`. The announcements box is a
hand-built `ScrollRect` → viewport (`RectMask2D`) → content → text hierarchy; its
content height is measured explicitly one frame later in `FitAnnouncementContent`
rather than by `ContentSizeFitter` (see `known-errors.md`, ERROR 14 for why).

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
  2. If OWNER → PostPlayerEvent(":crown:", pid, "joined as owner"); return
     (moderators attest like players since 2.0; their ":shield: joined as moderator" fires from OnManifestReceived)
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
├── client.yaml                     ← sharedSecret + Quick Login panel settings
│                                     (incl. serverAnnouncements block, 1.8.0)
├── <logo>.png / .jpg               ← optional; named by serverLogoPath
└── mods_for_allowed_mods.yaml      ← first-run export snippet
```

`client.yaml` is read once at `Awake` and is **not** hot-reloaded. It is only written
when missing, plus a single append-migration for `serverAnnouncements` — see
`settings-reference.md`, *Client config*.

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
| `Player.PlacePiece(Piece, Vector3, Quaternion, bool, bool)` | Log piece placement. Valheim 1.0 added the trailing `bool cheated`; the postfix binds `piece`/`pos` **by name**, so it still resolves. |
| `Player.OnDeath` (protected) | Send death report |
| `Chainloader.PluginInfos` | Build manifest list on client (reports the one merged GUID) |
| `SystemInfo.graphicsDeviceType` | Entry plugin: headless ⇒ dedicated server ⇒ server half |
| `Terminal.IsCheatsEnabled`, `Terminal.ConsoleCommand.IsValid`, `Terminal.m_cheat` (public static) | Client: staff dev-command unlock — see `console-guard.md` |
| `ZNet.RPC_RemoteCommand`, `ZNet.ListContainsId`, `ZNet.m_adminList` (reflection), `ZNet.RemotePrint` | Server: staff dev-command authorisation without `adminlist.txt` |
| `RandEventSystem.RPC_ConsoleStartRandomEvent` / `RPC_ConsoleResetRandomEvent`, `StartRandomEvent`, `ResetRandomEvent` | Server: `randomevent` / `stopevent` for moderators on the list |
| `BepInEx.Logging.Logger.Listeners` | Attach verbose Discord mirror |
| `FejdStartup.SetupGui` (postfix) | Build the Quick Login panel once the vanilla menu exists |
| `FejdStartup.m_characterSelectScreen`, `m_menuButtons`, `m_versionLabel` (reflection) | Parent + font/button templates for the panel |
| `FejdStartup.m_queuedJoinServer`, `SetServerToJoin`, static `ServerPassword` (reflection) | Direct connect, skipping the IP/password dialogs |
| `TMPro.TMP_TextUtilities.FindIntersectingLink` (reflection) | Which `<link>` was clicked in the announcements box |
| `UnityEngine.ImageConversion.LoadImage` (reflection) | Decode the server logo PNG/JPG |

Every string-named member above is verified against a new game build by the
procedure in `build-and-release.md`, *Verifying against a new Valheim release*.
Last verified: Valheim 1.0.7 (2026-09-09), all present.
