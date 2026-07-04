# Valheim ServerGuard

Lock your Valheim dedicated server to a specific list of mods. Vanilla and wrong-modpack players are automatically kicked.

It works because every player runs a small companion plugin that tells the server exactly which mods they have loaded — signed with a shared password so it can't be faked. The server compares the list to your allowlist and decides whether to let them in.

---

## What you need

You'll be installing **two files**:

- **`Valheim-ServerGuard.dll`** → goes on your **server** (you, the host)
- **`Valheim-ServerGuard-Client.dll`** → goes on **every player's** Valheim install (you and your friends)

Players who don't install the client file get kicked when they try to join. That's the whole point.

---

## Quick setup (5 minutes)

### Step 1 — Server side

1. Copy `Valheim-ServerGuard.dll` into your server's `BepInEx/plugins/` folder.
2. Start your server **once** and let it boot (about 30 seconds), then stop it.
3. ServerGuard creates a config folder at `BepInEx/config/ServerGuard/conf/` and **auto-generates a password** (called `sharedSecret`) for you.
4. Open `BepInEx/config/ServerGuard/conf/settings.yaml` and **copy the `sharedSecret:` line**. It looks like:
   ```yaml
   sharedSecret: 'ftnNxBse+Lx2H41ixsTJ637CFffq58C5rrvwwXrabYU='
   ```
   You'll need to give this exact value to every player.

### Step 2 — Each player (including you, on your gaming PC)

1. Copy `Valheim-ServerGuard-Client.dll` into the player's `BepInEx/plugins/` folder. With r2modman, this means dragging it into the active profile.
2. Launch Valheim **once**. Wait until the main menu appears, then close the game.
3. Open `BepInEx/config/ServerGuard/client.yaml` and paste the password the server gave you:
   ```yaml
   sharedSecret: "ftnNxBse+Lx2H41ixsTJ637CFffq58C5rrvwwXrabYU="
   ```

### Step 3 — Build the allowlist (one player does this once)

1. Have one trusted player (with the full modpack installed) launch Valheim once. The companion plugin writes a file at:
   ```
   BepInEx/config/ServerGuard/mods_for_allowed_mods.yaml
   ```
2. Open that file. It contains a ready-to-paste list of every mod they have loaded.
3. Copy its contents and **paste them over** the `required_mods:`, `allowed_mods:`, and `banned_mods:` sections in the server's `BepInEx/config/ServerGuard/conf/allowed_mods.yaml`.
4. Save. The server picks up the change in about 1 second — no restart needed.

### Step 4 — Connect

Players join the server normally. The server log (in `BepInEx/LogOutput.log`) will show a line like this for each successful connection:

```
[Info: Valheim ServerGuard] [ServerGuard] 76561198xxxxxxxxx attested OK (29 mods).
```

If someone gets kicked, the log will say exactly why:

```
[Warning: Valheim ServerGuard] [ServerGuard] 76561198xxxxxxxxx REJECTED: DisallowedMod - Unapproved mod: <name>
```

That's the whole setup. **Everything else has sensible defaults.** The rest of this document is only relevant if you want to customize behavior.

---

# Advanced

Stop reading here unless something doesn't work or you want to change defaults.

## How the protection actually works

```
1. Player connects.
2. Server sends a one-time random "challenge" to the player's companion plugin.
3. Companion replies with: a list of every mod loaded, the challenge, a timestamp,
   and a fingerprint (HMAC) computed using the shared password.
4. Server verifies:
     - the fingerprint matches (so the list wasn't tampered with),
     - the challenge matches (so it's not a replayed message),
     - the timestamp is recent,
     - every mod is in the allowlist,
     - no banned mods are present,
     - all required mods are present.
5. If anything fails, the player is kicked with a clear reason.
6. If the companion plugin doesn't reply within 10 seconds, the player is kicked
   for not having the companion plugin installed (vanilla clients fall here).
```

## All configuration files

All on the server, under `BepInEx/config/ServerGuard/conf/`. Edits are picked up live (no restart needed) for `settings.yaml`, `admins.yaml`, and `allowed_mods.yaml`.

| File | What it does |
|---|---|
| `settings.yaml` | Main switches and the shared password. |
| `admins.yaml` | Steam IDs that bypass the mod check entirely. |
| `allowed_mods.yaml` | The required / allowed / banned mod lists. |
| `registrations.yaml` | Auto-managed: which character names each Steam ID has used. |
| `violations.yaml` | Auto-managed: how many times each Steam ID has been rejected. |
| `metrics.yaml` | Auto-managed: connection / detection counters. |

