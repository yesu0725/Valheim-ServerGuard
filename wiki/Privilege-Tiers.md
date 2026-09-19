# Privilege Tiers

Added in **1.7.0**. Three tiers, two config files. Since **2.0.0** the tiers also decide who gets dev commands.

| Tier | File | Who | Dev commands (2.0) |
|---|---|---|---|
| **Owner** | `conf/owners.yaml` | You. Normally exactly one SteamID. | All of them. |
| **Moderator** | `conf/moderators.yaml` | Your staff. | The `moderatorDevcommands` list. |
| **Player** | — | Everyone else. | None. |

---

## Owner

An owner is exempt from **every** rule in this mod, unconditionally. There is no
setting that makes a rule apply to an owner.

Specifically, an owner:

- is never kicked or banned by ServerGuard — and an entry in `bans.yaml` matching an
  owner is **ignored**, so a stale auto-ban or a typo can't lock you out of your own
  server
- never accrues violation strikes, so can never reach the auto-ban threshold
- skips the mod-manifest attestation entirely
- is never speed-checked, skill-capped, or animation-cancel checked
- is never subject to the character limit or to cheat-item removal
- has full console access regardless of `consoleGuardMode`, and keeps their key binds
  regardless of `consoleGuardBindPolicy`
- is exempt from forced map positions
- has full `sg` command access
- can use **every dev command** on the server (`enableOwnerDevcommands`, default on) — see below

```yaml
# conf/owners.yaml
owners:
  - "76561198000000000"
```

Owners do **not** need to be listed in `moderators.yaml` as well — the owner tier already
includes everything moderators can do.

> Keep this list as short as it can possibly be. Anyone in it is invisible to every
> check the mod performs. If a moderator needs to be un-bannable, that's a reason to
> trust them less, not to promote them.

If `owners.yaml` can't be parsed, the owner list is treated as **empty** and an error
is logged. An unreadable file must not be able to hand out blanket exemptions.

---

## Moderator

`moderators.yaml` is the moderator list. It was called `admins.yaml` before 1.7.0.

**Upgrading from 1.6.x is automatic.** On the first boot after the update, ServerGuard
copies every SteamID out of `admins.yaml` into a new `moderators.yaml`, then renames
the old file to `admins.yaml.legacy` so there's only one file in play. Nothing for you
to do, and nothing is lost — check the log for:

```
[ServerGuard] Migrated admins.yaml -> moderators.yaml (N moderator(s)).
```

If the old file is malformed and can't be read, the migration stops and leaves it
alone rather than guessing — copy the IDs across by hand in that case.

Moderators keep every bypass the old "admin" tier had:

- run `sg` commands
- skip the devcommand gate and the console guard (`consoleGuardExemptModerators`,
  default `true`)
- skip the speed check and the character limit
- optionally exempt from forced map positions (`forceMapPositionsExemptAdmins`)

Since **2.0.0** moderators **do go through the mod check** — only owners skip it. Put
staff-only tooling in `moderator_allowed_mods:` in `allowed_mods.yaml`; moderators are
held to `required_mods` + `allowed_mods` + that list, players to the first two.

Moderators are **not** exempt from the 2.0 cheat-taint rules (`CheatedItem`,
`CheatedBuild`, `DebugFly`) unless `cheatTaintExemptModerators: true`.

Moderators do **not** need to be in Valheim's `adminlist.txt`. Everything on their
command list either runs on their own client or is authorised by ServerGuard itself.
Owners are treated as vanilla admins automatically.

On every login a moderator gets a chat message: a greeting, the dev commands they
currently have, where the `sg` tools are, and a reminder to moderate responsibly. Owners
and moderators also see the world X/Z under the cursor on the large map.

What they do **not** get: immunity from the ban layer. A moderator can be kicked and
can be banned. That's the difference between the two tiers.

```yaml
# conf/moderators.yaml
moderators:
  - "76561198000000001"
  - "76561198000000002"
```

An `admins:` key is still accepted here too, so pasting in an old file works.

---

## Dev commands (2.0)

Valheim refuses cheat commands on a dedicated-server client no matter who types them — the
game only allows them for the host of a listen server. ServerGuard lifts that for staff.

**Owners** get everything. Type `devcommands` in the F5 console, then use `fly`, `god`,
`ghost`, `spawn`, `goto`, `heal`, `tod`, `skiptime`, `setworldmodifier`, `randomevent`,
`debugmode` (with its Z / B hotkeys and Ctrl+click map teleport) and the rest, exactly as
in single-player. The server also treats owners as vanilla admins, so `kick` / `ban` /
`save` and the commands Valheim runs server-side all work without an `adminlist.txt`
entry.

**Moderators** get only what you put in `moderatorDevcommands`:

```yaml
# conf/settings.yaml
enableOwnerDevcommands: true
enableModeratorDevcommands: true
moderatorDevcommands:
  - goto
  - pos
  - removedrops
  - stopevent
  - find
```

The default list is deliberately narrow: nothing on it creates items, builds for free
or changes the world, so a moderator cannot hand out spawned gear or `nocost` a base for
a player. If a moderator turns up with cheat-flagged items or builds anyway, the
cheat-taint rules report them like anyone else (`cheatTaintExemptModerators: false`).

A moderator typing anything outside the list sees *"`spawn` refused — this dev command is
not in the server's moderator list"*, and the attempt is posted to your admin Discord
channel. It is **not** a violation and never counts toward auto-ban — they are staff.
`devcommands` itself is always allowed to a moderator with a non-empty list (it only
switches the mode on). `fly`, `debugmode`, `spawn`, `itemset`, `nocost`,
`noplacementcost` and `location` are **never** granted to a moderator, even if you list
them — the server drops them with a log line.

Everything that runs on the server (`skiptime`, `sleep`, `randomevent`, world modifiers)
is logged with who ran it and posted to the admin channel. On connecting, staff get one
console line telling them what they have been granted.

All three settings hot-reload, as do `owners.yaml` and `moderators.yaml`, so promoting or
demoting someone takes effect while they are online. Set either `enable…` switch to
`false` to turn the feature off for that tier; players are unaffected either way — for
them the console guard applies exactly as before.

If you were running *Server Devcommands* just to give staff cheats, you can drop it.

---

## Checking a player's tier

```
sg whois <steamid|name>
sg status
```

`sg whois` shows `role=owner|moderator|player` and flags an active ban. `sg status`
shows the owner and moderator counts.

Join notifications distinguish the tiers in Discord: 👑 for an owner, 🛡️ for a
moderator.

---

## Hot-reload

Both files hot-reload. Promoting or demoting someone takes effect within a second and
re-pushes the console policy to everyone online, so a change lands without anyone
having to reconnect.
