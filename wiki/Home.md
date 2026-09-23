# Valheim ServerGuard — Wiki

A Valheim mod that locks a dedicated server to a specific modpack, provides anti-cheat enforcement, dev commands for staff, Discord logging, admin tools, and forensic logs. One mod, installed on the server **and** on every player's game — it runs the server half or the client half depending on where it loads.

## Topics

- **[Installation](Installation)** — get the server + client running in 5 minutes.
- **[Configuration](Configuration)** — every `settings.yaml` option explained.
- **[Allowed Mods and Modset Fingerprint](Allowed-Mods-and-Modset)** — how to build your allowlist and verify everyone has the same modpack.
- **[Discord Integration](Discord-Integration)** — public + admin channels, raid alerts, shouts, and the daily summary.
- **[Admin Commands](Admin-Commands)** — full `sg` console reference.
- **[Anti-Cheat Features](Anti-Cheat-Features)** — every rule, how it works, how to tune it, and the violations system.
- **[Customs](Customs)** — optional inventory baseline: catch characters that come back carrying items they did not get on your server.
- **[Bans and Console Guard](Bans-and-Console-Guard)** — instant SteamID bans, console command gating, and key-bind removal.
- **[Privilege Tiers](Privilege-Tiers)** — owner vs moderator vs player, what each one bypasses, and which dev commands each tier gets.
- **[Forensic Logs](Forensic-Logs)** — death log + build/destroy heatmap CSVs.
- **[Quick Login Panel](Quick-Login)** — optional one-click title-screen join for your community, with a scrollable announcements box and clickable links (client-side).
- **[Troubleshooting](Troubleshooting)** — common issues and how to fix them.

## What this mod is

A toolkit for running a curated-modpack server: keep the wrong people out, catch the most common cheats, log what happened so you can investigate grief reports, and give moderators in-game admin commands.

## What it isn't

- Not a server-side framework — it's a single mod that happens to have a server half and a client half.
- Not a full anti-cheat suite — it catches the *common* cheats. Sophisticated cheats may slip through.
- Not a replacement for a code-of-conduct or active moderation. It's a force multiplier for admins.

## Versioning

Current version: **2.0.2**. Since 2.0 there is one package for both sides, so keep the server and your modpack on the same version. Pre-2.0 servers and clients used two packages (`Valheim_ServerGuard` + `Valheim_ServerGuard_Client`); see [Installation](Installation) for the upgrade note.

**Valheim 1.0:** ServerGuard is verified against Valheim 1.0.7 on BepInEx 5.4.23.5.

## Try it out

Built for the **TaegukGaming community server** running the **[Hearthbound modpack](https://thunderstore.io/c/valheim/p/TaegukGaming/Hearthbound_Valheim_Modpack/)**.
