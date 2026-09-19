# RPC Protocol

All server↔client communication uses Valheim's `ZRpc` named-RPC system: `peer.m_rpc.Register<T>(name, handler)` and `peer.m_rpc.Invoke(name, args)`.

---

## Critical registration rule

**ALL `peer.m_rpc.Register(...)` calls in `Patch_OnNewConnection` MUST come before the admin early-return.**

```csharp
// CORRECT order:
Postfix(ZNetPeer peer) {
    peer.m_rpc.Register<string>("ServerGuard_Manifest", ...);
    peer.m_rpc.Register<string>("ServerGuard_DevcommandAttempt", ...);
    // ... all other Register calls ...
    peer.m_rpc.Register<string>("ServerGuard_AdminCommand", ...);

    if (IsAdmin(pid)) {
        // admin handling...
        return;   // ← return AFTER registering
    }
    // ... attestation challenge for non-admins
}
```

If an admin connects and `Register` hasn't run yet, their companion's RPCs arrive at a peer with no listener and are silently dropped. Admin `sg` commands stop working.

---

## Server → Client RPCs (server invokes, client receives)

### `ServerGuard_RequestManifest`
- **Sender:** `Patch_OnNewConnection` (server)
- **Payload:** `string challenge` — random hex nonce (32 chars)
- **Client handler:** `Patch_RegisterClientHandler` receives it, calls `ClientPlugin.BuildManifestJson(challenge)`, invokes `ServerGuard_Manifest` back

---

### `ServerGuard_AdminCommandReply`
- **Sender:** `OnAdminCommandReceived` (server, after dispatching command)
- **Payload:** `string reply` — newline-separated response lines
- **Client handler:** `DisplayAdminReply(text)` — splits on `\n`, prints each line to F5 console

---

### `ServerGuard_ArrivalShout`
- **Sender:** `SendArrivalShoutPolicy(peer)` — from `Patch_OnNewConnection` (before the admin early-return) and from `BroadcastArrivalShoutPolicy()` on every settings.yaml hot-reload
- **Payload:** `string` — `"1"` = vanilla first-spawn shout allowed, `"0"` = swallow it
- **Client handler:** `ClientPlugin.OnArrivalShoutPolicyReceived(payload)` — sets `_arrivalShoutAllowed`, which the `Chat.SendText` prefix consults
- **Default when never sent:** allowed (older/unconfigured servers keep vanilla behaviour)

---

### `ServerGuard_ConsolePolicy`
- **Sender:** `SendConsolePolicy(peer)` — from `Patch_OnNewConnection` (before the admin early-return) and from `BroadcastConsolePolicy()` on every settings.yaml, admins.yaml *and* owners.yaml hot-reload
- **Payload:** `string` — 9 pipe-separated fields (6 before 2.0)
  ```
  mode|exempt|role|bindPolicy|blockedCsv|allowedCsv|devMode|devCsv|taint
  ```
  - `mode`: `open` / `restricted` / `whitelist` / `disabled`
  - `exempt`: `"1"` / `"0"` — resolved **server-side** (owner always; moderator when `consoleGuardExemptModerators`), so the client never models the tiers
  - `role`: `owner` / `moderator` / `player` — carried for the client log line and for `IsOwnerClient` (the client-only animation-cancel gate)
  - `bindPolicy`: `allow` / `block` / `purge` / `wipe`
  - `blockedCsv`, `allowedCsv`: comma-separated lowercase command names, may be empty
  - `devMode` *(2.0)*: `all` (owner, `enableOwnerDevcommands`) / `list` (moderator, `enableModeratorDevcommands` and a non-empty list) / `none`. Resolved server-side by `DevcommandModeFor(pid)`.
  - `devCsv` *(2.0)*: the sorted `moderatorDevcommands` set when `devMode == list`, else empty (reserved commands already removed)
  - `taint` *(2.0)*: `0` when `enableCheatTaintDetection` is off, else the normalised `cheatTaintPolicy` (`log`/`strip`/`violation`). Drives the client's one-per-launch notice panel (`ShowCheatTaintNotice`) and, with `role == moderator`, the per-login chat welcome (`ShowModeratorWelcome`) — both from `QueueLoginMessages`, which waits for spawn.
