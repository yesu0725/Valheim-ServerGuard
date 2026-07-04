# Discord Routing

## Two channels

| Channel | Webhook setting | Audience | Content |
|---|---|---|---|
| **Public** | `discordWebhookUrl` | All community members | Player joined/left/kicked/banned/died — plain language only |
| **Admin** | `discordWebhookUrlAdmin` | Moderators only | Violation strikes, config reloads, admin commands, plugin lifecycle, ping, self-test |

If only one URL is configured, `DiscordChannel.Admin` falls back to the public URL (single-channel deployments).

---

## Channel enum and send functions

```csharp
private enum DiscordChannel { Public, Admin, Both }

// Low-level: post to specific channel
private async Task SendDiscordNow(string text, DiscordChannel target = DiscordChannel.Public)

// Admin events: curated, goes to Admin channel
private void PostAdminEvent(string text)

// Player lifecycle events: routing depends on IsAdmin(platformId)
private void PostPlayerEvent(string emoji, string platformId, string action, string reason = null)
```

---

## PostPlayerEvent routing logic

```csharp
void PostPlayerEvent(emoji, platformId, action, reason) {
    TrackEventForDailySummary(action, reason);    // always counts toward summary
    var line = $"{emoji} {FormatPlayer(platformId)} {action}" + optional reason;

    if (IsAdmin(platformId)) {
        // Admin event → ADMIN channel only. Never public.
        SendDiscordNow(line, DiscordChannel.Admin);
        return;
    }
    // Non-admin → PUBLIC channel
    SendDiscordNow(line, DiscordChannel.Public);
}
```

**Admin events are hidden from the public channel.** Players never see admins coming and going.

---

## What calls PostPlayerEvent

| Caller | Emoji | Action |
|---|---|---|
| `Patch_OnNewConnection` (admin branch) | `:shield:` | `"joined as admin"` |
| `OnManifestReceived` (pass) | `:white_check_mark:` | `"joined"` |
| `Patch_Disconnect` | `:wave:` | `"left"` |
| `TryKick` | varies | `"was kicked"` |
| `AddViolation` (auto-ban) | `:no_entry:` | `"was auto-banned"` |
| `OnPlayerDeathReceived` | `:skull:` | `"died at [x, z]"` + cause |
| `OnDevcommandAttemptReceived` | `:warning:` | `"tried to use cheats"` |

---

## What calls PostAdminEvent

| Caller | Message |
|---|---|
| `Awake` | `:rocket: ServerGuard online v1.4.0 ...` |
| `LoadSettings` (hot-reload only) | `:arrows_counterclockwise: settings.yaml reloaded` |
| `LoadAdmins` (hot-reload only) | `:arrows_counterclockwise: admins.yaml reloaded (N admins)` |
| `LoadAllowedMods` (hot-reload only) | `:arrows_counterclockwise: allowed_mods.yaml reloaded` |
| `AddViolation` (counted) | `:warning: violated rule X (N/threshold)` |
| `AddViolation` (informational) | `:eye: triggered rule X (informational — not counted)` |
| `AddViolation` → `TryBan` | `:no_entry: Auto-banned ...` |
| `TryKick` | `:door: Disconnected ... — reason` |
| `OnAdminCommandReceived` (mutating cmds) | `:satellite: Admin command by ...` |
| `FlushPingOnDisconnect` | `:satellite: session ping avg: N ms` |
| `TickPingLog` (first ping) | `:satellite: first ping: N ms` |
| Self-test | `:white_check_mark:` / `:rotating_light: Self-test — N pass / N fail` |

---

## _bootCompleted guard

```csharp
private bool _bootCompleted = false;
// Set at the END of Awake(), after self-test, after PostAdminEvent(":rocket: ...").
```

`LoadSettings()` is called during `Awake()`. Without this guard, every settings reload during boot would post `:arrows_counterclockwise: reloaded` to Discord — noisy and meaningless on server restart.

```csharp
// Only fires AFTER boot is complete (i.e. on actual hot-reloads):
if (_bootCompleted) PostAdminEvent(":arrows_counterclockwise: settings.yaml reloaded");
```

---

## ReconfigureDiscordAndSummary()

Called from both `Awake()` and `LoadSettings()`. Idempotent.

```
ReconfigureDiscordAndSummary()
  If DiscordVerboseMirror=true AND admin URL set:
    Attach DiscordLogListener to BepInEx logger (if not already attached to THIS URL)
  If DiscordVerboseMirror=false OR URL changed:
    Tear down old listener
  If URL changed: log "Admin Discord channel armed"
  If dailySummary not started AND any webhook configured:
    StartCoroutine(DailySummaryLoop())
    _dailySummaryStarted = true
```

`_attachedAdminWebhookUrl` tracks which URL the listener is currently bound to. On URL change, the old listener is torn down and a new one attached.

---

## Verbose mirror (opt-in, default OFF)

```yaml
discordVerboseMirror: true
```

When `true`, attaches a `DiscordLogListener` to `BepInEx.Logging.Logger.Listeners`. This listener forwards every `LogInfo`, `LogWarning`, `LogError` line from the ServerGuard source to the admin webhook.

**Default is OFF** because it's very noisy (every connection attempt, every poll tick). Use only for debugging. The admin channel is designed to be readable by default.

---

## Death log routing

Death events are player lifecycle events handled by `OnPlayerDeathReceived`. They use `PostPlayerEvent`, which means:
- If the dead player is **an admin** → admin channel only
- If non-admin → public channel

Format:
```
💀 CharName (SteamID) died at [x, z] — killed by a Skeleton
💀 CharName (SteamID) died at [x, z] — killed by KillerName (KillerSteamID)
💀 CharName (SteamID) died at [x, z] — burned to death
```

---

## FormatPlayer

```csharp
string FormatPlayer(string steamId)
// Returns: "CharName (SteamID)" using registrations.yaml lookup
// If multiple chars: "CharA, CharB (SteamID)"
// If never logged in: "NewPlayer (SteamID)"
// If steamId null/empty: "NewPlayer (UNKNOWN)"
```

Used in ALL Discord messages for consistent formatting.

---

## Daily summary channel routing

```csharp
var target = (_settings.DailySummaryChannel ?? "admin").ToLowerInvariant() switch {
    "public" => DiscordChannel.Public,
    "both"   => DiscordChannel.Both,
    _        => DiscordChannel.Admin,  // default
};
```
