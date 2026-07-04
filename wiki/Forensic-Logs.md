# Forensic Logs

Two informational features that help admins investigate after the fact: the **death log** and the **build/destroy heatmap**.

## Death log

When a player dies in multiplayer, the companion sends a report to the server. The server posts a short message to Discord and resolves the killer.

### Example output (public channel)

```
💀 Erik (76561198064360681) died at [-1234, 567] — killed by a Skeleton
💀 Erik (76561198064360681) died at [-1234, 567] — killed by Loki (76561198000000000)
💀 Erik (76561198064360681) died at [-1234, 567] — burned to death
💀 Erik (76561198064360681) died at [-1234, 567] — fell to their death
💀 Erik (76561198064360681) died at [-1234, 567] — drowned or fell to a spirit
💀 Erik (76561198064360681) died at [-1234, 567] — took their own life
```

### Killer resolution

| Killer type | How it's resolved | Output |
|---|---|---|
| Another player (PvP) | Companion sends the killer's character name; server reverse-looks-up SteamID via `registrations.yaml` | `killed by **CharName** (SteamID)` (or `(another player)` if not registered yet) |
| Creature / NPC | Companion sends the localized hover name | `killed by a **Skeleton**` |
| Self | Companion detects `attacker == localPlayer` | `took their own life` |
| Environment | Companion sends the dominant damage type | `burned to death`, `fell to their death`, `drowned`, etc. |

### Admin player deaths

If the dead player is an admin, the message routes to the admin Discord channel instead of public. Players never see admins dying.

### Configuration

```yaml
enableDeathLog: true
```

That's the only knob. No violation rule attached — pure forensic logging.

## Build / destroy heatmap

Every piece placement and every piece destruction is appended to a daily CSV file at `BepInEx/config/ServerGuard/build_log/YYYY-MM-DD.csv`.

### CSV format

```csv
timestamp,action,steamId,charName,pieceName,x,y,z
2026-05-30T15:42:00Z,place,76561198000000000,"Erik",wood_wall_log,123.4,5.0,-456.7
2026-05-30T15:42:30Z,destroy,76561198999999999,"Loki",wood_wall_log,123.4,5.0,-456.7
2026-05-30T16:01:12Z,destroy,,"Troll",wood_pillar,123.4,4.5,-455.2
2026-05-30T16:05:00Z,destroy,,,wood_floor,150.0,2.0,-460.0
```

Columns:
- **timestamp** — UTC ISO-8601, sortable.
- **action** — `place` or `destroy`.
- **steamId** — destroyer's SteamID. Empty for creature destroys, decay, or unknown.
- **charName** — destroyer's character name (player) or creature name (Troll, Bonemass, …) or empty.
- **pieceName** — Valheim prefab name with `(Clone)` stripped.
- **x / y / z** — world coordinates, 1 decimal.

### Distinguishing player vs creature destroys

- **Player** → `steamId` non-empty, `charName` is their character name.
- **Creature** → `steamId` empty, `charName` is the creature name (e.g. `Troll`).
- **Unknown / decay** → both `steamId` and `charName` empty.

You can grep with `grep ',destroy,,' build_log/*.csv` to find non-player destroys, or use the `sg destroyed by <name>` console command.

### Where destroy events come from

| Destruction cause | Owner machine | Captured by |
|---|---|---|
| Player hammer-removes a nearby piece | Client (player) | Companion → RPC to server |
| Player attacks a nearby piece to death | Client (player) | Companion → RPC |
| Player attacks a distant piece | Server | Server-side patch |
| Creature smashes a nearby piece (raid) | Client (player) | Companion identifies creature, RPC includes its name |
| Creature smashes a distant piece | Server | Server-side patch + attacker name |
| Decay / fire / unattributed | Server | Server-side patch, empty attribution |

### Querying

Use the `sg` console commands:

```
sg build today [<n>]
sg build at <x> <z> [radius=50] [days=7]
sg build by <name> [days=7]
sg destroyed at|by|today …
sg placed at|by|today …
```

See **[Admin Commands](Admin-Commands)** for full reference.

You can also use any CSV tool: Excel, Python pandas, `grep`, etc. The files are plain UTF-8 CSV with quoted character names.

### Configuration

```yaml
enableBuildLog: true
buildLogRetentionDays: 30
```

A cleanup coroutine prunes files older than the retention each hour.

### Disk usage

Roughly 1 KB per 10 events. Even a busy server with thousands of events per day produces only a few hundred KB per file. The 30-day retention keeps total disk under ~10 MB for most servers.

## See also

- **[Discord Integration](Discord-Integration)** — death log public-channel format.
- **[Admin Commands](Admin-Commands)** — `sg build`/`destroyed`/`placed` query reference.
- **[Configuration](Configuration)** — `enableBuildLog`, `enableDeathLog`, retention.
