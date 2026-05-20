# Valheim ServerGuard — Client

Companion plugin for **Valheim ServerGuard**. Install this if you're joining a server that uses ServerGuard. Without it, the server kicks you on connect.

This is the *client-side* half of the system. The *server-side* half (`Valheim-ServerGuard`) is a separate Thunderstore package installed by the server host, not by you.

---

## What it does

When you connect to a ServerGuard-protected server, this plugin tells the server which BepInEx mods you have loaded. The server checks your mod list against its allowlist and decides whether to let you in. Your reply is cryptographically signed with a password the server gives you, so it can't be faked.

You don't need to do anything in-game — the plugin runs silently during the connection handshake.

---

## Install

1. Install this mod via your usual mod manager (r2modman / Thunderstore Mod Manager).
2. Launch Valheim **once** to the main menu, then close the game.
3. Open `BepInEx/config/ServerGuard/client.yaml` and paste in the password your server host gave you:
   ```yaml
   sharedSecret: "<paste here>"
   ```
4. Save. Restart Valheim. You're done.

> **Where do I get the password?** From the person running the server. They'll find it in their server's `BepInEx/config/ServerGuard/conf/settings.yaml`, on the `sharedSecret:` line. The value must match exactly.

---

## How do I know it's working?

Open `BepInEx/LogOutput.log` in your Valheim profile folder. Near the top, find a line like:

```
[Info: Valheim ServerGuard Client] [ServerGuard.Client] Loaded v1.3.0. Manifest entries: 29. HMAC: ON
```

- `HMAC: ON` means your `client.yaml` has a password set. If it says `OFF`, go back to step 3.
- `Manifest entries:` shows how many of your installed BepInEx plugins were enumerated.

When you actually connect to the server, you should see further down the log:

```
[Info: Valheim ServerGuard Client] [ServerGuard.Client] Registered manifest request handler on server peer.
[Info: Valheim ServerGuard Client] [ServerGuard.Client] Sent manifest (1700 bytes, 29 mods).
```

If the server's allowlist is set up correctly, you're in.

---

## Common kick messages

The server log shows the exact reason. The most common ones a client cares about:

| Server says | What it means | Fix |
|---|---|---|
| `Missing required companion plugin: ServerGuard.Client` | This plugin isn't loaded on your end. | Verify it's in `BepInEx/plugins/`. Check `LogOutput.log` for `Loading [Valheim ServerGuard Client …]`. |
| `Invalid signature` | Your `sharedSecret` doesn't match the server's, or your system clock is more than 2 minutes off. | Copy the password again, carefully. Check `w32tm /query /status` for clock drift on Windows. |
| `Unapproved mod: <name>` | One of your mods isn't in the server's allowlist. | Remove the mod, or ask the server host to add it. |
| `Required mod missing: <guid>` | You're missing a mod the server requires. | Install the mod listed. |

---

## Bonus: contribute the server's allowlist

On first launch this plugin creates a file at:

```
BepInEx/config/ServerGuard/mods_for_allowed_mods.yaml
```

It contains every mod you have loaded, formatted as a ready-to-paste YAML snippet for the server's `allowed_mods.yaml`. If you're the modpack curator setting up the server, hand that file to the server host and they can paste it directly into their config.

To refresh it after adding mods to your modpack: delete the file and launch Valheim once. It regenerates with your current mod list.

---

**Version:** 1.3.0
**Repository:** https://github.com/yesu0725/Valheim-ServerGuard
**Server-side package:** `Valheim_ServerGuard` (Thunderstore)
