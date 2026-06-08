# Known Errors and Fixes

Historical record of every error encountered during development of this mod, plus the exact fix applied. Consult this before debugging.

---

## ERROR 1 — TypeLoadException: System.ValueTuple

### Symptom
```
[Error] [HarmonyX] Exception from patching ...
System.TypeLoadException: Could not load type 'System.ValueTuple`2' from assembly 'System.ValueTuple, ...'
```
The plugin loads but patches in the affected class silently fail. No `sg` commands, no build log, etc.

### Root cause
Valheim's Mono runtime does not ship `System.ValueTuple`. ANY use of the `(T1, T2)` syntax anywhere in a class that gets compiled — including in lambda closures and LINQ `.Select()` — creates a compiler-generated closure class with `ValueTuple` fields. When Harmony tries to patch a method in the same class, it JITs the class and fails.

### Fix
- `Shared/Manifest.cs`: `ModsetFingerprint.ComputeStrict/Loose` signatures changed from `IEnumerable<(string,string)>` to `IEnumerable<KeyValuePair<string,string>>`
- `Plugin.cs`: `CmdBuildAt` rewritten as imperative loop; `TryParseXZ` changed from `(float X, float Z)?` return to `out float x, out float z` parameters; `Distance2D` takes plain `float` args
- Client: Any LINQ that used anonymous tuple projection was replaced with `new { }` anonymous objects or imperative loops

### Prevention
See `claude/mono-constraints.md` — CONSTRAINT 1. **Never use `(T1, T2)` syntax anywhere.**

---

## ERROR 2 — AccessTools.DeclaredMethod: Could not find IsCheatsEnabled

### Symptom
```
[HarmonyX] AccessTools.DeclaredMethod: Could not find method for type Console and name IsCheatsEnabled and parameters (null)
```

### Root cause
`Console.IsCheatsEnabled` does not exist in the Valheim build in use. The method was introduced in a later patch version.

### Fix
Removed `Patch_IsCheatsEnabled` entirely. The devcommands gate works by intercepting `Terminal.TryRunCommand` in `Patch_TryRunCommand` — the `BlockedCommands` HashSet check is sufficient.

### Prevention
Do not patch methods that may not exist in this Valheim build. Check the game's assembly first if unsure.

---

## ERROR 3 — Cannot get result from void method Terminal::TryRunCommand

### Symptom
```
[Error] [HarmonyX] Cannot get result from void method Terminal::TryRunCommand
```
The patch class is skipped entirely — NO patches in that class attach.

### Root cause
The original `Patch_TryRunCommand` Prefix had `ref bool __result` in its parameter list. `TryRunCommand` returns `void`. HarmonyX validates this at patch-attach time.

### Fix
Removed `ref bool __result` from the Prefix signature entirely. The patch swallows the command by just not calling `return true;` (runs `return false;` to skip original) — no result needed.

### Prevention
See `claude/mono-constraints.md` — CONSTRAINT 3. **Never declare `__result` in a patch on a void method.**

---

## ERROR 4 — CS0122: Player.OnDeath() is inaccessible

### Symptom
```
CS0122: 'Player.OnDeath()' is inaccessible due to its protection level
```
Compile-time error.

### Root cause
`Player.OnDeath()` is `protected`. `nameof(Player.OnDeath)` requires the compiler to resolve the member, which fails on protected members of external types.

### Fix
```csharp
// Before (compile error):
[HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]

