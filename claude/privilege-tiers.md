# Privilege Tiers

Three tiers as of 1.7.0. Two config files; the only settings that mention tiers are
the 2.0 dev-command grants.

| Tier | File | Meaning | Dev commands (2.0) |
|---|---|---|---|
| **Owner** | `conf/owners.yaml` | The server operator. Exempt from **every** rule, unconditionally. | **All**, plus vanilla admin rights (`enableOwnerDevcommands`). |
| **Moderator** | `conf/moderators.yaml` | Staff. Everything the pre-1.7 "admin" tier had. | Only `moderatorDevcommands` (`enableModeratorDevcommands`). |
| **Player** | — | Everyone else. | None — vanilla refusal plus the console guard. |

## Migration from `moderators.yaml`

`MigrateAdminsToModerators()` runs from `EnsureFoldersAndFiles`, before any load and
before the "create if missing" step (otherwise an empty `moderators.yaml` would be
written first and the migration would then decline to overwrite it).

```
admins.yaml exists?
  no  -> nothing to do
  yes -> moderators.yaml already exists?
           yes -> just rename admins.yaml -> admins.yaml.legacy
           no  -> parse it, write moderators.yaml with the IDs,
                  rename admins.yaml -> admins.yaml.legacy
```

Renaming rather than deleting matches the existing `TryRenameLegacy` pattern used for
`ignore_mods.yaml` / `mod_patterns.yaml`, and leaves exactly one live file so an
operator editing the wrong one notices.

If the old file can't be parsed, the migration **bails out without writing anything**
and leaves `moderators.yaml` in place, so the IDs are recoverable by hand and the next
boot retries.

`ModeratorsDoc` accepts both a `moderators:` and an `admins:` key and unions them, so
pasting an old file in still works.

---

## Predicates

```csharp
internal bool IsOwner(string id)     => _owners.Contains(id);
private  bool IsModerator(string id) => _admins.Contains(id);
private  bool IsAdmin(string id)     => IsModerator(id) || IsOwner(id);
private  string RoleOf(string id)    => "owner" | "moderator" | "player";
```

`IsAdmin` deliberately keeps its old meaning — "has staff bypasses". Every pre-existing
call site therefore behaves exactly as before, and owners inherit all of them for free
by being a superset. Only code that needs to *distinguish* the tiers calls `IsOwner` /
`IsModerator`.

An owner does **not** need to also be listed in `moderators.yaml`.

---

## Where the owner bypass is enforced

Most rules already called `IsAdmin`, so owners inherit those. The rest are explicit
short-circuits:

| Site | Behaviour for an owner |
|---|---|
| `AddViolation` | Returns immediately. No strike, no Discord post, no auto-ban progress. **This is the choke point every rule funnels through**, which is what makes "exempt from every rule" true by construction rather than by enumeration. |
| `TryKick` | Refuses. Covers `sg kick`, the attestation timeout, the character-limit kick, policy failures, the ban sweep, and the `cheatTaintBypassPolicy: kick` disconnect. |
| `IsBannedId` | Returns false before the list lookup — a hand-edited `bans.yaml`, or an auto-ban written before the ID was promoted, cannot lock the owner out. |
| `AddBan` | Refuses to write the entry at all, so the file stays honest. |
| `CmdBan` | Explicit "is an owner and cannot be banned" reply. |
| `ApplyForcedMapPosition` | Exempt regardless of `forceMapPositionsExemptAdmins` (that setting governs moderators only). |
| `SendCheatItemRemovalIfEnabled` | Skipped. |
| `SendConsolePolicy` | `exempt = 1` regardless of `consoleGuardExemptModerators`. |
| `CheatTaintExempt` (CheatedItem / CheatedBuild / DebugFly, 2.0) | Exempt. Moderators are **not** unless `cheatTaintExemptModerators`. |
| `Patch_OnNewConnection` attestation skip (2.0) | Owners only. **Moderators attest like players**, with `moderator_allowed_mods` added to their allowlist (`ValidateAgainstPolicy(manifest, pid)`). |

Inherited via `IsAdmin` (unchanged call sites): character limit, speed check,
devcommand/console reporting, skill cap, animation-cancel *reporting*, death log
routing, `sg` authorisation. (The attestation skip was `IsAdmin` until 2.0; it is
`IsOwner` now.)

One rule is enforced entirely **client-side** and so cannot be waved through by the
server: the emote/animation-cancel gate swallows the input locally before any RPC is
sent. `ShouldBlockAnimationCancel` therefore checks `ClientPlugin.IsOwnerClient`,
which reads the `role` field of the console-policy push — the only channel that
carries the tier to the client. Moderators are *not* exempt from that gate (only from
the reporting), which matches the pre-1.7 behaviour.

`Patch_Inventory_AddItem` has no per-peer attribution at its seam (an `Inventory` isn't
tied to a peer), so there is nothing to exempt — it was already anonymous.

---

## Fail direction

`LoadOwners` fails **closed**: a parse error leaves `_owners` empty and logs an error.
An unreadable `owners.yaml` must not be able to hand out blanket rule exemptions.

Contrast `LoadBans`, which fails **open** (keeps the last good list) because a
malformed ban file must not be able to deny every login. Different question, opposite
safe answer.

---

## Console guard interaction

| Tier | Exempt from console guard? |
|---|---|
| Owner | Always. No setting affects this. |
| Moderator | When `consoleGuardExemptModerators: true` (default). |
| Player | No. |

The server resolves this into a single `exempt` boolean in the
`ServerGuard_ConsolePolicy` payload, so the companion never has to know about tiers.
`role` also travels, for the client-side log line only.

Both `LoadOwners` and `LoadAdmins` call `BroadcastConsolePolicy()` on hot-reload —
without it, a player promoted or demoted mid-session would keep the console rights
they had at connect time.

## Dev commands per tier (2.0)

The same policy push carries `devMode` (`all` / `list` / `none`) and the moderator
list. Where each tier is authorised:

| Site | Owner | Moderator | Player |
|---|---|---|---|
| `DevcommandModeFor` → policy push | `all` | `list` (if enabled and non-empty) | `none` |
| `IsDevcommandAllowed(pid, cmd)` — server-side gate for forwarded / routed commands | true | `cmd == devcommands` or in list | false |
| `IsVanillaAdminByGrant` → `Patch_ZNet_ListContainsId` — reads as `adminlist.txt` member | true | **false** (kept out on purpose: vanilla admin RPCs are all-or-nothing) | false |
| `ModeratorReservedCommands` (`fly debugmode spawn itemset nocost noplacementcost location`) | n/a | **never**, whatever the list says | n/a |
| Client login messages (2.0) | one console line on grant | chat welcome every login: greeting, live command list, `sg help`, responsibility note | — |
| Staff map coordinates (2.0, `Patch_Minimap_UpdateBiome_StaffCoords`) | yes | yes | no |
| Client `ShouldBlockConsoleCommand` | exempt anyway | non-listed dev-only command → category `moderator` | unchanged |

Full mechanism in `claude/console-guard.md`, *Staff dev commands*.

---

## Operational advice

Keep `owners.yaml` to one entry. Anyone in it is invisible to every check the mod
performs: no speed check, no skill cap, no item validation, no build-log-driven
violation, no ban. That is the point of the tier, and also the reason not to hand it
out. Use `moderators.yaml` for staff — moderators still get every practical bypass they
need to do their job while remaining bannable if something goes wrong.

`sg status` reports both counts; `sg whois` reports the tier and any active ban.
