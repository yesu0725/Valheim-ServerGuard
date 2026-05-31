# Installation

ServerGuard has two pieces — the **server mod** and a **client companion**. Both are required.

## Server side (dedicated server only)

1. Make sure BepInEx is already installed on the dedicated server (`denikson-BepInExPack_Valheim`).
2. Install **TaegukGaming-Valheim_ServerGuard** through your usual workflow:
   - Manual: drop `Valheim-ServerGuard.dll` into `BepInEx/plugins/`.
   - Manager: install the Thunderstore package onto the server profile.
3. Start the server. First boot creates:
   - `BepInEx/config/ServerGuard/conf/settings.yaml` — main config, with a freshly-generated `sharedSecret`.
   - `BepInEx/config/ServerGuard/conf/admins.yaml` — empty list. Add your own SteamID to use admin commands.
   - `BepInEx/config/ServerGuard/conf/allowed_mods.yaml` — empty allowlist. You'll populate this in step 5.
4. Note the `sharedSecret` value printed in the server log on the first start:
   ```
   [Info] [ServerGuard] sharedSecret in use (copy to every client.yaml): yFJC+nCWfJdTAfCqlfNRluR0CAlzhRXdGhcFJPEEd38=
   ```
   You'll give this to every player. Treat it like a password — change it (and update all clients) if it leaks.
5. Build your allowlist — see **[Allowed Mods and Modset Fingerprint](Allowed-Mods-and-Modset)**.

## Client side (every player)

1. Install **TaegukGaming-Valheim_ServerGuard_Client** via your mod manager (r2modman / Thunderstore Mod Manager).
2. Launch Valheim once to the main menu, then close. The companion creates `BepInEx/config/ServerGuard/client.yaml`.
3. Open `client.yaml`. Paste in the `sharedSecret` your server host gave you:
   ```yaml
   sharedSecret: "<the value from the server's settings.yaml>"
   ```
4. Save. Restart Valheim. Done — the next time you connect to the ServerGuard server, the companion handles the handshake automatically.

## Self-test

After boot, the server runs a smoke test. If it passes silently, you're good. If it posts to your admin Discord, follow the listed fix. You can re-run on demand with `sg selftest` from the F5 console.

## Updating

Server and client versions must match. When you update one, update the other in the same session.
