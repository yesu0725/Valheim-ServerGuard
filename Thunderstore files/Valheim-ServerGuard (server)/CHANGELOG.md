# Changelog

Versions follow the plugin's internal version (`Plugin.cs` → `[BepInPlugin]`) on GitHub.

## 1.3.0

First release of the client-attestation architecture. The previous heuristic detection (RPC token sniffing, assembly namespace scanning, version-keyword matching) was unreliable — Phase 2 in particular was scanning the *server's* own AppDomain, not the client's, and produced a 100% false-positive rate. v1.3 replaces all of it.

### How it works now
- Every player runs a small companion plugin (`Valheim-ServerGuard-Client.dll`) that builds a list of their loaded BepInEx plugins (GUID + name + version + SHA-256 hash).
- The server challenges each connecting peer for that list, HMAC-SHA256 signed with a shared secret to prevent forgery and replay attacks.
- The server validates the signature, checks every mod against `allowed_mods.yaml` (`required_mods` / `allowed_mods` / `banned_mods`), and admits or kicks accordingly.
- Vanilla / wrong-modpack clients don't have the companion plugin → no manifest arrives → kicked on timeout.

### What's new
- **Auto-generated shared secret.** On first launch the server mints a 256-bit base64 password and writes it into `settings.yaml`. Upgrading from a config with an empty `sharedSecret` also self-heals — a fresh value is generated, written back, and logged so the operator can copy it to each client's `client.yaml`.
- **Companion-side `mods_for_allowed_mods.yaml` export.** On first launch the client plugin enumerates every loaded mod and writes a ready-to-paste YAML snippet (GUID-keyed, optionally hash-pinned, sorted by display name) at `BepInEx/config/ServerGuard/mods_for_allowed_mods.yaml`. Drop its contents into the server's `allowed_mods.yaml` to bootstrap the allowlist.
- **Deferred client manifest build.** The companion now waits past BepInEx's chainloader before enumerating plugins, so even mods that load after ServerGuard.Client are included. The manifest is also rebuilt on every server request to handle late-loading mods.
- **GUID-keyed allowlist with optional SHA-256 hash pinning.** Entries can be `GUID`, `GUID|sha256` (pin a specific DLL), or display-name fallback.
- **Discord & log messages identify players by character name.** Every line and webhook event now shows `CharacterName (SteamID)` instead of bare SteamIDs, sourced from `registrations.yaml`. Brand-new Steam IDs appear as `NewPlayer (id)` until they pick a character; multi-character Steam IDs are listed comma-separated.
- **`characterLimit` setting.** Caps how many distinct character names a single Steam ID can use on this server (default 1).

### Bug fixes in 1.3
- `allowed_mods.yaml` no longer parses as empty. The YAML deserializer's camelCase convention had been mangling the snake_case keys (`required_mods`, `allowed_mods`, `banned_mods`) before lookup, so zero entries matched. Fixed via explicit `[YamlMember(Alias = …)]` annotations.
- Kicks now actually disconnect the player. The previous reflection-based `Kick(ZNetPeer)` resolved to a Valheim method that soft-queued the disconnect; the handshake outpaced it. Now uses `ZNet.Disconnect(peer)` directly (the path Valheim's console `kick` uses).

### Removed / deprecated
- `mod_patterns.yaml`, `ignore_mods.yaml`, `enableAssemblyScanning`, `useWhitelistMode`, `aggressiveNoModCheck`, `requireAttestation` — gone. Old files are auto-renamed to `.legacy` on first launch; deprecated settings are silently ignored.

## 1.2.0 and earlier

Internal preview builds with heuristic detection. Not published on Thunderstore.
