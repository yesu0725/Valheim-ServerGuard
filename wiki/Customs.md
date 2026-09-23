# Customs (inventory baseline)

Customs remembers what each character was carrying when it was last on your server, and
checks what it carries when it comes back. Anything extra was obtained somewhere else: a
single-player world, another server, or a character file restored from a backup (the
classic "deposit it in a chest, roll the character back, log in again" duplication).

No other rule sees these items. They were never spawned on your server, and an item
crafted legitimately in another world carries no cheat mark for
[CheatedItem](Anti-Cheat-Features) to find.

Customs is **off by default**, and when you switch it on it starts in **dry run**, which
logs what it would refuse and refuses nothing.

---

## How it works

A dedicated server cannot see a player's inventory. It lives in the player's game and is
saved in their character file. So the ServerGuard client **declares** it:

1. The player connects and passes the normal [mod attestation](Allowed-Mods-and-Modset).
   Only then does the server ask for inventory declarations.
2. When the character spawns, the client declares what it brought in. The declaration
   combines two snapshots: one as the character file was loaded, and one after the game and
   other mods finished spawning it. Items the game drops out of the grid while spawning, or
   another mod moves in during that same step, are declared too.
3. The server compares the declaration with the character's **baseline**, the inventory it
   was last trusted with. Only **additions** count. Arriving with less is always fine.
4. While the player is online the client keeps the baseline current. It reports a change
   once the inventory has been different for a few seconds, sends a full checkpoint every
   couple of minutes, and sends a final report when the player logs out. What they earn on
   your server becomes their new baseline.

Items are compared by more than their name: quality, style variant, world level, crafter
and any mod data attached to the item. A sword upgraded elsewhere, or a copy crafted by
someone else, counts as a new item.

### Trust boundary

The ServerGuard client is the only source of the inventory, because the server has no
other. Attestation and a [hash-pinned modset](Allowed-Mods-and-Modset) make it hard to run
a modified client, but a player who gets past attestation with a doctored client can
declare anything. Customs checks where inventories came from. It does not make the server
safe from a modified client.

What it does guarantee:

- Silence is not a way through. A client that never declares, or declares garbage, is
  refused in enforce, including one that never reports its character entering the world.
- A refused, unusable or replayed report is **never** written to a baseline. The last
  trusted baseline stays exactly as it was.
- A report is tied to the connection it arrived on. It cannot speak for another player,
  and a session cannot switch character.

---

## Turning it on

1. **Update your modpack first.** A client without Customs never declares anything. In dry
   run that is logged; in enforce the player is disconnected.
2. Set `enableCustoms: true` and leave `customsMode: dryrun`. Every character's first
   arrival becomes its baseline.
3. Play for a while. Watch the admin channel for
   `:passport_control: Customs (dry run) — … would be refused`. Each one is a disconnect
   you would have issued. If a mod hands out items at login, add those prefab names to
   `customsIgnoredItems`.
4. Decide what happens to characters the server has never seen, in `customsNewCharacters`.
5. Switch to `customsMode: enforce`. Keep `sg customs approve` to hand.

Do not go straight to enforce. Until a character has a baseline, enforce treats it as a new
character. With the default `fresh` policy, that refuses every character on day one that is
carrying anything.

---

## Settings

All in `conf/settings.yaml`, hot-reloaded.

| Setting | Default | What it does |
|---|---|---|
| `enableCustoms` | `false` | Master switch. Off: nothing is requested, stored or judged. |
| `customsMode` | `dryrun` | `dryrun` logs what enforce would refuse and keeps learning. `enforce` disconnects. Any other value means `dryrun`. With ServerGuard's own `enforce: false`, Customs also runs as `dryrun`. |
| `customsNewCharacters` | `fresh` | In enforce, a character with no baseline yet is admitted: `fresh` only if it carries nothing, `any` always (its first arrival becomes the baseline), `approve` never without `sg customs approve`. |
| `customsExemptModerators` | `false` | Moderators are inspected unless this is on. Owners are never inspected. |
| `customsIgnoredItems` | `[]` | Prefab names Customs never counts, for example items a mod gives out at login. |
| `customsArrivalTimeoutSeconds` | `60` | How long after the character appears in the world its declaration may take before it counts as missing (minimum 5). If no character appears within 15 minutes of connecting, the timeout starts anyway. |
| `customsCheckpointSeconds` | `120` | How often clients re-send their inventory even if nothing changed (30–3600). |
| `customsDebounceSeconds` | `5` | How long a change waits before it is reported (1–120). |
| `customsMaxItemRecords` | `256` | Distinct stacks one declaration may hold (32–4096). Raise it for very large modded inventories. |

`countAsViolation` has one rule for Customs, `UndeclaredItems`, informational by default.
The refusal is already a disconnect. Turn the rule on if repeat offenders should build
towards an auto-ban.

---

## What happens on arrival

| | Nothing new | Undeclared items | New character, admissible | New character, not admissible |
|---|---|---|---|---|
| **dryrun** | cleared | admitted, logged, admin post | baseline established | baseline established, logged |
| **enforce** | cleared | **disconnected** | baseline established | **disconnected** |

"Admissible" follows `customsNewCharacters`. A pending `sg customs approve` turns an enforce
disconnect into an admission once, and that arrival becomes the character's new baseline.

