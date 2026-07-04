# Deployment Guide — Valheim ServerGuard v1.5

A step-by-step walkthrough for installing ServerGuard on a Windows-hosted Valheim dedicated server and on each player's PC.

If you only want a quick overview, read the [README.md](README.md) instead. This document is the full version with screenshots-worth-of-detail.

---

## Before you start — checklist

You need:

- A working **Valheim Dedicated Server** install (the `valheim_server.exe` one from Steam → Library → Tools).
- **BepInEx 5.4.x** installed on the server. If `BepInEx/` doesn't exist in your server folder yet, install it before continuing — see the BepInEx-for-Valheim guide on Thunderstore or the BepInEx GitHub releases page.
- The two compiled DLLs from this repo:
  - `bin/Release/Valheim-ServerGuard.dll` (server-side)
  - `ServerGuard.Client/bin/Release/Valheim-ServerGuard-Client.dll` (every player's PC)

  If you don't have them yet, build them first — see [BUILD.md](BUILD.md).
- Each player needs **BepInEx** running on their Valheim client. Most players already have this via [r2modman](https://thunderstore.io/c/valheim/p/ebkr/r2modman/) or Vortex.

---

## Part A — Server installation (one-time)

### A1. Find your server folder

Open Steam → Library → Tools → right-click **Valheim Dedicated Server** → **Properties** → **Local Files** → **Browse**.

Typical paths:

```
C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server
D:\Steam\steamapps\common\Valheim dedicated server
```

You'll see something like this inside:

```
Valheim dedicated server\
├── BepInEx\           ← must already exist
│   ├── plugins\
│   ├── config\
│   └── core\
├── valheim_server.exe
└── start_headless_server.bat
```

If `BepInEx\` is missing, install BepInEx first.

### A2. Drop in the server plugin

Copy `Valheim-ServerGuard.dll` into:

```
<server>\BepInEx\plugins\Valheim-ServerGuard.dll
```

It can sit directly inside `plugins\` — no subfolder needed.

### A3. Boot the server once

Run `start_headless_server.bat` (or `valheim_server.exe`) and wait until you see in the console:

```
[Info: Valheim ServerGuard] [ServerGuard] settings.yaml loaded
[Info: Valheim ServerGuard] [ServerGuard] admins.yaml loaded (0 admins)
[Info: Valheim ServerGuard] [ServerGuard] allowed_mods.yaml loaded (required=1, allowed=29, banned=0)
[Info: Valheim ServerGuard] [ServerGuard] Loaded (v1.5.0). Enforcement: ON. RequireCompanion: ON. RequireHmac: ON. ...
[Info: Valheim ServerGuard] [ServerGuard] sharedSecret in use (copy to every client.yaml): <a long base64 string>
```

The last line is your **password**. Copy and save it somewhere — every player needs the exact same string in their config.

> If you scroll up and don't see the password, open `BepInEx/LogOutput.log` and search for `sharedSecret in use`.

Stop the server with Ctrl+C.

### A4. Locate the generated config

```
<server>\BepInEx\config\ServerGuard\conf\
├── settings.yaml          ← server settings + the password
├── admins.yaml            ← Steam IDs that bypass checks
├── allowed_mods.yaml      ← the required / allowed / banned mod lists
├── registrations.yaml     ← auto-managed
├── violations.yaml        ← auto-managed
└── metrics.yaml           ← auto-managed
```

Open `settings.yaml` to confirm `sharedSecret:` is filled in. If it's empty, add a value or delete the line and restart — ServerGuard auto-fills it on next boot.

### A5. (Optional) Add yourself as admin

Open `admins.yaml` and add your Steam ID:

```yaml
admins:
  - "76561198012345678"
```

Find your ID at https://steamid.io (paste your profile URL, copy the **steamID64**).

Admins **skip the entire mod check**, so use this for yourself only when troubleshooting.

### A6. (Optional) Discord webhooks

ServerGuard supports two Discord webhooks: one for a public channel and one for admins only.

Open `settings.yaml` and fill in whichever you want:

```yaml
# Server boot/shutdown, player joins/leaves, shouts, deaths, raid events
discordWebhookUrl: 'https://discord.com/api/webhooks/<id>/<token>'

# Admin login/logout, kicks, bans, violations
discordAdminWebhookUrl: 'https://discord.com/api/webhooks/<id>/<token>'
```

Get each URL from Discord: server → channel → ⚙ → **Integrations → Webhooks → New Webhook → Copy URL**.

Save the file. ServerGuard hot-reloads — no restart needed.

> **Migrating from an older settings.yaml?** The old key `discordWebhookUrlAdmin` is still accepted as a fallback — you don't need to rename it.

**Maintenance mode:** Set `maintenanceMode: true` before taking the server down. All public events (including the next boot notification) are redirected to the admin webhook instead. Toggle back to `false` when maintenance is complete.

**Daily summary:** `dailySummaryEnabled: true` posts a summary to the webhook named in `dailySummaryChannel` (`public` or `admin`).

### A7. (Optional) Tune violation counting

By default, attestation failures (`companionMissing`, `hmacInvalid`, etc.) are log-only and do not count toward the auto-ban threshold, while gameplay integrity rules (`speedHack`, `illegalItem`, etc.) do count. You can change any of these in the `countAsViolation:` sub-section of `settings.yaml` — set a rule to `true` to have it count, `false` to log only.

```yaml
countAsViolation:
  companionMissing: false   # log-only — set true to ban persistent vanilla clients
  hmacInvalid: false        # log-only — set true to ban forged manifests
  characterNameLimitExceeded: true
  speedHack: true
  # ... (see README for full list)
```

---

## Part B — Client installation (every player, including the host)

Each player follows these same four steps on their own PC.

### B1. Drop in the client plugin

If using **r2modmanager**:

1. Open r2modman.
2. Select your Valheim profile.
3. Click the gear icon → **Browse profile folder**.
4. Drop `Valheim-ServerGuard-Client.dll` into `BepInEx/plugins/`.

If installing manually:

```
<your Valheim install>\BepInEx\plugins\Valheim-ServerGuard-Client.dll
```

### B2. Launch Valheim once

Start Valheim, wait until the **main menu** loads, then close the game.

This step matters: the companion plugin needs Valheim to fully start so all your other mods are loaded. On first launch it creates two files:

```
<profile>\BepInEx\config\ServerGuard\client.yaml
<profile>\BepInEx\config\ServerGuard\mods_for_allowed_mods.yaml
```

### B3. Paste the shared password

Open `client.yaml` and paste the password your server admin gave you:

```yaml
sharedSecret: "ftnNxBse+Lx2H41ixsTJ637CFffq58C5rrvwwXrabYU="
```

Watch out for:
- **Don't change the quotes** — leave them as they are in the file (single or double, both work).
- **No trailing spaces** after the value.
- **No accidental line break** — the password is one long string.

### B4. Verify the companion loaded

Open the BepInEx log:

```
<profile>\BepInEx\LogOutput.log
```

Find a line near the top that reads:

```
[Info: Valheim ServerGuard Client] [ServerGuard.Client] Loaded v1.5.0. Manifest entries: 29. HMAC: ON
```

If `HMAC: OFF`, your `client.yaml` doesn't have a password yet — go back to B3.

If `Manifest entries:` shows a much smaller number than the mods you have installed, close the game, **delete** `mods_for_allowed_mods.yaml`, and launch again. (The companion now waits for all plugins to finish loading before counting them, so this should not happen on a fresh setup.)

---

## Part C — Build the allowlist (one player, once)

The server is now running, but its `allowed_mods.yaml` is mostly empty. You need to fill it with the mods your modpack uses.

### C1. Pick a "reference" client

This is the player whose modpack defines what's allowed. Usually the host. They must have **every mod the server should permit** installed and working locally.

### C2. Copy the export file

On the reference client's PC, find:

```
<profile>\BepInEx\config\ServerGuard\mods_for_allowed_mods.yaml
```

Open it. It looks like this:

```yaml
# ServerGuard - allowed_mods snippet generated by ServerGuard.Client v1.5.0
# Generated: 2026-05-10 09:42:01Z   Mods on this client: 29
# ...

required_mods:
  - com.taeguk.valheim.serverguard.client|<hash>    # Valheim ServerGuard Client v1.5.0

allowed_mods:
  - advize.Armoire|<hash>                             # Armoire v1.1.5
  - balrond.astafaraios.BalrondShipyard|<hash>        # BalrondShipyard v1.6.5
  - com.bruce.valheim.comfyquickslots|<hash>          # ComfyQuickSlots v1.9.0
  ...

banned_mods: []
```

### C3. Paste into the server's allowed_mods.yaml

On the server, open:

```
<server>\BepInEx\config\ServerGuard\conf\allowed_mods.yaml
```

**Replace** the existing `required_mods:`, `allowed_mods:`, and `banned_mods:` blocks with the three blocks from the export file.

Save. The server's log will show:

```
[Info: Valheim ServerGuard] [ServerGuard] Reloaded: allowed_mods.yaml
[Info: Valheim ServerGuard] [ServerGuard] allowed_mods.yaml loaded (required=1, allowed=28, banned=0)
```

No restart needed.

### C4. Decide on hash pinning

Each entry in the export comes with a `|<sha256>` suffix that **locks the mod to a specific DLL version**. This is great for security but means you'll need to refresh the list whenever a mod updates.

If you'd rather accept any version of each mod, use search & replace in `allowed_mods.yaml` to remove the `|<hash>` portion. The GUID alone still uniquely identifies the mod.

---

## Part D — Smoke test

### D1. Server-side

In `<server>\BepInEx\LogOutput.log` (a fresh tail) you should see, on a successful connect:

```
[Info: Valheim ServerGuard] [ServerGuard] Incoming connection: <name> (<steamid>)
[Info: Valheim ServerGuard] [ServerGuard] <steamid> attested OK (29 mods).
```

### D2. Client-side

In the player's `BepInEx/LogOutput.log`, after connecting:

```
[Info: Valheim ServerGuard Client] [ServerGuard.Client] Registered manifest request handler on server peer.
[Info: Valheim ServerGuard Client] [ServerGuard.Client] Sent manifest (1700 bytes, 29 mods).
```

### D3. Test rejection

To prove enforcement works, add a fake banned entry to the server's `allowed_mods.yaml`:

```yaml
banned_mods:
  - com.jotunn.jotunn   # temporarily ban Jotunn
```

Save. Have a player reconnect. They should be kicked with:

```
[ServerGuard] <steamid> REJECTED: BannedMod - Disallowed mod present: Jotunn
```

Remove the line afterwards.

---

## Part E — Deploying to friends

Send each player two things via Discord/email/whatever:

1. The file `Valheim-ServerGuard-Client.dll`.
2. A copy-paste of the line:
   ```
   sharedSecret: "<the password from the server's settings.yaml>"
   ```
   …with instructions to put it in `BepInEx/config/ServerGuard/client.yaml`.

That's all. Their setup is Part B (steps B1–B4).

If you change the password later, redistribute the new value and have everyone update their `client.yaml`. The server hot-reloads its own; players need to relaunch Valheim.

---

## Troubleshooting

### Server: `allowed_mods.yaml loaded (required=0, allowed=0, banned=0)` but the file looks populated

The YAML keys must be exactly `required_mods:`, `allowed_mods:`, `banned_mods:` (snake_case). If the file has `requiredMods:` or `RequiredMods:`, the parser won't find the lists.

### Server: every connection times out with `CompanionMissing`

Either the player isn't running `Valheim-ServerGuard-Client.dll`, or BepInEx isn't loading on their client. Have them check their own `BepInEx/LogOutput.log` for `Loading [Valheim ServerGuard Client 1.5.0]`. If absent, BepInEx isn't installed correctly on their side.

### Server: rejections say `HmacInvalid` even with the password set

Three causes:
1. **Mismatched secret.** Compare the server's `settings.yaml` value to the client's `client.yaml` value byte-for-byte. Watch out for surrounding quotes and trailing whitespace.
2. **Clock skew.** The client's system clock is more than 2 minutes off from the server's. On Windows, check `w32tm /query /status`. Alternatively raise `maxClockSkewSeconds` in `settings.yaml`.
3. **Tampered manifest.** Genuine HMAC failure. Should not happen on a clean setup.

### Client: only some of my mods show up in `mods_for_allowed_mods.yaml`

The export ran before BepInEx finished loading. **Delete** `mods_for_allowed_mods.yaml` and launch Valheim again. The companion now defers manifest collection until all plugins are loaded; on a fresh setup this should produce a complete list.

### Player got kicked but then got back in / stayed connected

Means the server-side disconnect call didn't fire properly. Make sure you're running `Valheim-ServerGuard.dll` v1.5.0 — the kick path uses `ZNet.Disconnect(peer)` for a hard tear-down.

### A test player hit `violationThreshold` and got auto-banned

In the server console:

```
unban <steamid>
```

Optionally remove their entry from `BepInEx/config/ServerGuard/conf/violations.yaml` so the counter resets.

### I want to allow vanilla connections temporarily

In `settings.yaml`:

```yaml
requireCompanion: false
```

Save. The server hot-reloads. Vanilla clients are admitted (they still have to pass the rest of the policy if `allowUnlisted: false`, so set that too if you want to allow any mod set):

```yaml
requireCompanion: false
allowUnlisted: true
```

Reverse afterward.

---

## Maintenance

### Adding a new mod to the modpack

1. The reference player installs the mod locally.
2. Delete their `mods_for_allowed_mods.yaml`.
3. Launch Valheim once.
4. Open the regenerated file, copy the new entry into the server's `allowed_mods.yaml`.
5. Server hot-reloads.

### Updating an existing mod (with hash pinning)

If you pinned hashes, the mod's new version will fail validation. Either:
- Repeat the "add a new mod" flow above to refresh the hash, **or**
- Drop the `|<hash>` suffix from the entry to accept any version.

### Removing a mod

Delete the line from `allowed_mods.yaml`. Players running it will be kicked on next connect (with reason `Unapproved mod: <name>`).

### Rotating the shared password

1. Edit `settings.yaml` on the server, replace `sharedSecret:` with a new random value.
   - Or: delete the `sharedSecret:` line and restart the server. ServerGuard regenerates one and writes it back.
2. Distribute the new value to every player.
3. Each player edits their `client.yaml` and relaunches Valheim.

The server hot-reloads its own setting, so players currently connected stay connected — but new connections require the new password.

---

## Reference: where things live

| What | Where |
|---|---|
| Server plugin DLL | `<server>\BepInEx\plugins\Valheim-ServerGuard.dll` |
| Server config | `<server>\BepInEx\config\ServerGuard\conf\` |
| Server log | `<server>\BepInEx\LogOutput.log` |
| Client plugin DLL | `<player profile>\BepInEx\plugins\Valheim-ServerGuard-Client.dll` |
| Client config | `<player profile>\BepInEx\config\ServerGuard\client.yaml` |
| Client export (drop-in for server) | `<player profile>\BepInEx\config\ServerGuard\mods_for_allowed_mods.yaml` |
| Client log | `<player profile>\BepInEx\LogOutput.log` |

For everything else (option semantics, format details, advanced settings) see the [README.md](README.md) — Advanced section.
