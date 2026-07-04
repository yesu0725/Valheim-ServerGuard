# Mono Runtime Constraints

**Read this before writing any new code.**

Valheim runs on Unity's Mono runtime which ships with the game. This runtime is older than modern .NET and is missing several assemblies that ship with .NET 4.6.2 on Windows. These missing assemblies cause silent-at-compile-time, fatal-at-runtime crashes.

---

## CONSTRAINT 1 — No `ValueTuple` in any code path

### Symptom
```
[Error] [HarmonyX] Exception from patching ...
TypeLoadException: Could not load type 'System.ValueTuple`2' from assembly 'System.ValueTuple, Version=4.0.3.0'
```
The game loads but the patched class fails to JIT. Any Harmony patch in the same class file that contains `ValueTuple` usage dies silently at startup.

### Root cause
`System.ValueTuple` is a NuGet package (`System.ValueTuple.dll`). It ships with .NET 4.7+ by default, but Valheim's Mono install does not include it. The compiler generates `ValueTuple` fields in the `<>c__DisplayClass` closures for any code that uses the `(T1, T2)` syntax — even in lambdas or LINQ `.Select()` that look innocent.

### What to avoid
```csharp
// BAD — ValueTuple in return type
(float X, float Z)? TryParseXZ(string a, string b) { ... }

// BAD — ValueTuple in LINQ Select
var results = rows.Select(r => (Row: r, Pos: ParsePos(r))).ToList();

// BAD — anonymous type that embeds a ValueTuple
var q = rows.Select(r => new { R = r, P = (x: r.X, z: r.Z) });
```

### What to use instead
```csharp
// GOOD — out parameters
bool TryParseXZ(string a, string b, out float x, out float z) { ... }

// GOOD — imperative loop with separate variables
var results = new List<(string key, float x, float z)>();  // NO — still ValueTuple
// Actually do:
foreach (var r in rows) {
    if (!TryParseXZ(r.X, r.Z, out float x, out float z)) continue;
    // use x, z directly
}

// GOOD — KeyValuePair for key/value pairs
IEnumerable<KeyValuePair<string,string>> entries  // ModsetFingerprint API uses this
```

### Where this was already fixed
- `Shared/Manifest.cs` — `ModsetFingerprint.ComputeStrict/Loose` use `IEnumerable<KeyValuePair<string,string>>` not `IEnumerable<(string key, string hash)>`
- `Plugin.cs` — `CmdBuildAt` rewritten as imperative loop; `TryParseXZ(s, s, out float x, out float z)`; `Distance2D(float, float, float, float)` plain floats

---

## CONSTRAINT 2 — No `nameof` on protected methods

### Symptom
```
CS0122: 'Player.OnDeath()' is inaccessible due to its protection level
```

### Root cause
`nameof(Player.OnDeath)` is a compile-time expression — the compiler must be able to see the member. Protected members of external types are not visible.

### Fix
Use a string literal. Harmony resolves patch targets via reflection at runtime, not at compile time, so a string literal works fine:
```csharp
// BAD
[HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]

// GOOD
[HarmonyPatch(typeof(Player), "OnDeath")]
```

---

## CONSTRAINT 3 — No `ref bool __result` on void method patches

### Symptom
```
[Error] [HarmonyX] Cannot get result from void method Terminal::TryRunCommand
```
The Harmony patcher throws and the entire patch batch for that class is skipped.

### Root cause
HarmonyX validates that `ref bool __result` or `bool __result` only appear in patches of methods that actually return a value. Void methods have no result to capture.

### Fix
Omit `__result` entirely from the Postfix/Prefix signature:
```csharp
// BAD — TryRunCommand / PlacePiece are void
public static void Postfix(Terminal __instance, bool __result) { ... }

// GOOD
public static void Postfix(Terminal __instance) { ... }
```

---

## CONSTRAINT 4 — No direct reference to `PlatformUserID` or Splatform types

### Symptom
```
CS0012: The type 'PlatformUserID' is defined in an assembly that is not referenced.
The assembly 'Splatform, Version=1.0.0.0' cannot be found.
```

### Root cause
Several Valheim APIs have overloads that take `Splatform.PlatformUserID` — e.g. `Chat.AddString(string, Talker.Type)` and `Chat.SendText(Talker.Type, string, ...)`. Just writing those method names in a method call (even with correct argument types) triggers overload resolution that drags in the `Splatform` assembly.

### Fix
Use reflection for anything that *might* touch these types:
```csharp
// BAD
Chat.instance.AddString($"<color=orange>{text}</color>", Talker.Type.Normal);

// GOOD — resolve via reflection, avoid all Splatform-contaminated overloads
var consoleType = typeof(Terminal).Assembly.GetType("Console");
var method = consoleType?.GetMethod("Print", new[] { typeof(string) });
method?.Invoke(consoleInstance, new object[] { text });
```

The `ResolveConsoleInstance()` method in `ClientPlugin.cs` is the canonical pattern.

Also: `GetPeerPlatformId(object znetPeer)` in `Plugin.cs` accesses `m_platformUserID` via reflection rather than directly, for the same reason.

---

## CONSTRAINT 5 — No `Console.IsCheatsEnabled` patch

### Symptom
```
[HarmonyX] AccessTools.DeclaredMethod: Could not find method for type Console and name IsCheatsEnabled
```

### Root cause
`Console.IsCheatsEnabled` does not exist in this Valheim build. The method was introduced in a later patch. Any `[HarmonyPatch(typeof(Console), "IsCheatsEnabled")]` fails silently at patch time.

### Fix
The `devcommands` gate is fully handled in `Patch_TryRunCommand` — the `BlockedCommands` HashSet intercepts blocked commands before they reach the `Terminal.TryRunCommand` path, which is sufficient. Do not attempt to patch `IsCheatsEnabled`.

---

## CONSTRAINT 6 — `Inventory.AddItem` has multiple overloads

When patching `Inventory.AddItem`, you must specify the exact signature:
```csharp
[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem), new Type[] { typeof(ItemDrop.ItemData) })]
```
Without the explicit `new Type[]` argument, Harmony picks an arbitrary overload and the patch may silently attach to the wrong one.

---

## Summary table

| Constraint | What to avoid | What to use |
|---|---|---|
| ValueTuple | `(T1, T2)` syntax anywhere in code that runs at load | `KeyValuePair<K,V>`, `out` parameters, plain separate variables |
| Protected `nameof` | `nameof(Player.OnDeath)` | `"OnDeath"` string literal |
| Void result | `ref bool __result` / `bool __result` in void patches | Omit `__result` |
| Splatform assembly | `Chat.AddString`, `Talker.Type`, direct `PlatformUserID` usage | Reflection on Console/Terminal types |
| Missing method | `Console.IsCheatsEnabled` patch | Block in `TryRunCommand` Prefix instead |
| Overloaded patch target | Bare `nameof` for overloaded methods | Explicit `new Type[]` in patch attribute |