On every player's PC, only one file matters: `BepInEx/config/ServerGuard/client.yaml`, which holds the shared password.

## settings.yaml — every option

| Option | Default | What it does |
|---|---|---|
| `enforce` | `true` | If `false`, kicks are logged but never executed. Use for dry-run testing. |
| `violationThreshold` | `3` | After this many rejected connections of the same kind, the player is auto-banned. |
| `kickMessage` | (built-in) | Message shown when a player is kicked. The specific reason is appended automatically. |
| `banReason` | (built-in) | Reason recorded when an auto-ban triggers. |
| `characterLimit` | `1` | Maximum number of distinct character names a single Steam ID can use on this server. |
| `requireCompanion` | `true` | If `true`, players without the client plugin are kicked on timeout. Set to `false` to allow vanilla connections. |
| `companionTimeoutSeconds` | `10` | How long the server waits for the manifest to arrive before declaring no-companion. |
| `requireHmac` | `true` | If `true`, every manifest must be signed with the shared password. Strongly recommended. |
| `sharedSecret` | (auto-generated) | The shared password. Auto-created on first launch. Must match `client.yaml` on every player. |
| `allowUnlisted` | `false` | If `false`, every mod a player runs must be in `allowed_mods` or `required_mods`. If `true`, unknown mods are tolerated. |
| `maxClockSkewSeconds` | `120` | Reject manifests whose clock is more than this many seconds off from the server's. |
| `logPeerManifest` | `false` | If `true`, the server logs the full mod list of every connecting player. Useful for collecting plugin GUIDs to add to the allowlist. Turn off afterward — it's noisy. |
| `enableMetrics` | `true` | Track connection / detection counters in `metrics.yaml`. |
| `discordWebhookUrl` | `""` | If set, kicks/bans/violations are also posted to a Discord channel via webhook. |
| `discordChannelLink` | `""` | Free-form note for your reference. Not used by the plugin. |

## allowed_mods.yaml — format

Three sections. Each entry is either `GUID` or `GUID|hash`.

```yaml
required_mods:
  - com.taeguk.valheim.serverguard.client    # the companion itself - leave this in
  # - com.azu.anticheat                       # require AzuAntiCheat too, etc.

allowed_mods:
  - com.jotunn.jotunn                                          # match by GUID, any version
  - com.jotunn.jotunn|c56246207080b854363396f2b001b389e7d3...  # match by GUID, specific DLL hash
  - Jotunn                                                     # match by display name (less precise)

banned_mods:
  - com.example.flycheat                                       # always kicked if present
```

**GUIDs** are the strings inside `[BepInPlugin("...")]` in each mod's source code. They never change between versions, so they're the safest choice. The companion plugin's first-run export gives you GUIDs automatically — just paste them in.

**Hash pinning** (the `|hash` part) locks the mod to a specific DLL. Useful if you want to forbid newer or older versions. Dropping the hash means any version with that GUID is accepted.

## admins.yaml

```yaml
admins:
  - "76561198012345678"   # this Steam ID skips the entire mod check
```

Use sparingly. Admins are not asked for a manifest, so they can run any mods.

To find your Steam ID, paste your profile URL into https://steamid.io and copy the **steamID64** number.

## client.yaml (each player's PC)

```yaml
sharedSecret: "<paste the value from the server's settings.yaml>"
```

That's it. The companion reads this once at startup. If you change the password on the server, every player must update their `client.yaml`.

## Refreshing the mod list

When you (or a player) installs a new mod and you want to add it to the server's allowlist:

1. On a client PC, **delete** `BepInEx/config/ServerGuard/mods_for_allowed_mods.yaml`.
2. Launch Valheim once. The companion regenerates the file with the current mod list.
3. Open it and copy the new entries into the server's `allowed_mods.yaml`.

## Common log messages

What you'll see in `BepInEx/LogOutput.log`.

### Successful connection
```
[ServerGuard] Incoming connection: <name> (<steamid>)
[ServerGuard] <steamid> attested OK (29 mods).
```

### Player has the companion but a mod isn't on the allowlist
```
[ServerGuard] <steamid> REJECTED: DisallowedMod - Unapproved mod: <mod-name>
[ServerGuard] Disconnected <steamid>. Reason: ... (Unapproved mod: <mod-name>)
```
Fix: add the mod to `allowed_mods.yaml`, or remove it from the player's modpack.