- **Client handler:** `ClientPlugin.OnConsolePolicyReceived(payload)` — sets the static policy fields, then calls `ApplyBindPolicy()` and `AnnounceDevGrant()` (one console line when the grant changes). A 6-field payload from a pre-2.0 server yields `devMode = none`.
- **Default when never sent:** `restricted` mode, `allow` bind policy (pre-1.7 behaviour, so an older server is unaffected)
- **Reset:** `Patch_ZNet_Shutdown_ResetPolicy` restores defaults on disconnect, so leaving a `disabled`-mode server doesn't leave the local console dead in single-player


### `ServerGuard_StripCheated` *(2.0)*
- **Sender:** `OnCheatStateReceived` when `cheatTaintPolicy` is `strip` or `violation`, on every report while flagged items remain
- **Payload:** `string` — comma-separated prefab names to **keep** (`cheatTaintIgnoredItems`)
- **Client handler:** `OnStripCheatedReceived` — removes every `m_cheated` item not in the list from the local inventory immediately, shows a centre message, then force-sends a fresh `ServerGuard_CheatState`
---

## Client → Server RPCs (client invokes, server receives)

All registered in `Patch_OnNewConnection` on the server side.

### `ServerGuard_Manifest`
- **Sender:** Client, in response to `ServerGuard_RequestManifest`
- **Payload:** JSON string — serialized `ModManifest`
  ```json
  {
    "SchemaVersion": "1",
    "Challenge": "<nonce>",
    "TimestampUtc": 1234567890,
    "Mods": [{"Guid":"...", "Name":"...", "Version":"...", "Sha256":"..."}, ...],
    "Hmac": "<base64>"
  }
  ```
- **Server handler:** `OnManifestReceived(peer, json)` — verifies HMAC, validates against policy

---

### `ServerGuard_DevcommandAttempt`
- **Sender:** `Patch_TryRunCommand` on client when a blocked command is typed (or fired by a key bind)
- **Payload:** `string` — `"<command>|<category>"`, category ∈ `cheat` / `risky` / `bind` / `notallowed` / `moderator`
- **Server handler:** `OnDevcommandAttemptReceived(peer, command)`
  - `moderator` *(2.0)* → a moderator asked for a dev command outside `moderatorDevcommands`: admin channel post, **no violation**, no metric. Checked before the `IsAdmin` bypass.
  - `cheat` → public Discord post + `DevcommandAttempt` violation
  - everything else → admin channel only + `ConsoleCommandBlocked` violation
- **Back-compat:** companions ≤1.6.3 send a bare command name with no `|`; the server treats a missing category as `cheat`, preserving the old behaviour

---

### Vanilla RPCs intercepted for staff dev commands (2.0)

Not ServerGuard RPCs — vanilla ones the server half patches. See `console-guard.md`, *Staff dev commands*.

| Vanilla RPC | Sent by | ServerGuard patch | Behaviour for staff |
|---|---|---|---|
| `RPC_RemoteCommand` (ZRpc, string) | `Terminal.TryRunCommand` when a `RemoteCommand` command is invalid locally; also the `devcommands` handler every time it toggles | `Patch_ZNet_RPC_RemoteCommand` (prefix) | `TryHandleStaffRemoteCommand`: granted → run on the server console with `Terminal.m_cheat` forced on for the call, log + admin post, `RemotePrint` an ack; `devcommands` → ack only; not granted → vanilla (`adminlist.txt`) |
| `startrandomevent` / `resetrandomevent` (routed) | `RandEventSystem.ConsoleStart/ResetRandomEvent` from `randomevent` / `stopevent` | `Patch_RandEvent_ConsoleStart` / `_ConsoleReset` (prefix) | granted → `StartRandomEvent()` / `ResetRandomEvent()`; else vanilla `IsAdmin` check |
| `RemotePrint` (server → client) | `ZNet.RemotePrint` | — | Used for the acks above; prints into the player's console |

---

### `ServerGuard_CheatState` *(2.0)*
- **Sender:** `SendCheatStateIfDue` from `CheatStateLoop` — 15 s after spawn, then every 10 s poll: sends when the payload changed or 60 s passed; forced after a strip
- **Payload:** `string` — `usedCheats|bypass|count|prefab:stack,prefab:stack,...`
  - `usedCheats`: `PlayerProfile.m_usedCheats` (character has ever run an `IsCheat` command)
  - `bypass`: `PlayerProfile.s_bypassCheatChecks` (the `bypasscheatchecks` unique key)
  - items: every inventory `ItemData` with `m_cheated`, keyed by `m_dropPrefab.name` (`:`/`,`/`|` replaced), sorted