| Declaration… | dryrun | enforce |
|---|---|---|
| never arrives within `customsArrivalTimeoutSeconds` | logged once | disconnected |
| is malformed, oversized, or about a different character | logged; a session already admitted stops updating the baseline | disconnected |
| is stale, replayed, or over the rate limit | ignored | ignored |

Nothing that is refused, unusable or ignored is ever written to a baseline.

Posts: a disconnect is posted to the public channel like any other kick (for undeclared
items: `was kicked — arrived carrying items from outside this server`), and the item list
goes to the admin channel. Dry-run findings about undeclared items go to the admin channel.
New characters in dry run are only logged, because learning them is what a dry run is for.

### Changing settings while players are online

- **Switching Customs on**, or a player coming into scope (for example a moderator losing
  `customsExemptModerators`): their current inventory becomes their baseline without a
  judgement. Part of it was earned this session, so there is no arrival to judge. Their
  next login is judged normally.
- **Switching Customs off**, or a player leaving scope: their client stops reporting.
  Baselines already accepted are still written to disk.
- **dryrun ↔ enforce**: applies to the next arrival. Nobody already online is judged again.
- **Timing and `customsMaxItemRecords`**: pushed to online clients straight away.

Baselines only advance while Customs is on. If you switch it off while people keep playing,
their baselines go stale, and switching straight back to enforce would refuse anyone who
gained items in the meantime. After any period with Customs off, use dry run for a while
before going back to enforce.

---

## Admin commands

| Command | What it does |
|---|---|
| `sg customs` / `sg customs status` | Mode, settings, store health, pending approvals, and every live session. |
| `sg customs inspect <steamid\|name>` | A player's live session (what they carry, what was flagged) and their stored baselines. |
| `sg customs approve <steamid\|name>` | Their next arrival that Customs would refuse is admitted instead, and becomes their baseline. Spent on use; expires after 24 hours. |
| `sg customs unapprove <steamid\|name>` | Withdraw a pending approval. |
| `sg customs reset <steamid\|name> [characterId]` | Forget stored baselines (one character, or all). Only while the player is offline. |

Approve, unapprove and reset are posted to the admin channel. Moderators cannot approve or
reset themselves.

---

## Inventory mods

Customs reads the player's whole inventory grid. That includes equipped items and any extra
rows an inventory mod adds.

- **AzuExtendedPlayerInventory** extends the player's own inventory (extra rows, equipment
  and quick slots) rather than keeping a container of its own, so it should be covered
  with nothing to configure. This has not been verified against a live
  AzuExtendedPlayerInventory install: check it with the dry-run test below before you
  enforce.
- **Items a mod moves into the inventory while the character spawns** are part of the
  arrival declaration, as long as the mod does it during the spawn itself. Anything a mod
  moves in later counts as picked up during the session.
- **A container item** (a backpack from a backpack mod) is seen as one item, with its
  contents folded into its mod data if the mod keeps them there. Any change to what is
  inside then makes the backpack itself count as new.
- **Not covered:** items a mod keeps in a separate container of its own, outside the
  inventory. The retired EquipmentAndQuickSlots mod, installed on its own, is one example.
  If your modpack has such a mod, Customs cannot see those slots.

Verify your own modpack in dry run: put an item in each kind of slot, relog, and check that
`sg customs inspect` counts it.

---

## Files

Under `BepInEx/config/ServerGuard/customs/`, created the first time Customs needs it (never
while it is off):

| Path | What |
|---|---|
| `<steamid>/<characterId>.json` | One character's baseline. Written atomically, so a crash leaves the old file or the new one. |
| `<steamid>/<characterId>.json.bak` | The previous version, kept by every write. |
| `<steamid>/<characterId>.json.corrupt-<time>` | A file that could not be parsed, moved aside and never overwritten. Customs falls back to the `.bak`; with no readable copy the character counts as new. |
| `approvals.json` | Pending `sg customs approve` entries. |

If the folder cannot be read at all (permissions, a failing disk), or a baseline was
written by a newer ServerGuard than the one running (after a downgrade), the affected
players are admitted without a check. Nothing is written over the files, and the admin
channel is told. A problem on your side should not lock players out. If writes keep
failing, the admin channel is told as well. Baselines wait in memory until the disk
recovers, and a restart loses them.

---

## Known limitations

- **A modified client can lie.** See the trust boundary above.
- **Only SteamID64 accounts are inspected.** Baselines are kept per SteamID. A player whose
  SteamID ServerGuard cannot resolve (on a crossplay server, for example an Xbox account)
  is not inspected, and the server log says so when they join.
- **Crash rollbacks look like imports.** Valheim saves the character file every so often. After
  a crash the character is back at its last save, so anything used or stored since then
  reappears. Customs cannot tell that from a deliberate rollback duplicate. Approve the
  player if it was an honest crash.
- **The last few seconds before a crash** may not reach the server, so items picked up in
  that window can be flagged at the next login.
- **The first baseline trusts what it sees.** A character's first arrival in dry run (or
  under `any`) is recorded as-is, including anything imported before Customs was on.
- **Mods that rewrite item data by themselves** (timers, counters) make those items look new.
  Dry run will show it. Such items cannot be ignored individually except by prefab name.
- **Refusal is not confiscation.** A refused player is disconnected as soon as the
  declaration is judged, normally well under a second after they spawn. Anything they
  manage to drop in that moment stays in the world.
- **Old baselines are kept.** Files of characters that never return are not pruned. Use
  `sg customs reset`, or delete their folder while the server is stopped.
