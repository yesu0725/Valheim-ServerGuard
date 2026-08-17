# SteamID Ban Layer

An independent SteamID denylist enforced inside the Valheim handshake, so a banned
player's connection is refused before they can spawn.

---

## Why vanilla banning is late

Valheim's ban list is applied in two places:

1. **`ZNet.IsAllowed(hostName, playerName)`** — called from `RPC_PeerInfo` before the
   peer is accepted. Checks `m_bannedList`. This path is immediate.
2. **`ZNet.UpdateBanList(dt)`** — a timer that fires when `m_banlistTimer > 5f`, then
   walks `m_bannedList` and calls `InternalKick(user)` for each entry.

The second path is the problem. `banlist.txt` is a `SyncedList` reloaded off disk on
a timer, so a ban added while the player is connected (or added by an admin seconds
before they reconnect) is only noticed on the next sweep. Up to five seconds of play
— enough to drop items, read chat, or reconnect in a loop.

## Where ServerGuard hooks

```
Steam socket accepted
   └── ZNet.OnNewConnection(peer)
         └── [Patch_OnNewConnection postfix]     ← LAYER 2
             peer.m_socket.GetHostName() is already the SteamID64
             banned -> Error(ErrorBanned) + Disconnect, return before
                       registering any handler or issuing a challenge

Client sends PeerInfo
   └── ZNet.RPC_PeerInfo(rpc, pkg)
         └── ZNet.IsAllowed(hostName, playerName)
               └── [Patch_ZNet_IsAllowed postfix]  ← LAYER 1 (primary)
                   __result = false
                   vanilla then: rpc.Invoke("Error", 8 /* ErrorBanned */); return
                   -> no character spawned, no ZDOs sent, nothing registered

Ban added while online (sg ban / bans.yaml edit)
   └── SweepBannedPeers()                          ← LAYER 3
       immediate TryKick of any matching connected peer
```

`ZSteamSocket.GetHostName()` returns `m_peerID.GetSteamID().ToString()` — the
SteamID64 — which is why layer 2 works before any application-level packet arrives.

Layer 1 is the primary gate because it reuses vanilla's own rejection path: the
client gets `ConnectionStatus.ErrorBanned` and shows Valheim's real "banned" screen.

### Ordering note

`Patch_ZNet_IsAllowed` is a **postfix**, not a prefix: vanilla's decision runs first
and we only ever flip an allow into a deny, never the reverse. A `whitelist`
(`permittedlist.txt`) server therefore keeps working unchanged.

---

## Storage

`BepInEx/config/ServerGuard/conf/bans.yaml`, hot-reloaded by `FileSystemWatcher`.

```yaml
bans:
  - id: "76561198000000000"
    reason: "Item duping"
    expires: ""                          # empty = permanent
    added: "2026-08-16T10:00:00.0000000Z"
    addedBy: "76561198000000001"
```

| Field | Notes |
|---|---|
| `id` | SteamID64, 17 digits. Decorated forms (`Steam_7656…`) are accepted on load — `ExtractSteamIdFromString` pulls the 17-digit run out. |
| `reason` | Shown to the player on refusal and in Discord. |
| `expires` | ISO-8601 UTC. Empty or unparseable = permanent (**fail closed**). Expired entries are ignored at match time and dropped on the next write. |
| `added`, `addedBy` | Audit only. |

`SaveBans()` writes the file by hand rather than through `_yamlOut` so the
explanatory header survives every rewrite.

### Fail-open on parse error

`LoadBans()` catches parse failures and **keeps the previously loaded list** rather
than clearing it or denying everyone. A malformed `bans.yaml` must not be able to
lock every player out. The failure is loud on the log and the admin channel.

Note the asymmetry with expiry, which fails *closed*: an unparseable `expires` keeps
the ban. Different questions — "is the file usable?" vs "is this ban still live?".

---

## Relationship to `banlist.txt`

`banLayerMirrorToVanilla: true` (default) also writes each ban into Valheim's own
list via `ZNet.Ban`, so the ban survives ServerGuard being removed.

The lists are **not** synchronised in the other direction:

- Valheim's in-game `unban` command clears `banlist.txt` only. It does not touch
  `bans.yaml`, so it cannot undo a ServerGuard ban. This is deliberate — it means a
  compromised admin account cannot quietly lift enforcement.
- `sg unban` clears `bans.yaml` only, and says so in its reply. Clearing the vanilla
  entry too requires the vanilla `unban` command.

---

## Admin commands

```
sg ban <steamid> [for <N>d|h|m] [reason]
sg unban <steamid>
sg bans [<n>]
```

- The target does not need to be online, or ever to have connected — a full 17-digit
  SteamID is banned pre-emptively. Names only resolve for players in
  `registrations.yaml`.
- Banning yourself, or anyone in `moderators.yaml`, is refused.
- `sg ban` calls `SweepBannedPeers()` so an online target is removed immediately.
- Durations: `7d`, `12h`, `30m`, `45s`; a bare number means days.

Auto-bans from `violationThreshold` route through the same layer: `TryBan` calls
`AddBan` (which mirrors to vanilla) and then sweeps. With `enableBanLayer: false`
it falls back to the pre-1.7 vanilla-only behaviour.

---

## Settings

| Key | Default | Effect |
|---|---|---|
| `enableBanLayer` | `true` | Master switch. When false, no gate runs and `TryBan` uses the vanilla list only. |
| `banLayerKickMessage` | `"You are banned from this server."` | Shown on refusal. |
| `banLayerMirrorToVanilla` | `true` | Also write to `banlist.txt`. |

## Metrics

`ban_layer_blocks` in `metrics.yaml` counts every refused connection attempt across
both gates. A high count against one SteamID is a reconnect loop worth noticing.
