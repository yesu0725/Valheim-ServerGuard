# Admin Commands

ServerGuard exposes a `sg` command family in the **in-game console (F5)**. Anyone can type the command, but the server only executes for SteamIDs listed in `admins.yaml`.

## Setup

Add your SteamID to `BepInEx/config/ServerGuard/conf/admins.yaml`:

```yaml
admins:
  - 76561198064360681
```

Save — the server hot-reloads. From now on, `sg` commands typed in your console reply only to you (other players never see the output).

## Usage

Press **F5** to open the console. Type:

```
sg help
```

You'll get back a list of every available command.

## Command reference

### General

| Command | What it does |
|---|---|
| `sg help` | Print this command list. |
| `sg status` | Quick health check — enforcement state, allowlist counts, modset fingerprint, peer count, violators on file. |
| `sg selftest` | Re-run the boot smoke tests. |
| `sg reload` | Reload `settings.yaml`, `admins.yaml`, `allowed_mods.yaml` immediately. |
| `sg modset` | Print the full + short modset fingerprints for sharing. |

### Players

| Command | What it does |
|---|---|
| `sg whois <steamid\|name>` | Show player info: registered character names, online status, admin status, per-rule violations. Accepts SteamID literal or character-name substring. |
| `sg violations [<n>]` | Top N players by violation count (default 10). |
| `sg pardon <steamid>` | Clear a player's recorded violation strikes. |
| `sg kick <steamid> [reason]` | Kick a connected player. The reason is shown to them on the disconnect screen. |

### Build / destroy heatmap queries

| Command | What it does |
|---|---|
| `sg build at <x> <z> [radius=50] [days=7]` | All build events near coords. |
| `sg build by <steamid\|name> [days=7]` | All build events by a player. |
| `sg build today [<n>]` | Last N build events today (default 10). |
| `sg destroyed at <x> <z> [radius] [days]` | Same as `build at` but only destroy events. |
| `sg destroyed by <name> [days]` | Same as `build by` but only destroy events. |
| `sg destroyed today [<n>]` | Last N destroys today. |
| `sg placed at \| by \| today …` | Mirrors `destroyed` but for placements only. |

For destroy queries, `<name>` can be a creature name like `Troll` or `Bonemass` — creature destructions are tracked the same way as player destructions.

## Examples

```
> sg status
[ServerGuard] v1.4.0  enforce=True  requireCompanion=True  requireHmac=True
  Allowlist  required=1  allowed=29  banned=0
  Modset     loose=8ce8906e  strict=19815033
  Online     3 peer(s)
  Violators  2 player(s) with strikes recorded

> sg whois Erik
  Erik (76561198064360681)
      DevcommandAttempt              2/3
      SpeedHack                      1/3 (informational)
      online=yes  admin=no

> sg destroyed by Troll 7
[ServerGuard] 1 destroy(s) by `Troll` in last 7d:
  14:21:42  DESTROY  Troll               wood_floor              (-1232.8, 565.2)

> sg destroyed at -1230 565 30
[ServerGuard] 3 destroy(s) within 30m of (-1230, 565) in last 7d:
  14:22:18  DESTROY  Loki                wood_wall_log           (-1230.5, 565.2)
  14:21:42  DESTROY  Troll               wood_floor              (-1232.8, 565.2)
  10:15:00  DESTROY                      stone_wall              (-1228.0, 567.5)
```

## Audit trail

Mutating commands (`reload`, `pardon`, `kick`) are posted to the admin Discord channel as `🛠 <admin> ran sg <command>`. Read-only queries (`status`, `whois`, `build …`) are not — they happen too often during investigations.

## Trust + security

Anyone can type `sg ...` in the console. The intercept always swallows the input so it's never broadcast as chat. The server validates `IsAdmin(steamId)` before executing — non-admins get a one-line "you are not an admin" reply.

The chat intercept also accepts `/sg ...` (with leading slash). Either form works.

## See also

- **[Configuration](Configuration)** — `admins.yaml` and related settings.
- **[Forensic Logs](Forensic-Logs)** — the underlying CSV the `sg build`/`destroyed`/`placed` commands query.
- **[Anti-Cheat Features](Anti-Cheat-Features)** — the rules `sg whois` and `sg violations` show.
