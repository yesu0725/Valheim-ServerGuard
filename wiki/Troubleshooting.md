# Troubleshooting

Common issues and how to fix them.

## "I get kicked immediately when I try to join"

Look at the kick message — it tells you what's wrong:

| Kick reason | Cause | Fix |
|---|---|---|
| "Missing required companion plugin" | You don't have the client mod installed. | Install **TaegukGaming-Valheim_ServerGuard_Client** via your mod manager. |
| "wrong password" | Your `client.yaml` has the wrong `sharedSecret`. | Get the password from the server host and paste it exactly. |
| "Required mod missing: …" | The server requires a mod you don't have. | Install the mod listed. |
| "had a mod that isn't allowed (…)" | You have a mod the server doesn't allow. | Remove the mod, or ask the host to add it to `allowed_mods.yaml`. |
| "had a banned mod (…)" | You have a mod the server explicitly bans. | Remove that mod. |
| "mod file doesn't match the server's copy (…)" | You have the right mod but a different version. | Reinstall the version the host uses. The host can publish the modset fingerprint (e.g. `8ce8906e`) — yours must match. |
| "system clock too far off" | Your computer's clock is more than 2 minutes off real UTC. | Sync your system clock (`w32tm /resync` on Windows). |

## "I'm an admin but `sg` commands don't reply"

1. Verify your SteamID is in the server's `BepInEx/config/ServerGuard/conf/admins.yaml` — *exact* number, one per line.
2. Save the file. The server hot-reloads within ~1 second; you should see `admins.yaml reloaded` in the admin Discord channel (if configured).
3. Reconnect to the server. RPC handlers are registered per peer at connection time.
4. Press **F5** to open the console (NOT the chat).
5. Type `sg help` and press Enter.

If you're typing in chat instead of the console, the command will broadcast as a normal chat message and won't trigger anything. **F5 console only.**

## "My admin webhook isn't receiving anything"

1. Confirm `discordWebhookUrlAdmin` is set in `settings.yaml` (not just `discordWebhookUrl`).
2. Save — the server hot-reloads. You should see `Admin Discord channel armed` in the server log.
3. Run `sg selftest` to trigger an admin Discord post; it'll fire if any check fails.
4. Run `sg reload` — that posts a `🔄 settings.yaml reloaded` message you can use to verify the wiring.

If still nothing: check the URL syntax. Should start with `https://discord.com/api/webhooks/` and have an ID + token path. Run `sg selftest` to validate.

## "The public webhook is too noisy with admin events"

By design, **admin events don't go to the public channel** in 1.4.0+. If you're still seeing them: confirm the admin's SteamID is in `admins.yaml`. ServerGuard routes by admin status at post time.

## "Build log is missing destroys for pieces I hammered/attacked"

You're probably on an older version. Update to **1.4.0** — earlier builds only captured destruction of server-owned pieces. The companion now also reports destructions of client-owned pieces (most pieces near a player).

If you're on 1.4.0+ and still missing entries:
- Confirm `enableBuildLog: true` in `settings.yaml`.
- Confirm the companion is loaded on your client (the companion is what sends destroy events).
- Try a different piece — some decorative items don't have a `WearNTear` component and can't be tracked.

## "Player gets kicked but the public Discord says nothing"

Two possibilities:
1. The kicked player is an admin — their kick events route to the admin channel.
2. `discordWebhookUrl` isn't set or is wrong.

Run `sg selftest`. The webhook URL syntax check will fail if it's malformed.

## "My modset fingerprint differs from the server's"

Check your client log on startup:

```
[ServerGuard.Client] Modset fingerprint  loose=<short>  strict=<short>
```

Compare to the server's `BepInEx/config/ServerGuard/modset_fingerprint.txt` (or the value the host publishes). If `loose` differs, you have the wrong *set* of mods. If `loose` matches but `strict` differs, you have the right mods but different versions.

## "Speed-hack false positives on a horse / lox / wolf mount"

Modded mounts can legitimately exceed the default 15 m/s threshold. Raise:

```yaml
speedCheckMaxMetersPerSecond: 25.0
```

Or set `countAsViolation.SpeedHack: false` to keep the log/post but skip auto-ban escalation.

## "I can't see admin posts but the server log shows them"

You probably hit the curated-channel design: only specific events route to admin Discord. The full log mirror is opt-in:

```yaml
discordVerboseMirror: true
```

This is noisy — use only for debugging.

## "`HashMismatch` keeps firing on mod X"

Your `allowed_mods.yaml` has a pinned hash that doesn't match the current DLL. Either:
- Update the hash: regenerate by deleting `mods_for_allowed_mods.yaml` on a client and rerunning Valheim, then copy the new entry.
- Loosen the entry: change `<GUID>|<sha256>` to just `<GUID>` (no hash pin).

## "The `sg whois` command says ambiguous"

You queried by a character name and multiple SteamIDs registered that name. Use the literal SteamID instead.

## "Self-test on boot keeps failing on `Public webhook URL`"

The URL is malformed. Discord webhook URLs look like:

```
https://discord.com/api/webhooks/<id>/<token>
```

Both `<id>` (numeric) and `<token>` (long random string) are required. Check for typos, trailing spaces, accidental quotes.

## Got an issue not listed?

Open an issue at https://github.com/yesu0725/Valheim-ServerGuard/issues with:
- Your `settings.yaml` (with the `sharedSecret` redacted).
- The relevant section of `BepInEx/LogOutput.log`.
- A short description of what you tried and what you expected.
