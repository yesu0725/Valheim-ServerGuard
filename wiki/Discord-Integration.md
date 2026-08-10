# Discord Integration

ServerGuard supports two independent Discord webhooks: a **public** channel and an **admin** channel. Either, both, or neither may be set.

## What each channel receives

### Public channel (`discordWebhookUrl`)

Player-facing events only. Safe to share with your whole community.

| Event | Example |
|---|---|
| Player joined | `✅ Erik (76561198064360681) joined` |
| Player left | `👋 Erik (76561198064360681) left` |
| Player kicked | `🚪 Erik (76561198064360681) was kicked — wrong password` |
| Player auto-banned | `⛔ Erik (76561198064360681) was auto-banned — too many strikes` |
| Player died | `💀 Erik (76561198064360681) died at [-1234, 567] — killed by a Troll` |
| Player shouted | `📣 **Erik** shouted: they're at the docks!` |
| Raid started | `⚔️ **The Horde Is Attacking** has started! Location: X:123, Z:-456` |
| Raid paused / resumed | `⏸️ **The Horde Is Attacking** is paused — no players in the event area.` |
| Raid ended | `✅ **The Horde Is Attacking** is over!` |
| Server starting | `⏳ **Server is starting...**` |
| Server ready | `✅ **The server has started, you may now login.**` |
| Server stopping | `🛑 **Server is shutting down.**` |

The three server-lifecycle messages have no on/off setting — like raid alerts, they follow `discordWebhookUrl`.

**"Starting" and "started" are two separate messages on purpose.** The plugin loads long before the world does, so "starting" fires immediately on boot, and "you may now login" only once the world is loaded and location generation has finished — which on a brand-new seed can be several minutes later. Players who join on the first message would just be refused.

If world generation somehow never completes, no public "started" message is sent; a timeout warning goes to the admin channel instead after 15 minutes.

The shutdown message needs a **graceful** stop (console `stop`, service stop, window close). A `SIGKILL`, power loss, or host crash gives the plugin no chance to post.

**Admins are hidden from this channel.** Their joins, leaves, deaths, and kicks all route to the admin channel instead. Players never see admins coming and going.

### Admin channel (`discordWebhookUrlAdmin`)

Curated moderation events. Use a private channel that only moderators can see.

| Event | Example |
|---|---|
| Server boot | `🚀 ServerGuard online v1.6.2  enforce=ON  requireHmac=ON  req/allow/ban=1/29/0  modset=8ce8906e` |
| Hot-reload | `🔄 allowed_mods.yaml reloaded — req=1 allow=29 ban=0` |
| Counted violation | `⚠️ Erik (765…) violated DevcommandAttempt (1/3) — fly` |
| Informational rule | `👁 Erik (765…) triggered HashMismatch (informational — not counted) — Jotunn` |
| Auto-ban | `⛔ Auto-banned Erik (765…) (threshold reached)` |
| Manual kick | `🚪 Disconnected Erik (765…) — Kicked by admin.` |
| Admin command | `🛠 Erik (765…) ran sg kick someone` |
| Admin player events | `🛡 Erik (765…) joined as admin` |
| First ping (if enabled) | `🛰 Erik (765…) first ping: 42 ms` |
| Session avg ping | `🛰 Erik (765…) session ping avg: 38 ms (24 samples)` |
| Daily summary | A one-paragraph digest at the configured UTC hour |

### Verbose mirror (optional, `discordVerboseMirror: true`)

Forwards every ServerGuard log line to the admin channel. Very noisy — debug only.

## Creating webhooks

In Discord:
1. Open your server settings → Integrations → Webhooks.
2. Create a new webhook in the channel where you want events to appear.
3. Copy the URL.
4. Paste into `settings.yaml`:

```yaml
discordWebhookUrl: 'https://discord.com/api/webhooks/...'        # public
discordWebhookUrlAdmin: 'https://discord.com/api/webhooks/...'   # admin
```

5. Save. Hot-reload picks it up — no restart needed.

If only one URL is configured, all events that would route to the other channel are silently suppressed.

## Daily summary

A coroutine fires once per UTC day and posts a digest:

```
📊 Daily summary
2026-05-29 00:00 UTC → 2026-05-30 00:00 UTC
• Joins: 14
• Leaves: 12
• Kicks: 3
• Auto-bans: 1
Top kick reasons:
  – wrong password (2)
  – had a mod that isn't allowed (BiggerBackpack) (1)
```

Configure:

```yaml
dailySummaryEnabled: true
dailySummaryHourUtc: 0       # 0..23 - hour at which the post fires
dailySummaryChannel: admin   # public | admin | both
```

If no events occurred, the scheduled post is silently skipped (no spam).

## Friendly reason wording

The public channel uses non-technical language. Internal rule names like `HmacInvalid`, `DisallowedMod`, `ChallengeMismatch` are translated:

- `HmacInvalid` → "wrong password"
- `CompanionMissing` → "missing the required companion mod"
- `RequiredModMissing` → "missing a required mod (Name)"
- `DisallowedMod` → "had a mod that isn't allowed (Name)"
- `BannedMod` → "had a banned mod (Name)"
- `HashMismatch` → "mod file doesn't match the server's copy (Name)"
- `CharacterNameLimitExceeded` → "tried to use too many characters"

The admin channel keeps the technical rule name so moderators can correlate to log lines.

## Stopping a noisy channel

If you accidentally point both URLs at the same channel and don't want duplicates: set `dailySummaryChannel: admin` (or `public`) instead of `both`, and don't enable `discordVerboseMirror`.

## See also

- **[Configuration](Configuration)** — all webhook settings.
- **[Anti-Cheat Features](Anti-Cheat-Features)** — rules that fire Discord events.
- **[Admin Commands](Admin-Commands)** — `sg` events that post to admin channel.
