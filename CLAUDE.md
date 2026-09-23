# Valheim ServerGuard — AI Context File

This file is the entry point for Claude Code sessions on this project.
Read this first, then follow links to sub-files for deep detail.

---

## Project identity

| Item | Value |
|---|---|
| **Current version** | 2.0.2 |
| **GUID** | `com.taeguk.valheim.serverguard` (one plugin, one DLL, both sides — since 2.0) |
| **Legacy client GUID** | `com.taeguk.valheim.serverguard.client` (pre-2.0 companion package; still accepted in `allowed_mods.yaml`) |
| **Target framework** | net462 (Mono, .NET Framework 4.6.2) |
| **BepInEx** | 5.x (5.4.23.5 verified on Valheim 1.0.7) |
| **Game verified against** | Valheim **1.0.7** (network version 39, Unity 6000.0.75), 2026-09-09 |
| **GitHub** | https://github.com/yesu0725/Valheim-ServerGuard |

---

## What this mod does

A single BepInEx mod (`Valheim-ServerGuard.dll`) installed on **both** the dedicated server and every player's client. One entry plugin picks the half to run at load:

- **Entry point** (`ServerGuardPlugin.cs`) — the only `[BepInPlugin]`. Headless process (no graphics device) → attaches `ServerPlugin`; otherwise → attaches `ClientPlugin`. Overridable via `General.Mode` in the BepInEx `.cfg`. Owns `PatchNested`, which applies only the patch classes nested inside the chosen half.
- **Server half** (`ServerPlugin.cs`, `MonoBehaviour`) — runs on the dedicated server. Enforces rules, handles attestation, logs to Discord, exposes `sg` admin console commands, authorises staff dev commands server-side.
- **Client half** (`ClientPlugin.cs`, `MonoBehaviour`) — runs on the player's Valheim client. Signs the mod manifest, gates the console, unlocks dev commands for staff per the server's grant, reports suspicious activity, sends build/death events, draws the Quick Login panel.
- **Shared library** (`Shared/Manifest.cs`, `Shared/CustomsProtocol.cs`, `Shared/CustomsLedger.cs`) — attestation DTO/crypto plus the Unity-free Customs wire model, policy/session engine and durable baseline store.

The two halves never run in the same process. "Companion" in older comments and docs means the client half.

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
│   ├── ban-layer.md
│   ├── console-guard.md
│   ├── privilege-tiers.md
│   ├── discord-routing.md
│   ├── settings-reference.md
│   ├── build-and-release.md
│   ├── known-errors.md
│   └── IMPLEMENTATION_SUMMARY.md
├── ServerGuardPlugin.cs               ← BepInEx entry point: picks server or client half
├── ServerPlugin.cs                    ← server half (~7600 lines; was Plugin.cs)
├── ClientPlugin.cs                    ← client half (~4100 lines; was ServerGuard.Client/ClientPlugin.cs)
├── Shared/
│   ├── Manifest.cs                    ← attestation DTO + crypto
│   ├── CustomsProtocol.cs             ← Customs item model, bounds and RPC framing
│   └── CustomsLedger.cs               ← Customs policy, sessions and baseline store
├── tests/ServerGuard.Tests/           ← 96 Unity-free Customs protocol/ledger tests
├── Valheim-ServerGuard.csproj         ← the one project; builds the one DLL
├── wiki/                              ← GitHub Wiki pages, including Customs.md (not in Thunderstore zip)
└── Thunderstore files/
    └── Valheim-ServerGuard/           ← the one package (server + client)
```

---

## Critical constraints — read before touching code

> Full detail in `claude/mono-constraints.md` and `claude/harmony-patterns.md`

1. **No `ValueTuple` anywhere** — Valheim's Mono runtime doesn't ship `System.ValueTuple`. Any `(T1, T2)` in a compiler-generated closure causes `TypeLoadException` at boot. Use `KeyValuePair<string,string>` or `out` parameters instead.

2. **No `nameof` on protected methods** — fails at compile time. Use string literals (e.g. `"OnDeath"` not `nameof(Player.OnDeath)`).

3. **No `ref bool __result` on void method patches** — Harmony throws `Cannot get result from void method`. Omit `__result` entirely.

4. **No direct reference to `PlatformUserID` or overloads that pull it in** — `Splatform` assembly not referenced. Use reflection for anything touching `ZNetPeer.m_platformUserID`.

5. **RPC handlers MUST be registered before the admin early-return** — otherwise admins can't use `sg` commands. See `Patch_OnNewConnection`.

6. **Valheim's `Console` type needs `global::Console`** — `using System;` is in scope in both halves, so a bare `Console` binds to `System.Console`. Valheim's `Console` sits in the global namespace.

7. **Don't pick a Valheim collection field by "first one of the right interface"** — `Terminal` has three static dictionaries and `m_testList` is declared before `commands`. Match on the generic argument types instead. See `ResolveTerminalCommands` in `ClientPlugin.cs`.

8. **Never call `Harmony.PatchAll()` in either half** — it sweeps the whole assembly and applies the *other* half's patches too. Put every patch class inside `ServerPlugin` or `ClientPlugin` (nested, any depth) and let `ServerGuardPlugin.PatchNested` apply it. A top-level patch class would be applied by nobody.

9. **`ServerPlugin` and `ClientPlugin` are plain `MonoBehaviour`s, not `BaseUnityPlugin`** — there is no `Logger`/`Config`/`Info` on them. Log through the static `LogS` (assigned from `ServerGuardPlugin.Log`); the version is `ServerGuardPlugin.VERSION`.

---

## Sub-file index

| File | When to read |
|---|---|
| [`claude/architecture.md`](claude/architecture.md) | How the entry plugin picks a half, how the two halves communicate, BepInEx/Harmony lifecycle |
| [`claude/mono-constraints.md`](claude/mono-constraints.md) | Before writing any new code — list of things that will crash at runtime |
| [`claude/harmony-patterns.md`](claude/harmony-patterns.md) | Before adding or modifying any Harmony patch |
| [`claude/rpc-protocol.md`](claude/rpc-protocol.md) | Adding a new server↔client message, payload format |
| [`claude/features-and-rules.md`](claude/features-and-rules.md) | All anti-cheat rules and Customs, their defaults, trust boundaries and enable flags |
| [`claude/ban-layer.md`](claude/ban-layer.md) | The SteamID denylist — where it hooks the handshake, `bans.yaml`, how it relates to `banlist.txt` |
| [`claude/console-guard.md`](claude/console-guard.md) | Console command gating, key-bind purging, staff dev commands, and the per-command risk assessment |
| [`claude/privilege-tiers.md`](claude/privilege-tiers.md) | Owner / moderator / player tiers, and every site the owner bypass is enforced |
| [`claude/discord-routing.md`](claude/discord-routing.md) | Adding a new Discord post or changing what channel something routes to |
| [`claude/settings-reference.md`](claude/settings-reference.md) | Adding a new setting, understanding all current settings (including Customs modes and timing) |
| [`claude/build-and-release.md`](claude/build-and-release.md) | Building, bumping version, releasing to Thunderstore and GitHub |
| [`claude/known-errors.md`](claude/known-errors.md) | Debugging — every error hit in this project and its fix |
| [`claude/IMPLEMENTATION_SUMMARY.md`](claude/IMPLEMENTATION_SUMMARY.md) | High-level feature and code structure overview — good first read for a new session |
