# Build and Release

## Build commands

### Build both projects
```
cd "E:\Valheim Modding\Valheim-ServerGuard"
dotnet build Valheim-ServerGuard.csproj -c Release
dotnet build ServerGuard.Client\Valheim-ServerGuard-Client.csproj -c Release
```

Or from Visual Studio / Rider: Build → Build Solution.

### Output locations
```
bin/Release/Valheim-ServerGuard.dll
ServerGuard.Client/bin/Release/Valheim-ServerGuard-Client.dll
```

---

## Auto-copy on build

Both `.csproj` files have a post-build `<Target Name="CopyTo...">` that silently copies the DLL if the destination folder exists.

**Server** → `C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server\BepInEx\plugins`

**Client** → `C:\Users\yesu0725\AppData\Roaming\r2modmanPlus-local\Valheim\profiles\Hearthbound Valheim - Test\BepInEx\plugins\TaegukGaming-Valheim_ServerGuard_Client`

Override at build time with env vars `SERVERGUARD_TEST_SERVER_DIR` or `SERVERGUARD_TEST_CLIENT_DIR`. Safe to commit — the `Condition="Exists(...)"` makes it a no-op on machines without those paths.

---

## Version bump — 6 locations, all must match

When bumping version (e.g. 1.4.0 → 1.5.0), update **all six**:

| File | What to change |
|---|---|
| `Plugin.cs` line 20 | `[BepInPlugin("...", "...", "1.4.0")]` → new version |
| `Plugin.cs` Awake log | `$"[ServerGuard] Loaded (v1.4.0)."` → new version |
| `Plugin.cs` Awake admin post | `$":rocket: **ServerGuard online** v1.4.0"` → new version |
| `ServerGuard.Client/ClientPlugin.cs` | `public const string VERSION = "1.4.0";` → new version |
| `Valheim-ServerGuard.csproj` | `<Version>1.4.0</Version>` → new version |
| `ServerGuard.Client/Valheim-ServerGuard-Client.csproj` | `<Version>1.4.0</Version>` → new version |

Then also update Thunderstore manifests (separate files — see below).

---

## Thunderstore package structure

### Server package
```
Thunderstore files/Valheim-ServerGuard (server)/
├── manifest.json       ← version_number must match plugin version
├── README.md           ← user-facing, non-technical
├── CHANGELOG.md        ← user-facing release notes
├── icon.png            ← 256×256 PNG
└── Valheim-ServerGuard.dll
```

### Client package
```
Thunderstore files/Valheim-ServerGuard (client)/
├── manifest.json       ← version_number must match plugin version
├── README.md
├── CHANGELOG.md
├── icon.png
└── Valheim-ServerGuard-Client.dll
```

### manifest.json format
```json
{
  "name": "Valheim_ServerGuard",
  "version_number": "1.4.0",
  "website_url": "https://github.com/yesu0725/Valheim-ServerGuard",
  "description": "...",
  "dependencies": ["BepInEx-BepInExPack-5.4.2202"]
}
```

---

## Release checklist

1. **Bump version** in all 6 locations above
2. **Build both projects** — verify no errors
3. **Copy DLLs to Thunderstore folders:**
   ```
   copy bin\Release\Valheim-ServerGuard.dll "Thunderstore files\Valheim-ServerGuard (server)\"
   copy ServerGuard.Client\bin\Release\Valheim-ServerGuard-Client.dll "Thunderstore files\Valheim-ServerGuard (client)\"
   ```
4. **Update Thunderstore manifests** — `version_number` in both `manifest.json`
5. **Update CHANGELOGs** — user-friendly, non-technical (readers are mod users, not developers)
6. **Update READMEs** if needed
7. **Zip each package:**
   ```powershell
   $ts = Get-Date -Format "yyyyMMdd_HHmmss"
   $ver = "1.4.0"
   Compress-Archive -Path "Thunderstore files\Valheim-ServerGuard (server)\*" `
       -DestinationPath "Thunderstore files\Valheim-ServerGuard (server)\Valheim-ServerGuard_v${ver}_${ts}.zip"
   Compress-Archive -Path "Thunderstore files\Valheim-ServerGuard (client)\*" `
       -DestinationPath "Thunderstore files\Valheim-ServerGuard (client)\Valheim-ServerGuard-Client_v${ver}_${ts}.zip"
   ```
   **Do NOT include the wiki/ directory in zips.** Wiki files are only for GitHub Wiki.
8. **Commit and push to GitHub**
9. **Upload zips to Thunderstore** manually (Claude cannot do this)

---

## GitHub Wiki

The `wiki/` directory contains 9 markdown files in GitHub Wiki format:
```
wiki/Home.md
wiki/Installation.md
wiki/Configuration.md
wiki/Allowed-Mods-and-Modset.md
wiki/Discord-Integration.md
wiki/Admin-Commands.md
wiki/Anti-Cheat-Features.md
wiki/Forensic-Logs.md
wiki/Troubleshooting.md
```

To publish: clone `<repo>.wiki.git`, copy files in, push. GitHub auto-renders them. `Home.md` becomes the wiki landing page.

---

## Git workflow

Standard main-branch workflow:
```
git add -A
git commit -m "Release vX.Y.Z: <brief summary>
Co-Authored-By: Claude Opus 4.7 <noreply@anthropic.com>"
git push origin main
```

Never force-push to main.

---

## CHANGELOG format guidelines

- Section header: `## v1.4.0 — <date>`
- Audience: mod users (not developers)
- Language: "Added", "Fixed", "Improved" — not technical class/method names
- Keep it short: 5-10 bullet points
- Latest version at the top

## README format guidelines

- Quick overview (1 paragraph)
- Setup steps (numbered, brief)
- Link to GitHub Wiki for details
- AI disclaimer (required in this project's READMEs)
- Hearthbound modpack mention + link
- No wall-of-text technical content