// After (correct):
[HarmonyPatch(typeof(Player), "OnDeath")]
```

### Prevention
See `claude/mono-constraints.md` — CONSTRAINT 2. String literals for protected methods.

---

## ERROR 5 — CS1061: 'Player' does not contain 'm_skills'

### Symptom
```
CS1061: 'Player' does not contain a definition for 'm_skills'
```

### Root cause
`m_skills` is private/internal in this Valheim build and not directly accessible.

### Fix
Use reflection to find any field of type `Skills` on `Player`:
```csharp
foreach (var f in typeof(Player).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
{
    if (f.FieldType == typeof(Skills)) { _playerSkillsField = f; break; }
}
```
Field handle cached in `_playerSkillsField` after first resolution.

---

## ERROR 6 — CS0012: PlatformUserID defined in Splatform assembly

### Symptom
```
CS0012: The type 'PlatformUserID' is defined in an assembly that is not referenced.
The assembly 'Splatform, ...' cannot be found.
```

### Root cause
Certain Valheim API overloads (e.g. `Chat.AddString(string, Talker.Type)`) have overloads that pull in `Splatform.PlatformUserID` in their signatures. Just *calling* those overloads triggers the Splatform reference in the compiled output, which Valheim's BepInEx can't resolve.

### Fix
- Use reflection to invoke `Console.Print(string)` or `Console.AddString(string)` instead of direct calls
- Access `ZNetPeer.m_platformUserID` via `reflection.GetField("m_platformUserID", ...)` not directly
- `ResolveConsoleInstance()` in `ClientPlugin.cs` is the canonical pattern for console writes

### Prevention
See `claude/mono-constraints.md` — CONSTRAINT 4.

---

## ERROR 7 — Build positions all (0, 0, 0) in CSV

### Symptom
Build log CSV shows `x=0.0, y=0.0, z=0.0` for all placed pieces.

### Root cause
`piece.transform.position` in the `PlacePiece` patch was reading the **prefab template's transform**, which is at `(0,0,0)`. The actual world position is passed as the `pos` parameter to `PlacePiece(Piece piece, Vector3 pos, ...)`.

### Fix
Added `Vector3 pos` to the Postfix signature. Harmony binds it by parameter name from the original method:
```csharp
// Before:
public static void Postfix(Player __instance, Piece piece)
{
    var pos = piece.transform.position; // WRONG — prefab origin
}

// After:
public static void Postfix(Player __instance, Piece piece, Vector3 pos)
{
    // pos = actual world position, injected by Harmony from method's `pos` arg
}
```

---

## ERROR 8 — sg commands not working for admins

### Symptom
Admin types `sg help` in F5 console, gets no response. Non-admins also couldn't use it if their connection was tested.

### Root cause
`Patch_OnNewConnection` had an early `return` for admin peers **before** registering the `ServerGuard_AdminCommand` RPC handler. The admin's companion sent the RPC to a peer that had no listener registered for it.

### Fix
Moved ALL `peer.m_rpc.Register(...)` calls to BEFORE the admin check:
```csharp
// Register ALL handlers first
peer.m_rpc.Register<string>("ServerGuard_Manifest", ...);
peer.m_rpc.Register<string>("ServerGuard_DevcommandAttempt", ...);
// ... all others ...
peer.m_rpc.Register<string>("ServerGuard_AdminCommand", ...);

// THEN check admin
if (IsAdmin(pid)) { ... return; }
```

### Prevention
See `claude/rpc-protocol.md` — Critical registration rule.

---

## ERROR 9 — Admin webhook not receiving anything

### Symptom
Server admin Discord channel receives nothing. Public channel works fine.

### Root cause
`ReconfigureDiscordAndSummary()` was only called from `Awake()`. When the admin URL was added to `settings.yaml` after the server had booted (hot-reload), `LoadSettings()` updated `_settings.discordWebhookUrlAdmin` in memory but never re-evaluated the Discord listener. The URL was in memory but nothing used it to route events.

### Fix
`LoadSettings()` calls `ReconfigureDiscordAndSummary()` at the end:
```csharp
private void LoadSettings() {
    // ... parse YAML ...
    try { ReconfigureDiscordAndSummary(); } catch { ... }
}
```

---

## ERROR 10 — Hammer/weapon destroys not logged in CSV

### Symptom
CSV build log missing entries for pieces the player destroyed with a hammer (demolish) or weapon.

### Root cause
`WearNTear.Destroy` only fires on the **ZDO owner** of the piece. Most pieces near an active player are owned by that player's client, not the server. The server-side `Patch_WearNTear_Destroy_Log` only fires for server-owned ZDOs — which covers server-side decay and distant creature damage, but not player-adjacent pieces.

### Fix
Added client-side patches:
- `Patch_WearNTear_Damage_TrackClient` — tracks `hit.GetAttacker()` in `_clientLastHitOnPiece`
- `Patch_WearNTear_Destroy_ClientReport` — reads attribution, sends `ServerGuard_BuildDestroy` RPC to server

Together, client covers nearby pieces, server covers server-owned pieces. No double-logging because a given `Destroy()` only fires on one machine.

---

## ERROR 11 — Cannot get result from void method Player::PlacePiece

### Symptom
```
[Error] [HarmonyX] Cannot get result from void method Player::PlacePiece
```
Same class as `Patch_WearNTear_Destroy_ClientReport` fails to attach.

### Root cause
`Patch_PlacePiece_Report` Postfix had `bool __result` in its parameter list. `PlacePiece` is void.

### Fix
Removed `bool __result` from signature. The placement already happened by the time the Postfix runs — no result needed.

---

## ERROR 12 — TypeLoadException in CmdBuildAt (ValueTuple in LINQ)

### Symptom
Same as Error 1, but specifically in `CmdBuildAt` / `CmdBuild` methods.

### Root cause
```csharp
// BAD - creates ValueTuple in compiler-generated closure:
var matches = rows.Select(r => new { Row = r, Pos = TryParseXZ(r.X, r.Z) })
                  .Where(p => p.Pos.HasValue && ...)
```
`TryParseXZ` returning `(float X, float Z)?` was the source.

### Fix
Rewrote as imperative loop using `out` parameters:
```csharp
bool TryParseXZ(string sx, string sz, out float x, out float z) { ... }

var matches = new List<BuildLogRow>();
foreach (var r in rows) {
    if (!TryParseXZ(r.X, r.Z, out float rx, out float rz)) continue;
    if (Distance2D(rx, rz, targetX, targetZ) > radius) continue;
    matches.Add(r);
}
```
