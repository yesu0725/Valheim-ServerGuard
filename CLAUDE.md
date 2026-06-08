# Valheim ServerGuard — AI Context File

This file is the entry point for Claude Code sessions on this project.
Read this first, then follow links to sub-files for deep detail.

---

## Project identity

| Item | Value |
|---|---|
| **Current version** | 1.4.0 |
| **Server GUID** | `com.taeguk.valheim.serverguard` |
| **Client GUID** | `com.taeguk.valheim.serverguard.client` |
| **Target framework** | net462 (Mono, .NET Framework 4.6.2) |
| **BepInEx** | 5.x |
| **GitHub** | https://github.com/yesu0725/Valheim-ServerGuard |

---

## What this mod does

A two-part BepInEx mod for Valheim dedicated servers:

- **Server plugin** (`Plugin.cs`) — runs on the dedicated server. Enforces rules, handles attestation, logs to Discord, exposes `sg` admin console commands.
- **Client plugin** (`ClientPlugin.cs`) — runs on the player's Valheim client. Signs the mod manifest, blocks devcommands, reports suspicious activity, sends build/death events.
- **Shared library** (`Shared/Manifest.cs`) — compiled into both. Contains `ModManifest`, `ModManifestEntry`, `ModsetFingerprint`.

---

## Repository layout

```
Valheim-ServerGuard/
├── CLAUDE.md                          ← this file
├── claude/                            ← AI context sub-files
│   ├── architecture.md
│   ├── mono-constraints.md
│   ├── harmony-patterns.md
│   ├── rpc-protocol.md
│   ├── features-and-rules.md
│   ├── discord-routing.md
│   ├── settings-reference.md
│   ├── build-and-release.md
│   └── known-errors.md
├── Plugin.cs                          ← server plugin (~4100 lines)
├── ServerGuard.Client/
│   └── ClientPlugin.cs                ← client plugin (~1340 lines)
├── Shared/
│   └── Manifest.cs                    ← shared DTO + crypto
├── Valheim-ServerGuard.csproj
├── ServerGuard.Client/Valheim-ServerGuard-Client.csproj
├── wiki/                              ← GitHub Wiki pages (not in Thunderstore zip)
└── Thunderstore files/
    ├── Valheim-ServerGuard (server)/
    └── Valheim-ServerGuard (client)/
```

---

## Critical constraints — read before touching code

> Full detail in `claude/mono-constraints.md` and `claude/harmony-patterns.md`

1. **No `ValueTuple` anywhere** — Valheim's Mono runtime doesn't ship `System.ValueTuple`. Any `(T1, T2)` in a compiler-generated closure causes `TypeLoadException` at boot. Use `KeyValuePair<string,string>` or `out` parameters instead.

2. **No `nameof` on protected methods** — fails at compile time. Use string literals (e.g. `"OnDeath"` not `nameof(Player.OnDeath)`).

3. **No `ref bool __result` on void method patches** — Harmony throws `Cannot get result from void method`. Omit `__result` entirely.

4. **No direct reference to `PlatformUserID` or overloads that pull it in** — `Splatform` assembly not referenced. Use reflection for anything touching `ZNetPeer.m_platformUserID`.

5. **RPC handlers MUST be registered before the admin early-return** — otherwise admins can't use `sg` commands. See `Patch_OnNewConnection`.

---

## Sub-file index

| File | When to read |
|---|---|
| [`claude/architecture.md`](claude/architecture.md) | Understanding how the two plugins communicate, BepInEx/Harmony lifecycle |
| [`claude/mono-constraints.md`](claude/mono-constraints.md) | Before writing any new code — list of things that will crash at runtime |
| [`claude/harmony-patterns.md`](claude/harmony-patterns.md) | Before adding or modifying any Harmony patch |
| [`claude/rpc-protocol.md`](claude/rpc-protocol.md) | Adding a new server↔client message, payload format |
| [`claude/features-and-rules.md`](claude/features-and-rules.md) | All anti-cheat rules, their constants, defaults, and enable flags |
| [`claude/discord-routing.md`](claude/discord-routing.md) | Adding a new Discord post or changing what channel something routes to |
| [`claude/settings-reference.md`](claude/settings-reference.md) | Adding a new setting, understanding all current settings |
| [`claude/build-and-release.md`](claude/build-and-release.md) | Building, bumping version, releasing to Thunderstore and GitHub |
| [`claude/known-errors.md`](claude/known-errors.md) | Debugging — every error hit in this project and its fix |
