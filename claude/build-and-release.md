# Build and Release

## Build commands

### Build (one project, one DLL — since 2.0)
```
cd "E:\Valheim Modding\Valheim-ServerGuard"
dotnet build Valheim-ServerGuard.csproj -c Release
```

Or from Visual Studio / Rider: Build → Build Solution. The client half compiles into
the same assembly; `ServerGuard.Client/` and its csproj no longer exist.

### Output location
```
bin/Release/Valheim-ServerGuard.dll      ← installed unchanged on server AND client
```

---

## Auto-copy on build

The csproj has one post-build target, `CopyToTestInstalls`, that silently copies the
DLL to each destination that exists.

**Server** → `C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server\BepInEx\plugins\TaegukGaming-Valheim_ServerGuard`

**Client** → `C:\Users\yesu0725\AppData\Roaming\com.kesomannen.gale\valheim\profiles\HB Test\BepInEx\plugins\TaegukGaming-Valheim_ServerGuard`

**HB Test is the only Gale profile that ever receives a build.** The other profiles
(`HB Modpack Ref`, `Hearthbound - Admin`, `Hearthbound Valheim`, `TG Mods Only`) stay on
published releases — never add them to the target or copy there by hand. The target
creates the `TaegukGaming-Valheim_ServerGuard` folder in HB Test if it is missing.

The client destination is the **merged** package folder (no `_Client` suffix). The
target also emits a build *warning* if the pre-2.0 `TaegukGaming-Valheim_ServerGuard_Client`
folder still holds `Valheim-ServerGuard-Client.dll` in that profile — the old companion
must be uninstalled, or both would run.

