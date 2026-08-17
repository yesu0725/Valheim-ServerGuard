# Privilege Tiers

Added in **1.7.0**. Three tiers, two config files, no settings to configure.

| Tier | File | Who |
|---|---|---|
| **Owner** | `conf/owners.yaml` | You. Normally exactly one SteamID. |
| **Moderator** | `conf/moderators.yaml` | Your staff. |
| **Player** | — | Everyone else. |

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
- skip the attestation handshake
- skip the devcommand gate and the console guard (`consoleGuardExemptModerators`,
  default `true`)
- skip the speed check and the character limit
- optionally exempt from forced map positions (`forceMapPositionsExemptAdmins`)

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