### Player is missing the companion / didn't install it
```
[ServerGuard] <steamid> did not deliver a manifest within 10s.
[ServerGuard] Disconnected <steamid>. Reason: ... (Missing required companion plugin: ServerGuard.Client)
```
Fix: have the player install `Valheim-ServerGuard-Client.dll` and set their `client.yaml`.

### Wrong password
```
[ServerGuard] HMAC mismatch for <steamid>. Either bad sharedSecret on client, or tampered manifest.
[ServerGuard] Disconnected <steamid>. Reason: ... (Invalid signature)
```
Fix: copy the server's `sharedSecret` into the player's `client.yaml` exactly. Watch for stray quotes or trailing spaces.

### Server itself
```
[ServerGuard] Loaded (v1.3.0). Enforcement: ON. RequireCompanion: ON. RequireHmac: ON. AllowUnlisted: OFF. Required: 1, Allowed: 28, Banned: 0. Metrics: ON
[ServerGuard] sharedSecret in use (copy to every client.yaml): <password>
```
Confirms the plugin loaded and the allowlist parsed. If `Allowed: 0` despite a populated file, the YAML keys are wrong — they must be `required_mods:`, `allowed_mods:`, `banned_mods:` exactly.

## Discord notifications

In `settings.yaml`:

```yaml
discordWebhookUrl: 'https://discord.com/api/webhooks/12345/abcdef...'
```

Get the URL from your Discord server: **Channel Settings → Integrations → Webhooks → New Webhook → Copy URL**.

You'll get real-time alerts for kicks, bans, and rejected manifests, plus a 2-second-buffered stream of all ServerGuard log events to the same channel.

## Rotating the shared password

If a player leaves and you want to lock them out:

1. On the server, edit `settings.yaml` and replace the `sharedSecret:` value with a new random string (or just delete the line and restart — it'll auto-generate a fresh one).
2. Send the new password to every remaining player.
3. They each update their `client.yaml`.

The departing player can no longer connect (their old password fails the HMAC check).

## Auto-bans

After 3 rejected connections of the same kind (configurable via `violationThreshold`), the player is auto-banned via Valheim's normal ban list. To undo:

1. Server console: `unban <steamid>`
2. Optional: open `BepInEx/config/ServerGuard/conf/violations.yaml` and remove their entry, so the count restarts at zero.

## Migrating from v1.2.x

If you previously used heuristic detection (`ignore_mods.yaml`, `mod_patterns.yaml`):

1. Stop the server.
2. Replace `Valheim-ServerGuard.dll` with the new one.
3. Start the server. The old files are auto-renamed `ignore_mods.yaml.legacy` / `mod_patterns.yaml.legacy`. A fresh `allowed_mods.yaml` is created. The new `sharedSecret` is auto-generated.
4. Have one trusted player install `Valheim-ServerGuard-Client.dll`, copy the password into their `client.yaml`, launch the game once, and use their `mods_for_allowed_mods.yaml` to populate the server's `allowed_mods.yaml`.
5. Distribute the client DLL + password to all other players.

The old YAML files (now `.legacy`) are kept for reference. You can delete them when you're ready.

## Building from source

See [BUILD.md](BUILD.md) for instructions. Both the server DLL and client DLL build from `dotnet build -c Release` after pointing `VALHEIM_PATH` at your Valheim install.

## Architecture map (for code readers)

| File | Role |
|---|---|
| [Plugin.cs](Plugin.cs) | Server plugin. Patches `ZNet.OnNewConnection` (challenge issue + manifest receiver registration) and `ZNet.RPC_PeerInfo` (character-limit enforcement). |
| [ServerGuard.Client/ClientPlugin.cs](ServerGuard.Client/ClientPlugin.cs) | Client companion. Patches `ZNet.OnNewConnection` (registers the manifest reply handler). Builds the manifest from `Chainloader.PluginInfos` and writes `mods_for_allowed_mods.yaml` on first run. |
| [Shared/Manifest.cs](Shared/Manifest.cs) | Shared DTO (used by both): the `ModManifest` shape, the canonical-string format used as input to HMAC, and the HMAC-SHA256 helpers. |

---

**Version:** 1.4.0
**Repository:** https://github.com/yesu0725/Valheim-ServerGuard
