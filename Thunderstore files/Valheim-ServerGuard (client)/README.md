# Valheim ServerGuard — Client

Companion plugin for **Valheim ServerGuard**. Install this if you're joining a server that uses ServerGuard. Without it, the server kicks you on connect.

This is the *client-side* half of the system. The *server-side* mod (`Valheim_ServerGuard`) is installed by the server host, not by you.

## What it does

When you connect to a ServerGuard server, this plugin tells the server which mods you have loaded so it can decide whether to let you in. While you play, it also:
- Blocks cheat console commands (`devcommands`, `god`, `fly`, etc.) on multiplayer servers — single-player and host-and-play keep full cheats.
- Blocks the emote attack-cancel exploit. Sheathing your weapon is untouched — swapping, looting and building all work normally mid-swing.
- Reports your deaths, shouts, and built / destroyed pieces to the server's logs.
- Reports your skill levels for cap enforcement.
- Removes server-configured cheat items from your inventory on login (non-admins).
- Suppresses the automatic "I have arrived!" shout on first spawn, if the server asks for it. You can still shout manually.

You don't need to do anything in-game — it runs silently in the background.

## Optional: Quick Login panel

The companion can add a **one-click login panel to the title screen** so you (or your community) can join a specific server without the server browser, IP entry, or password prompt. It shows the server logo, name, description, and a live player count.

It's **off by default**. To enable it, edit `BepInEx/config/ServerGuard/client.yaml`:

```yaml
quickLoginEnabled: true
serverAddress: "my.server.com"   # or an IP
serverPort: 2456
serverPassword: ""               # if the server has one
serverName: "My Server"
serverDescription: "Welcome!"
serverLogoPath: "logo.png"       # PNG/JPG placed in BepInEx/config/ServerGuard/
```

Click **Connect** → pick a character → you're in. (Or click **Start Game**, pick a character, then **Connect** — the panel stays visible on the character screen.)

## Quick setup

1. Install via your mod manager (r2modman / Thunderstore Mod Manager).
2. Launch Valheim **once** to the main menu, then close the game.
3. Open `BepInEx/config/ServerGuard/client.yaml` and paste in the password your server host gave you:
   ```yaml
   sharedSecret: "<paste here>"
   ```
4. Save. Restart Valheim. You're done.

> The password is unique to each server. Ask the server host — they'll find it in their server's `BepInEx/config/ServerGuard/conf/settings.yaml` on the `sharedSecret:` line.

## Documentation

For setup, troubleshooting, and admin-command details see the [GitHub Wiki](https://github.com/yesu0725/Valheim-ServerGuard/wiki).

## Try it out

This mod was built for the **TaegukGaming community server** running the **Hearthbound modpack**. If you want to see it in action:

🏰 **[Hearthbound Valheim Modpack](https://thunderstore.io/c/valheim/p/TaegukGaming/Hearthbound_Valheim_Modpack/)**

## Disclaimer

This mod is **created using AI**. No other mods were copied during the process. All feature ideas come from the uploader and are mainly to cater the needs of the **TaegukGaming community server**. If any features or ideas look similar to other mods, these are not intentional.

This mod is **free to use as is**. Voluntary support is appreciated.

---

**Version:** 1.6.3
**Source / issues / wiki:** https://github.com/yesu0725/Valheim-ServerGuard
**Server-side package:** `TaegukGaming-Valheim_ServerGuard`
