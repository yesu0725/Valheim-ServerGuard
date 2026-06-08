# Harmony Patterns

Conventions and gotchas specific to this project's use of HarmonyX on Valheim.

---

## Patch attribute forms

### Standard public method
```csharp
[HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
public static class Patch_Disconnect { ... }
```

### Protected or inaccessible method — string literal required
```csharp
// nameof(Player.OnDeath) won't compile — protected method
[HarmonyPatch(typeof(Player), "OnDeath")]
public static class Patch_Player_OnDeath_Report { ... }
```

### Overloaded method — explicit signature required
```csharp
[HarmonyPatch(typeof(Inventory), nameof(Inventory.AddItem),
    new Type[] { typeof(ItemDrop.ItemData) })]
public static class Patch_Inventory_AddItem { ... }
```

### Internal Valheim method (no public name)
```csharp
[HarmonyPatch(typeof(ZNet), "OnNewConnection")]
[HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
```

---

## Prefix vs Postfix

| Use | When |
|---|---|
| **Prefix** | Need to read state *before* the method runs (e.g. `m_lastHit` before OnDeath clears it), or to return `false` to suppress the method |
| **Postfix** | Need to run after the original method (e.g. after a piece is placed, read its world `pos`) |

### Returning false from a Prefix to block execution
```csharp
public static bool Prefix(...)
{
    if (shouldBlock) return false;   // skip the original
    return true;                     // run the original
}
```

---

## Parameter binding rules

HarmonyX binds Prefix/Postfix parameters **by name**, not by position.

### `__instance` — the object the method is called on
```csharp
public static void Prefix(WearNTear __instance, HitData hit) { ... }
```

### `__result` — the method's return value
- Only valid when the original method returns a non-void type.
- On a **void method**, declaring `__result` causes:
  ```
  [HarmonyX] Cannot get result from void method ...
  ```
  **Always omit `__result` on void methods.**

### Named method parameters — bind by matching the parameter name exactly
```csharp
// Original: bool PlacePiece(Piece piece, Vector3 pos, Quaternion rot, bool isDungeonMode)
// Postfix that reads the placed world position:
public static void Postfix(Player __instance, Piece piece, Vector3 pos)
```
The `pos` name matches the original method's `pos` parameter — Harmony injects its value. **This is how we get the real world position**, not `piece.transform.position` (which is the prefab origin).

### `ref` parameters for mutation
```csharp
// Modify a return value in a Prefix or Postfix
public static void Postfix(ref bool __result) { __result = false; }
```

---

## Patches in this project and their patterns

| Class | Method | Type | Key parameters | Notes |
|---|---|---|---|---|
| `Patch_OnNewConnection` | `ZNet.OnNewConnection` | Postfix | `ZNetPeer peer` | Registers all RPC handlers **before** admin check |
| `Patch_Disconnect` | `ZNet.Disconnect` | Prefix | `ZNetPeer peer` | `_suppressLogoutFor` prevents double-posting |
| `Patch_RPC_PeerInfo` | `ZNet.RPC_PeerInfo` | Postfix | `ZNet __instance, ZRpc rpc` | Resolves peer from rpc via reflection |
| `Patch_Inventory_AddItem` | `Inventory.AddItem` | Prefix | `Inventory __instance, ItemDrop.ItemData item` | Returns `false` only when `!logOnly` |
| `Patch_WearNTear_Damage_Track` (server) | `WearNTear.Damage` | Prefix | `WearNTear __instance, HitData hit` | Fills `LastHitBox` in `ConditionalWeakTable` |
| `Patch_WearNTear_Destroy_Log` (server) | `WearNTear.Destroy` | Prefix | `WearNTear __instance` | Reads `LastHitBox`, logs to CSV |
| `Patch_WearNTear_Damage_TrackClient` (client) | `WearNTear.Damage` | Prefix | `WearNTear __instance, HitData hit` | Only on `IsActiveMultiplayerClient()` |
| `Patch_WearNTear_Destroy_ClientReport` (client) | `WearNTear.Destroy` | Prefix | `WearNTear __instance` | Sends `ServerGuard_BuildDestroy` RPC |
| `Patch_PlacePiece_Report` (client) | `Player.PlacePiece` | Postfix | `Player __instance, Piece piece, Vector3 pos` | `pos` is the actual world position argument |
| `Patch_Player_OnDeath_Report` (client) | `Player."OnDeath"` | Prefix | `Player __instance` | String literal, not nameof |
| `Patch_TryRunCommand` (client) | `Terminal.TryRunCommand` | Prefix | `Terminal __instance, string text` | Intercepts `sg`/`/sg` before devcommands gate |
| `Patch_Player_StartEmote_BlockDuringAttack` (client) | `Player.StartEmote` | Prefix | `Player __instance` | Returns `false` to block emote during attack |
| `Patch_Humanoid_HideHandItems_BlockDuringAttack` (client) | `Humanoid.HideHandItems` | Prefix | `Humanoid __instance` | Casts to Player, returns `false` during attack |
| `Patch_RegisterClientHandler` (client) | `ZNet.OnNewConnection` | Postfix | `ZNetPeer peer` | Stashes `_serverRpc`, registers `ServerGuard_RequestManifest` handler |

---

## ConditionalWeakTable for auto-GC piece tracking

```csharp
private static readonly ConditionalWeakTable<WearNTear, LastHitBox> _lastHit
    = new ConditionalWeakTable<WearNTear, LastHitBox>();
```

- Key = `WearNTear` instance. When the GameObject is destroyed, the key's GC reference drops and the entry is automatically removed.
- No manual cleanup needed. No instance-ID collisions.
- Used in both server (`Plugin.cs`) and client (`ClientPlugin.cs`) — separate tables, separate classes.

---

## Guard patterns

Almost every patch starts with guards:
```csharp
if (Plugin.Instance == null) return;         // plugin not yet initialized
if (ZNet.instance == null) return;           // ZNet not up yet
if (!ZNet.instance.IsServer()) return;       // server-only logic
if (!IsActiveMultiplayerClient()) return;   // client-only logic
if (__instance == null) return;             // null check on target
if (__instance != Player.m_localPlayer) return; // local player only
```

`IsActiveMultiplayerClient()` in `ClientPlugin.cs`:
```csharp
return ZNet.instance != null
    && !ZNet.instance.IsServer()
    && ZNet.instance.GetNrOfPlayers() >= 0;  // i.e. connected to a server
```

---

## PatchAll vs manual Harmony.Patch

All patches are inner classes annotated with `[HarmonyPatch]` attributes, so `_harmony.PatchAll()` in `Awake()` picks them all up. No `Harmony.Patch(...)` calls needed anywhere. The GUID for the server harmony instance is `"com.taeguk.valheim.serverguard"` and for the client it is the GUID constant.
