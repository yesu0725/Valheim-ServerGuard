# Allowed Mods and Modset Fingerprint

## `allowed_mods.yaml`

Lives at `BepInEx/config/ServerGuard/conf/allowed_mods.yaml`. Three sections:

```yaml
required_mods:
  - com.taeguk.valheim.serverguard.client    # companion plugin - keep this entry

allowed_mods:
  - com.bepis.bepinex
  - randyknapp.mods.epicloop
  - <more entries>

banned_mods:
  - some.bad.guid
```

### Entry format

Each entry is a string. Two forms:

```yaml
- <GUID-or-Name>                # accepts any version of this mod
- <GUID-or-Name>|<sha256_hex>   # also requires the DLL to hash to this value
```

- **GUID-keyed** (preferred): the BepInEx plugin GUID, e.g. `com.bepis.bepinex`. Stable across name changes.
- **Name-keyed**: the mod's display name. Fragile — mods sometimes change their name.
- **Hash-pinned**: the `|sha256` suffix locks the entry to one specific DLL build. Players running a different build are kicked with `HashMismatch`. Use for security-critical mods.

### Section meanings

| Section | Effect |
|---|---|
| `required_mods` | Every connecting client must report all of these. Missing → kick (`RequiredModMissing`). |
| `allowed_mods` | Extra mods the client may run beyond the required set. |
| `banned_mods` | Any presence is fatal (`BannedMod`). |

If `allowUnlisted: false` (the default), every mod the client has must appear in `required_mods` or `allowed_mods`. If `true`, unlisted mods are tolerated.

## Building your allowlist

The companion plugin writes a ready-to-paste snippet on its first run. On a client machine with your full modpack installed:

```
BepInEx/config/ServerGuard/mods_for_allowed_mods.yaml
```

The file contains every loaded plugin formatted as a hash-pinned `allowed_mods:` block. Copy its contents into your server's `allowed_mods.yaml`, save, and the server hot-reloads.

To regenerate after adding/removing mods on the client: delete `mods_for_allowed_mods.yaml`, launch Valheim once, and the file reappears with the current set.

## Alternative: harvest GUIDs from a real connection

In `settings.yaml`:

```yaml
logPeerManifest: true
```

Connect a real client. The server's `LogOutput.log` now lists every GUID + sha256 in the connecting peer's manifest. Copy what you want into `allowed_mods.yaml`.

## Modset fingerprint

Each time `allowed_mods.yaml` loads, the server computes two fingerprints:

- **Loose** — SHA-256 of the sorted GUID set. Matches across version bumps.
- **Strict** — SHA-256 of the sorted (GUID, sha256) set. Matches only when binaries are identical too.

The short form (first 8 chars) is logged on startup and written to `BepInEx/config/ServerGuard/modset_fingerprint.txt`:

```
loose:  8ce8906e3a...
strict: 19815033f8...
short_loose:  8ce8906e
short_strict: 19815033
```

## Publishing the fingerprint to your community

Share the short loose fingerprint in your Discord / forum / server description:

> **Server modset:** `8ce8906e`

Players install the modpack, launch Valheim once, and see in their client log:

```
[ServerGuard.Client] Modset fingerprint  loose=8ce8906e  strict=19815033
```

If their loose matches yours, they have the right *set* of mods. If strict also matches, they have the exact same builds.

## Connection-time verification

When a player joins, the server computes their fingerprint from the attestation manifest and compares to its own:

- `modset ✓ exact` — same mods + same builds.
- `modset ✓ same set, different versions` — right mods, different builds. Players using a newer/older version of one mod show this.
- `modset ⚠ differs from server` — wrong modpack entirely (or extra/missing mods).

The line is in the server log on every successful join. With verbose admin Discord, you'll see it there too.

## See also

- **[Installation](Installation)** — setting up the companion on each client.
- **[Configuration](Configuration)** — `allowUnlisted`, `requireHmac`, etc.
- **[Anti-Cheat Features](Anti-Cheat-Features)** — what each violation rule does.
