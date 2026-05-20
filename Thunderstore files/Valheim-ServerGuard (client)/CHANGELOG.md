# Changelog

Versions track the server-side ServerGuard plugin on GitHub.

## 1.3.0

First release of the companion plugin. Required on every client connecting to a server that runs Valheim ServerGuard 1.3+.

### What it does
- On connect, the server sends a one-time random challenge.
- This plugin enumerates every loaded BepInEx plugin via `Chainloader.PluginInfos`, computes a SHA-256 hash of each DLL, and packages them into a `ModManifest` (schema version, the challenge, a timestamp, and the mod list).
- The manifest is signed with HMAC-SHA256 using the `sharedSecret` from `BepInEx/config/ServerGuard/client.yaml`, then sent back to the server.
- The server validates the signature, the timestamp window, and matches every mod against its allowlist.

### Configuration

After installing this DLL, launch Valheim once. It will create `BepInEx/config/ServerGuard/client.yaml`. Paste the server's shared password into it:

```yaml
sharedSecret: "<the value from the server's settings.yaml>"
```

### Convenience features
- **First-run mod export.** On first launch the plugin writes `BepInEx/config/ServerGuard/mods_for_allowed_mods.yaml` — a ready-to-paste YAML snippet listing every loaded plugin (GUID-keyed, hash-pinned, sorted by display name). Paste its contents into the server's `allowed_mods.yaml` to bootstrap the allowlist.
- **Deferred plugin enumeration.** The first-run export waits past BepInEx's chainloader before counting plugins, so even mods that load after this companion are included.
- **Just-in-time manifest rebuild.** The manifest is rebuilt on every server request, not cached at startup. Late-loading mods are still reported correctly.

### Dependencies
- BepInEx (denikson-BepInExPack_Valheim)
- YamlDotNet
- Newtonsoft.Json
