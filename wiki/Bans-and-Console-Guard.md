# Bans and Console Guard

Two features added in **1.7.0**. Both are configured on the server; the console guard
is enforced by the companion plugin, so every player needs the 1.7.0 client.

---

# Instant SteamID bans

## The problem

Valheim's own ban system checks `banlist.txt` on a five-second timer. A banned player
therefore completes the connection, loads into the world, and gets up to five seconds
of play before the sweep removes them. That is enough time to drop items to an
accomplice, read chat, or start a reconnect loop.

## What ServerGuard does

It keeps a second list in `BepInEx/config/ServerGuard/conf/bans.yaml` and checks it at
the point in Valheim's connection handshake where the SteamID first becomes known —
before the player's character is created and before any world data is sent. A banned
player sees Valheim's normal "you are banned" screen and never enters the world.

## Settings

```yaml
enableBanLayer: true
banLayerKickMessage: "You are banned from this server."
banLayerMirrorToVanilla: true
```

| Setting | Default | Effect |
|---|---|---|
| `enableBanLayer` | `true` | Master switch. Off = Valheim's original behaviour. |
| `banLayerKickMessage` | *(see above)* | Text shown to a refused player, followed by the ban reason. |
| `banLayerMirrorToVanilla` | `true` | Also write each ban into Valheim's `banlist.txt`, so it still applies if you ever uninstall ServerGuard. |

## Managing bans

Use the [admin commands](Admin-Commands): `sg ban`, `sg unban`, `sg bans`.

You can also edit `bans.yaml` directly — it hot-reloads within a second, and anyone
online who matches a new entry is disconnected immediately.

```yaml
bans:
  - id: "76561198000000000"
    reason: "Item duping"
    expires: ""                    # empty = permanent
    added: "2026-08-16T10:00:00.0000000Z"
    addedBy: "76561198000000001"
```

`expires` takes an ISO-8601 UTC timestamp. Leave it empty for a permanent ban. If you
write something the server can't parse, the ban is treated as permanent rather than
being quietly dropped.

If the whole file fails to parse, the server keeps using the last list it loaded
successfully rather than letting everyone in — the error is logged and posted to your
admin Discord channel.

## Auto-bans

Players who cross `violationThreshold` are added to this list automatically, so a
repeat offender is refused instantly on their next attempt rather than getting in and
being swept out.

## One-way by design

| Action | Clears `bans.yaml` | Clears `banlist.txt` |
|---|---|---|
| `sg unban` | Yes | No |
| in-game `unban` | **No** | Yes |

Valheim's `unban` command cannot lift a ServerGuard ban. That's deliberate: it means a
compromised or careless admin account can't quietly undo your enforcement.

---

# Console guard

## What it covers

The F5 developer console. ServerGuard's companion plugin decides, per command, whether
it is allowed to run — using a policy your server sends on connect.

Valheim's own position is that cheat commands "do not work on a dedicated server". That
is true of an unmodified client, but the switch that enforces it lives *on the client*.
A patched client turns it on, and many of those commands then work for real, because
each client has authority over the objects near it. A client-side `spawn` produces a
genuine item on your server.

The guard is meaningful because of what sits in front of it: `requireCompanion` kicks
clients that can't produce a signed mod manifest, and the companion's own file hash is
checked against your allowlist. A player who removes or edits the enforcer loses their
connection. Treat it as one layer among several, not as the whole defence.

## Modes

```yaml
consoleGuardMode: restricted
consoleGuardExemptModerators: true
```

| Mode | Effect |
|---|---|
| `open` | No gating. Vanilla behaviour. |
| `restricted` **(default)** | Blocks cheat commands, anything Valheim flags as a cheat (including commands added by other mods), and a curated list of non-cheat commands that still change shared world state. |
| `whitelist` | Blocks every command except the ones you list in `consoleAllowedCommands`, plus a safe core (chat, emotes, `help`, `sg`, display settings). |
| `disabled` | The console cannot be opened at all — F5 and the gamepad shortcut both do nothing. Chat still works. |

Exemptions follow the [privilege tiers](Privilege-Tiers):

| Tier | Console guard |
|---|---|
| Owner (`owners.yaml`) | **Always exempt.** No setting changes this. |
| Moderator (`moderators.yaml`) | Exempt when `consoleGuardExemptModerators: true` (default). |
| Player | Subject to the policy. |

Keep the moderator exemption on if you use `disabled` mode, because `sg` commands are
typed into that same console.

### What `restricted` blocks beyond cheats

Commands that don't need `devcommands` but still affect everyone:

| Command | Why |
|---|---|
| `bind`, `unbind`, `resetbinds`, `printbinds` | See the key-bind section below. |
| `nomap`, `noportals` | Toggle a **global key** — changes the rule for the entire world, not the caller. |
| `setworldmodifier`, `setworldpreset`, `resetworldkeys` | Change combat difficulty, resource rates, raid frequency and portal rules world-wide. |
| `resetsharedmap` | Wipes shared cartography-table map data the whole group contributed to. |
| `optterrain` | Rewrites every old terrain modification in the loaded area — a heavy sync push to the server. |
| `printseeds` | Prints dungeon seeds *and positions*. |
| `resetknownitems`, `resetplayerprefs`, `resetspawn` | Silent local data loss, especially if bound to a key. |
| `cr`, `restartparty` | Spammable stalls. |

Valheim's admin-only commands (`ban`, `unban`, `kick`, `banned`, `save`) are
deliberately **not** blocked: the server already refuses them for non-admins, so
blocking them client-side would add nothing while breaking real moderation. Add them to
`consoleBlockedCommands` yourself if your admin list is broader than you'd like.

### Extending the lists

```yaml
consoleBlockedCommands:      # added to the built-in list (mode: restricted)
  - somemodcommand

consoleAllowedCommands:      # the permitted set (mode: whitelist)
  - help
  - ping
```

## Key binds

```yaml
consoleGuardBindPolicy: purge
```

This is the part most worth understanding.

`bind <key> <command>` attaches any command — including `devcommands` — to a key.
Two details make it more dangerous than it looks:

1. **Binds fire without the console.** Valheim runs them from the chat update loop, not
   the console. Locking the console shut does nothing to them.
2. **Binds skip Valheim's own permission check.** A bind runs commands that would be
   refused with *"is not valid in the current context"* if the player typed them.

And they persist. A player can set a bind offline, close the game, and arrive on your
server the next day with it still armed.

| Policy | Effect |
|---|---|
| `allow` | Binds untouched. |
| `block` | The `bind` command is refused, but binds already loaded still fire. |
| `purge` **(default)** | Binds are cleared while the player is on your server, and `bind` is refused. Their saved binds are left alone and return in single-player. |
| `wipe` | As `purge`, and the player's saved bind list is deleted permanently. |

`purge` is the default because it closes the hole without destroying someone's local
setup — a player who binds `cheer` to a mouse button for roleplay keeps it offline.
Players get a console message when their binds are removed, so it isn't mysterious.

Everything is restored the moment they disconnect.

## Reporting

```yaml
consoleGuardReportAttempts: true
```

| What was blocked | Where it's reported | Counts toward auto-ban? |
|---|---|---|
| A cheat command | Public + admin Discord | Yes (`DevcommandAttempt`, default on) |
| A restricted command, a bind, or anything under whitelist mode | Admin Discord only | No (`ConsoleCommandBlocked`, default off) |

The split matters: someone typing `bind` out of curiosity shouldn't be announced to
your whole community as a cheater. Change either default under `countAsViolation` in
`settings.yaml`.

Set `consoleGuardReportAttempts: false` to keep the blocking but stop the reporting.