The client test profile is managed by **[Gale](https://thunderstore.io/c/valheim/p/Kesomannen/GaleModManager/)**, not r2modman — hence the `com.kesomannen.gale` app-data root and the `HB Test` profile name. The old r2modman profile (`r2modmanPlus-local\...\Hearthbound Valheim - Test`) is no longer the test target; if it still exists on disk it is stale and gets no new builds.

> **Both paths must end in the `TaegukGaming-*` mod subfolder, never the `BepInEx\plugins` root.** Both mod managers install the mod into that subfolder, BepInEx scans `plugins` recursively, and a second copy sitting at the root loads the plugin twice — the duplicate GUID then makes one instance fail to load. This bit the server target before 1.6.2.

Override at build time with env vars `SERVERGUARD_TEST_SERVER_DIR` or `SERVERGUARD_TEST_CLIENT_DIR`. Safe to commit — the `Condition="Exists(...)"` makes it a no-op on machines without those paths, and prints `Skipped client-DLL copy (path missing): <path>` so a renamed profile is visible in the build log rather than silently stale.

---

## Version bump — every site, all must match

One mod, one version. Thunderstore rejects a re-upload of an already-published
`version_number`, so never reuse one.

### Source

| File | What to change |
|---|---|
| `ServerGuardPlugin.cs` | `public const string VERSION = "x.y.z";` — **the only version literal in code.** `ServerPlugin` (the `Loaded (vX)` log, the `ServerGuard online vX` post, the settings.yaml header, `sg status`) and `ClientPlugin.VERSION` all read it. |
| `Valheim-ServerGuard.csproj` | `<Version>x.y.z</Version>` |
| `README.md` | `**Version:** x.y.z` footer + a new `### New in x.y.z` / `### Fixed in x.y.z` section at the top |
| `CLAUDE.md` | `| **Current version** | x.y.z |` |
| `claude/IMPLEMENTATION_SUMMARY.md` | New `**x.y.z**` entry in the Version section |
| `wiki/Home.md` | the version line |
| `wiki/Discord-Integration.md` | the `ServerGuard online vx.y.z` example line |

### Thunderstore files

| File | What to change |
|---|---|
| `manifest.json` | `"version_number": "x.y.z"` |
| `README.md` | `**Version:** x.y.z` footer |
| `CHANGELOG.md` | New `## x.y.z` section at the **top** — never rename a previous heading |

After bumping, **re-grep the old version** and confirm every remaining hit is
historical prose (`"Fixed in 1.6.1"`, `"renamed in 1.7.0"`, changelog headings).
Do not rewrite those.

---

## Thunderstore package structure

### The one package (since 2.0)
```
Thunderstore files/Valheim-ServerGuard/
├── manifest.json       ← version_number must match plugin version; name "Valheim_ServerGuard"
├── README.md           ← user-facing, non-technical
├── CHANGELOG.md        ← user-facing release notes
├── icon.png            ← 256×256 PNG
└── Valheim-ServerGuard.dll
```

The pre-2.0 `Valheim_ServerGuard_Client` package is retired. Its folder was removed
from the repo in 2.0.0; the Thunderstore listing stays as-is (Thunderstore has no
delete) — its README already points at the merged package.

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

1. **Bump version** at every site listed above
2. **Build** — verify no errors. The post-build target auto-deploys to the test server
   and the Gale test profile; nothing to copy by hand there.
3. **Copy the DLL to the Thunderstore folder:**
   ```
   copy bin\Release\Valheim-ServerGuard.dll "Thunderstore files\Valheim-ServerGuard\"
   ```
   Then **verify the staged DLL's `FileVersion` actually reads the new number** before
   zipping (`(Get-Item <dll>).VersionInfo.FileVersion`). A stale DLL zipped under a new
   `version_number` is the most embarrassing release mistake available.
4. **Update the Thunderstore manifest** — `version_number` in `manifest.json`
5. **Update the CHANGELOG** — user-friendly, non-technical (readers are mod users, not developers)
6. **Update the README** if needed
7. **Zip the package — into the `Thunderstore files\` root, never into the package folder:**
   ```powershell
   $ts = "E:\Valheim Modding\Valheim-ServerGuard\Thunderstore files"
   $stamp = Get-Date -Format "yyyy-MM-dd_HHmm"
   $items = Get-ChildItem (Join-Path $ts "Valheim-ServerGuard") | Where-Object { $_.Extension -ne ".zip" } | Select-Object -ExpandProperty FullName
   Compress-Archive -Path $items -DestinationPath (Join-Path $ts ("Valheim-ServerGuard_" + $stamp + ".zip")) -Force
   ```
   **Why the `.zip` exclusion and the root destination:** an earlier version of this
   step wrote the zip *into* the package folder, so the next release's zip swallowed the
   previous release's zip as a package file. Keep zips out of the package folder.
   The zip must contain exactly five root entries: `CHANGELOG.md`, `icon.png`,
   `manifest.json`, `README.md`, and `Valheim-ServerGuard.dll`. **Never the `wiki/` directory.**
8. **Commit and push to GitHub**
9. **Upload zips to Thunderstore** manually (Claude cannot do this)

---

## Verifying against a new Valheim release

Done for Valheim 1.0.7 on 2026-09-09 (shipped as 1.8.1 — no code changes were needed).
Repeat this on every game update; the result goes in `CLAUDE.md` (*Game verified
against*) and `IMPLEMENTATION_SUMMARY.md`.

**A clean compile proves almost nothing here.** Most of this mod's access to Valheim is
by *string* — `[HarmonyPatch(typeof(X), "Method")]`, `GetField("m_x")`,
`GetMethod("Y")` — and those fail silently at runtime, not at build time. The order
below is deliberate; only the last step proves the patches actually apply.

1. **Copy the pre-update `assembly_valheim.dll` aside *first*.** Steam can finish updating
   the dedicated server mid-session and overwrite it, and without a baseline, absent
   members read as regressions when several are long-standing fallback paths
   (`ZNetPeer.m_platformUserID`, `ZRpc.GetUID`, `ZRpc.m_ping`, `ConsoleCommand.m_isCheat`
   all report "missing" on *every* build).
2. **Compile** against the new `Managed\` folders — catches direct references only.
3. **Enumerate every string lookup**: grep `HarmonyPatch(`, `AccessTools.`, `GetField("`,
   `GetMethod("`, `GetProperty("`, `GetType("`, and the `GetField(obj, "m_…")` helper calls.
4. **Resolve each against the new assembly** with a `System.Reflection.MetadataLoadContext`
   tool (reflects without executing; target `net6.0` — the local SDK default refuses
   `net9.0`). Members the client reflects on in `Unity.TextMeshPro.dll` and
   `UnityEngine.ImageConversionModule.dll` need the same check with that assembly loaded.
5. **Diff signatures *and parameter names*, not just existence.** A method that survives
   with a changed signature breaks a patch just as hard, and Harmony injects postfix
   arguments by **name**. (1.0 added a trailing `bool cheated` to `Player.PlacePiece`;
   the patch survived only because `piece`/`pos` kept their names and positions.)
6. **Boot the dedicated server headless** and read `BepInEx\LogOutput.log` for the
   `Loading [Valheim ServerGuard x.y.z]` line, `Self-test pass=N fail=0`, and any Harmony
   patch error. Isolate the run: `-public 0`, an off-band `-port`, a throwaway `-savedir`.
   **BepInEx truncates `LogOutput.log` on every run** — read the whole file afterwards;
   capturing "new bytes" by offset skips the entire plugin-load preamble.
7. Errors from *other* plugins in that log are useful triage for the user but are not
   this mod's problem — attribute by stack frame before reporting.

The client half can be verified through steps 2–5 only; step 6 needs the game GUI. Step 6
on the server proves the entry plugin picked the server half (`starting as SERVER`) and
applied the expected patch-class count (`Applied 14 server-side Harmony patch class(es)`
as of 2.0.0).

---

## GitHub Wiki

The `wiki/` directory contains 12 markdown files in GitHub Wiki format:
```
wiki/Home.md
wiki/Installation.md
wiki/Configuration.md
wiki/Allowed-Mods-and-Modset.md
wiki/Discord-Integration.md
wiki/Admin-Commands.md
wiki/Anti-Cheat-Features.md
wiki/Bans-and-Console-Guard.md      ← added 1.7.0
wiki/Privilege-Tiers.md             ← added 1.7.0
wiki/Forensic-Logs.md
wiki/Quick-Login.md
wiki/Troubleshooting.md
```

**The wiki is a SEPARATE repository** — pushing `origin main` does not publish it:

```
https://github.com/yesu0725/Valheim-ServerGuard.wiki.git   (default branch: master)
```

To publish: clone the `.wiki.git` repo, copy the changed pages in, commit, push
`origin master`. `Home.md` becomes the landing page.

**Copy only the pages that actually changed.** The two repos use different newline
conventions in places, so a copy-all produces whole-file diffs on pages with no real
edits and makes the wiki history unreadable. Find the real changes with:

```bash
diff -q --strip-trailing-cr "wiki/<page>.md" "<wikiclone>/<page>.md"
```

then write each changed page using the destination's convention (currently CRLF
throughout):

```bash
sed 's/\r$//' "$SRC/$n" | sed 's/$/\r/' > "$DST/$n"
```

Verify with `git diff --cached --stat` before committing — the line counts should
match the size of the real edit, not the size of the file.

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
