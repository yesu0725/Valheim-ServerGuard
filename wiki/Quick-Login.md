# Quick Login Panel

The companion plugin (`Valheim_ServerGuard_Client`) can add an optional **one-click login panel** to the Valheim title screen. It lets players join a specific server without the server browser, without typing an IP, and without a password prompt.

This is a **client-side** feature configured in `client.yaml`. It is **off by default** and has no effect on the server.

## What it looks like

A panel in the upper-right of the main menu showing:

- The server **logo** (an image you provide)
- The server **name** (heading)
- A short **description**
- A **live player count** (queried while the player sits on the menu)
- A **Connect** button styled like the game's own buttons

## Enabling it

Edit `BepInEx/config/ServerGuard/client.yaml`:

```yaml
quickLoginEnabled: true
serverAddress: "my.server.com"   # hostname or IP
serverPort: 2456                  # the game port (default 2456)
serverPassword: ""                # the server password, if any
serverName: "My Server"
serverDescription: "Welcome to the server!"
serverLogoPath: "logo.png"        # image file in BepInEx/config/ServerGuard/
```

Restart Valheim. The panel appears on the title screen.

## The logo image

- **Format:** PNG (recommended — supports transparency) or JPG. Other formats are not supported.
- **Size:** any resolution; it is scaled to fit roughly **300 × 120 px**, preserving aspect ratio. A landscape image around **512 × 256** works well. Keep it reasonable (≤ 1024 px) to avoid wasted memory.
- **Location:** put the file directly in `BepInEx/config/ServerGuard/` and set `serverLogoPath` to just the filename (e.g. `logo.png`).

## How connecting works

There are two flows, both of which skip the IP/password prompts:

1. **Connect → Character Select → Start** — click **Connect** on the panel. The main menu hides and you go to character selection. Pick (or create) a character and confirm; you connect straight to the configured server.
2. **Start Game → Character Select → Connect** — click the vanilla **Start Game** first. The panel stays visible on the character screen. Pick a character, then click **Connect** to join immediately.

The password (if set) is applied automatically, so the in-game password dialog never appears.

## Notes & troubleshooting

- If the logo doesn't appear, check the BepInEx log for `Logo file not found` or `unsupported image` warnings, and confirm the file is a PNG/JPG in `BepInEx/config/ServerGuard/`.
- The live player count uses a standard Steam server query (A2S_INFO) sent to the **query port**, which is your game port **+ 1** — so `2457` for a default `2456` server. If your host or firewall blocks UDP on that port, the panel shows `Players: ?`. Check the BepInEx log for `Player-count query to <host>:<port> got no answer.` to confirm which port was tried.
  - *Fixed in 1.6.1:* before that version the count always showed `?`, because the query didn't answer the challenge packet Valve's query protocol requires. If you're on 1.6.0 or earlier, update the companion plugin.
- The panel only shows on the title screen; it disappears once you're in-game.
- Disabling is as simple as setting `quickLoginEnabled: false` (or leaving the address blank).

See also: **[Installation](Installation)**, **[Configuration](Configuration)**.