- **Server handler:** `OnCheatStateReceived(peer, payload)` — see `features-and-rules.md`, *CheatedItem*
- **Back-compat:** a pre-2.0 server has no handler; the RPC is dropped silently

### `ServerGuard_AnimationCancelAttempt`
- **Sender:** `Patch_Player_StartEmote_BlockDuringAttack`
- **Payload:** `string source` — `"emote"`
- **Server handler:** `OnAnimationCancelReceived(peer, source)` — drops any source in `_animationCancelIgnoredSources` (currently `"sheathe"`, still sent by companions from 1.6.1 and earlier)

---

### `ServerGuard_SkillReport`
- **Sender:** `SkillReportLoop()` every 60 seconds (after 15s initial delay)
- **Payload:** `string payload` — pipe-separated `skill:level` pairs
  ```
  Swords:85.2|Bows:73.0|Run:100.0|...
  ```
- **Server handler:** `OnSkillReportReceived(peer, payload)`

---

### `ServerGuard_PlayerDeath`
- **Sender:** `Patch_Player_OnDeath_Report` Prefix
- **Payload:** `string payload` — 6 pipe-separated fields, invariant-culture floats
  ```
  posX|posY|posZ|attackerKind|attackerLabel|causeHint
  ```
  - `attackerKind`: `"player"`, `"creature"`, `"self"`, `"environment"`
  - `attackerLabel`: player char name, creature hover name, or empty
  - `causeHint`: dominant damage type (`"Fire"`, `"Blunt"`, `"Fall"`, …) or empty
- **Server handler:** `OnPlayerDeathReceived(peer, payload)`

---

### `ServerGuard_BuildPlace`
- **Sender:** `Patch_PlacePiece_Report` Postfix
- **Payload:** `string payload` — 4 pipe-separated fields
  ```
  pieceName|posX|posY|posZ
  ```
  - `pieceName`: prefab name with `(Clone)` stripped, max 64 chars
  - Positions: invariant-culture 1-decimal floats
- **Server handler:** `OnBuildPlaceReceived(peer, payload)` → writes CSV row

- **2.0:** optional 5th field `cheated` (`1`/`0`) — Valheim 1.0's `PlacePiece` argument, read from `__args` so the patch still binds without it. Server logs it to the CSV and queues `CheatedBuild` verification against the piece ZDO.
---

### `ServerGuard_BuildDestroy`
- **Sender:** `Patch_WearNTear_Destroy_ClientReport` Prefix
- **Payload:** `string payload` — 6 pipe-separated fields
  ```
  pieceName|posX|posY|posZ|attackerKind|attackerLabel
  ```
  - `attackerKind`: `"self"` (local player hammer/weapon), `"player"` (another player), `"creature"` (mob), `"unknown"` (unattributed)
  - `attackerLabel`: creature hover name or other-player char name, or empty for `"self"` (server fills from RPC sender)
- **Server handler:** `OnBuildDestroyReceived(peer, payload)` → writes CSV row

---

### `ServerGuard_AdminCommand`
- **Sender:** `Patch_TryRunCommand` when player types `sg ...` in F5 console
- **Payload:** `string command` — everything after `sg ` (e.g. `"whois 76561198000000000"`)
- **Server handler:** `OnAdminCommandReceived(peer, command)`
  - Validates `IsAdmin(pid)` first
  - Dispatches to `DispatchAdminCommand(args, peer, pid)`
  - Replies via `ServerGuard_AdminCommandReply`

---

## Payload sanitation

Any string that travels over RPC and ends up in Discord or a CSV must be sanitized. The `SanitiseShort(string s, int max)` helper in `ClientPlugin.cs`:
```csharp
var v = (s ?? "").Replace('|', ' ').Replace('\n', ' ').Trim();
if (v.Length > max) v = v.Substring(0, max);
return v;
```

The pipe `|` is the field delimiter. Newlines would corrupt Discord embeds. Always sanitize before embedding in payloads.

---

## Payload parsing (server side)

All payloads are split by `|` with `payload.Split('|')`. Fields are addressed by index. Always guard against short arrays:
```csharp
var parts = payload?.Split('|');
if (parts == null || parts.Length < 4) { LogS.LogWarning("..."); return; }
var pieceName = parts[0];
if (!float.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out float posX)) return;
```
