# Valheim ServerGuard

A single mod that handles the messy parts of running a dedicated Valheim server with a curated modpack: mod allowlist, anti-cheat, moderation tools, Discord integration, and forensic logging — all configurable from YAML.

## What it does (in plain English)

- **Locks your server to a specific modpack.** Players running the wrong mods are kicked at the door.
- **Blocks common cheats.** `devcommands` / `god` / `fly` / `spawn` and other console cheats are silently neutered. Emote attack-cancel exploit is blocked. Suspicious movement speed and skill levels are flagged.
- **Sends events to Discord.** Public channel for player events (joined / kicked / died / **shouts** / **raid alerts**). Optional admin channel for moderation events (violations / config reloads / daily summary).
- **Announces raids by their real name.** Random-event raids are posted to Discord using the in-game event name (e.g. "The Horde Is Attacking") with coordinates, plus pause/resume/end updates.
- **Strips cheat items on login.** Configured items (`SwordCheat`, `SledgeCheat` by default) are removed from non-admin players' inventories when they join.
- **Can force everyone onto the map.** Optionally override each player's "public position" toggle so all players are permanently visible on each other's maps. Enforced server-side; admins can be exempted.
- **Can mute the "I have arrived!" shout.** Optional — useful when the server already posts login notifications.
- **Provides admin commands in the game console.** Open the F5 console, type `sg help`. Kick, pardon, query the build log, hot-reload config — without leaving the game.
- **Records build / destroy events to CSV.** Useful when investigating grief reports.

## Quick setup

1. Install this mod on your **dedicated server**.
2. Install **Valheim_ServerGuard_Client** on every player's machine.
3. Launch the server. It writes `BepInEx/config/ServerGuard/conf/settings.yaml` with a random `sharedSecret`.
4. Copy that `sharedSecret` value. Each player pastes it into their `BepInEx/config/ServerGuard/client.yaml`.
5. Add your modpack to `BepInEx/config/ServerGuard/conf/allowed_mods.yaml`. The companion plugin generates a ready-to-paste snippet at `mods_for_allowed_mods.yaml` after a player runs Valheim once.

That's the minimum. Everything else is optional.

## Documentation

For full configuration, admin commands, and feature details see the [GitHub Wiki](https://github.com/yesu0725/Valheim-ServerGuard/wiki).

## Try it out

This mod was built for the **TaegukGaming community server** running the **Hearthbound modpack**. If you want to see it in action, check out the modpack:

🏰 **[Hearthbound Valheim Modpack](https://thunderstore.io/c/valheim/p/TaegukGaming/Hearthbound_Valheim_Modpack/)**

## Disclaimer

This mod is **created using AI**. No other mods were copied during the process. All feature ideas come from the uploader and are mainly to cater the needs of the **TaegukGaming community server**. If any features or ideas look similar to other mods, these are not intentional.

This mod is **free to use as is**. Voluntary support is appreciated.

---

**Version:** 1.7.0
**Source / issues / wiki:** https://github.com/yesu0725/Valheim-ServerGuard
**Companion (required on every client):** `TaegukGaming-Valheim_ServerGuard_Client`
