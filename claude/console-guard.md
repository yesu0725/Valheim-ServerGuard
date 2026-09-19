# Console Guard & Key Binds

Reference for the console-command gate (client-enforced, server-configured) and the
risk assessment behind the built-in blocklists.

Source of the command list: <https://valheim.fandom.com/wiki/Developer_console>
(verified against `assembly_valheim.dll` for the mechanics described below).

---

## How Valheim's console actually works

Facts established by reading the shipped assembly, not the wiki. These drive every
design decision in this file.

| Mechanism | Reality |
|---|---|
| `Console.IsConsoleEnabled()` | Instance method returning the static `Console.m_consoleEnabled`. `Console.Update()` early-returns when it is false, and that single check gates **both** the F5 key and the gamepad chord. Forcing it false is a complete console lockout. |
| `Terminal.TryRunCommand(text, silentFail, skipAllowedCheck)` | The single dispatch point for every console **and** chat command. Patching it catches typed commands, bind-fired commands, and other mods' commands alike. It is `void` — a Harmony prefix must not declare `ref bool __result`. |
| `Terminal.isAllowedCommand(cmd)` | Compiles to `return true`. It is a stub. Do **not** rely on it as a gate. |
| `Terminal.IsCheatsEnabled()` | `m_cheat && ZNet.instance != null && ZNet.instance.IsServer()`. On a multiplayer **client** the `IsServer()` term is false, so this is **always false** there. |
| `Terminal.m_cheat` | Written in exactly two places: the static ctor, and the `devcommands` command's own handler. |
| `ConsoleCommand.IsValid(terminal, skipAllowedCheck)` | `if (IsCheat && !terminal.IsCheatsEnabled()) return false;` then `if (!(terminal.isAllowedCommand(this) \|\| skipAllowedCheck)) return false;` — note the cheat check is **not** covered by `skipAllowedCheck`. |
| `Terminal.commands` | `Dictionary<string, Terminal.ConsoleCommand>`. `ConsoleCommand` carries `IsCheat`, `OnlyAdmin`, `OnlyServer`, `IsNetwork`, `RemoteCommand`, `IsSecret`. |
| `Terminal.m_binds` | `Dictionary<KeyCode, List<string>>`, static. Written **only** by `Terminal.updateBinds()` — verified by scanning every writer in the assembly. |
| `Terminal.m_bindList` | `List<string>`, static. Raw `"<KeyCode> <command>"` lines. Persisted to `PlatformPrefs["ConsoleBindings"]`, newline-separated. |
| Bind execution | Happens in **`Chat.Update`**, not `Console.Update`. |
| Bind dispatch call | `TryRunCommand(text, silentFail: true, skipAllowedCheck: true)`. |
| `ZNet.UpdateBanList` | Runs on a 5-second timer (`m_banlistTimer > 5f`) and calls `InternalKick` per banned entry. This is the delay in vanilla banning. |
| `ZNet.IsAllowed(hostName, playerName)` | Private, called from `RPC_PeerInfo` **before** the peer is accepted. Returning false makes vanilla send `ConnectionStatus.ErrorBanned` (8) and `ret`. |

### Two consequences worth spelling out

**1. Disabling the console does not disable key binds.**
Binds fire from `Chat.Update`, which does not consult `IsConsoleEnabled`. A player
with `bind f devcommands` saved never needs to open the console — or even be able
to. This is why `consoleGuardMode: disabled` and `consoleGuardBindPolicy` are
separate settings, and why setting the mode to `disabled` alone is not sufficient.

**2. Binds do NOT escalate past the cheat check — but they don't need to.**

`Chat.Update` passes `skipAllowedCheck: true`, which reads like a bypass. It isn't,
for cheat commands: in `ConsoleCommand.IsValid` the `IsCheat` test comes *first* and
`skipAllowedCheck` only covers the following `isAllowedCommand` test — which is a
`return true` stub anyway, so the flag currently changes nothing at all.

The real bind threat is narrower and still worth closing: a bind runs a **non-cheat**
command (or any command a *mod* registered without `IsCheat`) on a keypress, with no
console open, and it persists across sessions. `bind f nomap`,
`bind g resetsharedmap`, `bind h setworldmodifier …` all work on a stock multiplayer
client today. That is what the risky tier plus the bind purge is for.

**3. Vanilla already blocks cheat commands on a multiplayer client.**

Because `IsCheatsEnabled()` requires `IsServer()`, every `IsCheat` command is refused
on a client whether typed or bound, and the debug-mode hotkey block in `Player.Update`
is gated on the same call, so `B`/`Z`/`K`/`L` are dead there too. (For the record:
`K` and `L` dispatch through `TryRunCommand("killenemies"/"removedrops")` and would be
caught by our gate anyway; `B` and `Z` call `Player.ToggleNoPlacementCost` /
`Player.ToggleDebugFly` directly and would not.)

So the `cheat` tier is **defence in depth, not the primary control**. It matters when:

- another mod deliberately re-enables devcommands server-side (e.g. Server_devcommands),
  or registers powerful commands without setting `IsCheat`;
- the player is the host of a listen server rather than a dedicated-server client;
- a patched client flips `m_cheat` — which is what attestation, not this gate, defends
  against.

The tier that does load-bearing work on an ordinary client is **risky**, because those
commands are ones vanilla is perfectly happy to run.

---

## Enforcement architecture

```
settings.yaml (server)
   consoleGuardMode / consoleGuardBindPolicy / consoleGuardExemptModerators
   consoleBlockedCommands / consoleAllowedCommands
   + owners.yaml / moderators.yaml  (tier -> exempt yes/no, resolved server-side)
        |
        |  ServerGuard_ConsolePolicy RPC
        |  "mode|exempt|role|bindPolicy|blockedCsv|allowedCsv|devMode|devCsv"
        v
ClientPlugin
   Patch_TryRunCommand         -> ShouldBlockConsoleCommand()  -> swallow + report
   Patch_Terminal_UpdateBinds  -> ApplyBindPolicy()            -> clear m_binds
   Patch_Console_IsConsoleEnabled -> force false (mode: disabled)
   Patch_Terminal_IsCheatsEnabled -> m_cheat when a dev grant is active   (2.0)
   Patch_ConsoleCommand_IsValid   -> re-validate granted commands          (2.0)
   Patch_ZNet_Shutdown         -> ResetConsolePolicy()
```

The two 2.0 patches are the *unlock* side of the guard — see **Staff dev commands**
below. They are inert without a grant.

### Why a client-side gate is meaningful here

It is not a security boundary on its own — a player running a hacked client simply
won't honour it. It is meaningful because of what sits in front of it:

- `requireCompanion: true` kicks any client that cannot produce a signed manifest,
  so a vanilla client never reaches the game at all.
- The manifest is HMAC-signed and the companion DLL's SHA-256 is checked against
  `allowed_mods.yaml`, so a *modified* companion fails the hash check.

The console guard is therefore enforced on a client whose binary the server has
already verified. Removing or patching the enforcer costs the attacker their
connection. Treat it as defence-in-depth, not as a substitute for the server-side
rules (speed check, inventory validation, skill cap), which validate outcomes
rather than inputs.

---

## Modes

| Mode | Effect |
|---|---|
| `open` | No gating. Companion behaves like vanilla. |
| `restricted` **(default)** | Blocks the `cheat` tier, anything `Terminal.commands` flags `IsCheat` (so other mods' cheat commands too), the `risky` tier, and `consoleBlockedCommands`. |
| `whitelist` | Blocks every *registered* command except `consoleAllowedCommands` plus the always-allowed core. Unregistered tokens pass through (they aren't commands; vanilla prints "not a recognized command"), which keeps emotes and chat working. |
| `disabled` | `Console.IsConsoleEnabled()` is forced false — the console window cannot be opened. Chat still works, and chat-typed commands are still gated by the `restricted` rules. |

## Exemptions

| Tier | File | Console guard |
|---|---|---|
| Owner | `owners.yaml` | **Always exempt.** No setting can make the guard apply. |
| Moderator | `moderators.yaml` | Exempt when `consoleGuardExemptModerators: true` (default). |
| Player | — | Subject to the policy. |

The server resolves this and sends the answer as the `exempt` field, so the client
never models the tiers itself.

`sg` commands are typed into the console, so exempting moderators is effectively
required under `disabled` mode unless you are content to lose the `sg` interface for
staff. See `claude/privilege-tiers.md`.

## Bind policies

| Policy | Effect |
|---|---|
| `allow` | Binds untouched. |
| `block` | `bind` / `unbind` refused; already-loaded binds still fire. |
| `purge` **(default)** | Live `Terminal.m_binds` cleared, re-cleared on every `updateBinds()` call, and `bind`/`unbind` refused. The player's saved `ConsoleBindings` pref is left on disk, so their binds return in single-player. |
| `wipe` | As `purge`, plus `m_bindList` is cleared and `updateBinds()` is called to overwrite the persisted pref. Permanent. |

`purge` is the default because it fully addresses the threat (nothing is sitting on
a hotkey while the player is on the server) without destroying a player's local
setup — someone who binds `cheer` to a mouse button for roleplay keeps it offline.
Choose `wipe` if you want binds gone for good.

---

## Risk assessment: every command on the wiki page

Tier column: **C** = blocked as cheat, **R** = blocked as risky, **A** = vanilla
admin command (not blocked client-side — see note), **–** = allowed.

### Player commands (no `devcommands` required)

| Command | Tier | Why it is / isn't a threat |
|---|---|---|
| `bind [keycode] [command]` | **R** | The headline risk. Binds any command — including `devcommands` — to a key, persist to `PlatformPrefs["ConsoleBindings"]` across sessions, fire from `Chat.Update` without the console being open, and dispatch with `skipAllowedCheck: true` so they bypass Valheim's context validation. A player can set binds offline and arrive with them armed. |
| `unbind [keycode]` | **R** | Blocked with `bind` for symmetry; leaving it usable is harmless but confusing when binds are purged. |
| `resetbinds` | **R** | Support tooling for the above. Harmless in itself. |
| `printbinds` | **R** | Reconnaissance — shows a player exactly which binds survived the guard, telling them where the gaps are. |

> The four bind commands are gated by `consoleGuardBindPolicy`, **not** by the risky
> tier. Under `bindPolicy: allow` they stay usable even in `restricted` mode, so an
> operator who doesn't care about binds isn't stuck with them blocked.
| `nomap` | **R** | The wiki is explicit: *"If the server, also toggles the nomap global key."* Global-key mutation affects the whole world, not the caller. |
| `noportals` | **R** | Same — toggles the `noportals` global key server-wide. Turning portals on in a no-portal server voids the server's core ruleset. |
| `setworldmodifier [name] [value]` | **R** | Changes combat difficulty / resource rate / raid frequency / portal rules for the entire world. |
| `setworldpreset [name]` | **R** | Resets every world modifier to a named preset. Same blast radius, one command. |
| `resetworldkeys [name]` | **R** | Resets all world modifiers to default. Undoes a server's configured difficulty in one line. |
| `resetsharedmap` | **R** | Wipes shared cartography-table map data — destroys map exploration the group contributed to collectively. |
| `resetspawn` | **R** | Moves the spawn location. |
| `optterrain` | **R** | Converts every old terrain modification in the loaded area to the new system. A mass ZDO rewrite pushed to the server; a lag and corruption vector if run repeatedly or in a heavily terraformed base. |
| `printseeds` | **R** | Prints seeds *and positions* of nearby dungeons. Information disclosure that trivialises loot routes. |
| `resetknownitems` | **R** | Wipes the character's known recipes. Bound to a key and triggered by accident (or by a "try this bind" social-engineering prompt), it is silent data loss. |
| `resetplayerprefs` | **R** | Resets all saved settings and variables. Also clears the `ConsoleBindings` pref out from under the guard. High support burden, low server risk. |
| `cr` | **R** | Forces an asset unload that normally happens every 20 minutes. Spammable stall. |
| `restartparty` | **R** | Restarts the PlayFab party network — connection churn for the caller and potentially the session. |
| `lodbias [number]` | **R** | The wiki documents it as *"Sets the draw distance for the server."* Blocked out of caution. If your players use it as a local graphics setting, add it to `consoleAllowedCommands` (whitelist mode) or drop it from `RiskyCommands`. |
| `die`, `respawn` | – | Self-kill. Spammable (tombstone litter) but not a security issue. |
| `s`, `say`, `w [player]` | – | Chat. Spam is a moderation problem, handled by the existing shout logging. |
| `ping` | – | Latency measurement. |
| `help`, `clear`, `info`, `xb:version` | – | Local, read-only. |
| `fov`, `maxfps`, `exclusivefullscreen`, `hidebetatext` | – | Local display settings. |
| `filtercraft`, `sortcraft` | – | Local crafting-list UI. |
| `tutorialreset`, `tutorialtoggle` | – | Local tutorial state. |

### Admin commands (gated by the server's admin list)

Not blocked client-side. Valheim already refuses these server-side for non-admins,
so blocking them on the client adds no security while breaking real moderation.
Listed so operators can add them to `consoleBlockedCommands` if their admin list is
broader than they'd like.

| Command | Tier | Risk if the admin list is over-broad |
|---|---|---|
| `ban [name/ip/userID]` | A | A rogue or compromised admin can remove players. |
| `unban [name/ip/userID]` | A | **Cannot undo a ServerGuard ban** — that list is independent by design. It only clears `banlist.txt`. |
| `kick [name/ip/userID]` | A | Disconnects players. |
| `banned` | A | Lists banned users — information disclosure. |
| `save` | A | Forces a world save. Spamming it stalls the server on a large world. |
| `resetworldkeys`, `setworldmodifier`, `setworldpreset` | **R** | Also reachable as player commands — see above. Blocked at the player tier. |

### Cheat commands (`devcommands`)

The wiki states these *"are available in singleplayer or manually hosted mode only.
They do not work on a dedicated server."* That is accurate, and stronger than the wiki
makes it sound: `IsCheatsEnabled()` ANDs `m_cheat` with `ZNet.IsServer()`, so on a
dedicated-server client the flag is irrelevant — `devcommands` cannot turn cheats on
at all.

The reasons to still block them:

- a mod can re-enable them server-side (Server_devcommands exists to do exactly that),
  or register an equivalent command without setting `IsCheat`;
- on a listen server (host-and-play) `IsServer()` is true for the host's client, so
  they *do* work;
- a patched client can flip `m_cheat` or patch `IsCheatsEnabled` outright — attestation
  is the control there, but a redundant list costs nothing.

The table below is therefore mostly a **threat inventory**: what each command would do
if it ran. Where a client owns the ZDOs for nearby objects, these are real server-side
effects — a `spawn` that executes produces a genuine server-side item.

| Command(s) | Why it threatens the server |
|---|---|
| `yesiuseddevcommandsbutiwantmyachievementsanyway` | Sets the `bypasscheatchecks` unique key (1.0), after which the game marks nothing as cheated — blinds the 2.0 cheat-taint rules for that character. Not `IsCheat` in vanilla, so it is listed by name. |
| `devcommands`, `debugmode`, `imacheater` | The master switches. `debugmode` in particular unlocks **keypress** actions (B/K/L/Z/Ctrl+MMB/Shift+C) that never reach `TryRunCommand`, so blocking these two commands is the only way to stop that whole class. |
| `spawn [entity] [amount] [level]` | Client-authoritative entity creation: item duping, boss spawning, mass mob spawns. The wiki notes high-level creatures can drop thousands of items and freeze the game on death — a denial-of-service in one line. |
| `forcedelete [radius] [*name]` | The single most destructive griefing command. Removes objects within up to 50 m. Deletes builds wholesale. |
| `killall`, `killenemies`, `killtame` | Mass kills nearby creatures — `killall` and `killtame` include **other players'** tamed animals. |
| `removedrops`, `removebirds`, `removefish` | Removes world objects and other players' dropped loot. |
| `setkey`, `removekey`, `resetkeys`, `listkeys` | **Global keys are world-wide, persistent progression state.** `setkey` can unlock every biome/boss gate for everyone; `resetkeys` can wipe the server's progression. |
| `event`, `randomevent`, `stopevent` | Triggers or cancels raids for the whole server. |
| `tod`, `skiptime`, `sleep`, `timescale` | World-wide time control. `skiptime` has documented side effects: spawners stop spawning, crops and animals stop growing, and fermenters/smelters/kilns can stop working until time catches up. |
| `env`, `resetenv`, `wind`, `resetwind` | World-wide weather and wind. |
| `players [number]` | Changes the global difficulty scale for everyone. |
| `recall [name]` | **Teleports other players to the caller.** Directly manipulates other people's characters. |
| `goto [x] [z]`, `findtp [text]`, `pos` | Teleport anywhere / locate anything. Bypasses portal rules, ore-transport rules, and base security. |
| `fly`, `freefly`, `ffsmooth` | Movement and camera freedom — base infiltration, terrain bypass, map scouting. |
| `god`, `ghost` | Invulnerability and aggro immunity. Decisive in PvP. |
| `nocost`, `noplacementcost` | Free building and crafting. Breaks the server economy. |
| `itemset [name] [keep]` | Spawns a full premade gear set *and* sets skills to the tier's level in one command. |
| `raiseskill`, `resetskill` | Skill manipulation. Partly covered independently by the server-side skill-cap rule. |
| `location [name]`, `nextseed` | Spawns a location instance / regenerates a dungeon seed. Both **permanently disable saving** on the instance that runs them — catastrophic on a host. |
| `genloc` | Redistributes all unplaced locations. |
| `tame`, `aggravate` | `tame` tames creatures other players are working on; `aggravate` aggros neutrals within 20 m — usable to sic mobs on someone. |
| `setfuel` | Adds (or with a negative value, removes) fuel in all nearby fire sources — including other players' smelters and kilns. |
| `heal`, `puke`, `damage`, `addstatus`, `clearstatus`, `setpower` | Self-state manipulation: infinite sustain, instant power cooldown reset, arbitrary status effects. |
| `resetcharacter` | Wipes all character data. |
| `exploremap`, `resetmap`, `find [text]` | Map intel. `find` pings matching objects on the map — a wallhack for chests, bosses and other players' bases. |
| `printcreatures`, `printlocations` | Entity and location intel. |
| `dpsdebug`, `gc`, `test` | Diagnostics. Low risk individually; cheap to spam. |
| `beard`, `hair`, `model` | Cosmetic. Blocked only because they ride in with the cheat set. |
| `time`, `save` | Read-only / admin-gated. |

---

## Adding to the lists

Per-server additions go in `settings.yaml` and need no code change:

```yaml
consoleBlockedCommands:
  - somemodcommand
  - anothercommand

consoleAllowedCommands:   # only used when consoleGuardMode: whitelist
  - help
  - ping
```

Code-level tiers live in `ClientPlugin.cs`:
`CheatCommands`, `RiskyCommands`, `VanillaAdminCommands`, `AlwaysAllowedCommands`.

Other mods' cheat commands need no listing at all — `IsRegisteredCheatCommand`
reads `ConsoleCommand.IsCheat` out of `Terminal.commands` at call time, so anything
registered with `isCheat: true` is caught dynamically.

---

## Valheim 1.0 (verified against 1.0.7, 2026-09-09)

Nothing here needed changing. What did change around it:

- **`ConsoleCommand`'s constructor grew from 12 to 13 parameters** — `command,
  description, action, isCheat, isNetwork, onlyServer, isSecret, allowInDevBuild,
  hideBehindDevCommands, optionsFetcher, alwaysRefreshTabOptions, remoteCommand,
  onlyAdmin`. ServerGuard never constructs one, so it is unaffected. Mods that *do*
  register commands and were built against the old ctor throw `MissingMethodException`
  at `InitTerminal` (Server Devcommands 1.109 does — see `known-errors.md`, ERROR 16).
- **New field `ConsoleCommand.HideBehindDevCommands`** (bool), alongside the existing
  `IsCheat`, `IsSecret`, `IsNetwork`. `IsCheat` is unchanged, so `ReadBoolMember` — which
  probes `IsCheat` first — still drives dynamic detection. Note the `m_isCheat` spelling
  in that fallback list has never existed on any build; it is harmless dead probing.
- `Terminal`'s fields and `TryRunCommand(string, bool, bool)` / `updateBinds()` are
  byte-identical to pre-1.0. `IsCheatsEnabled()` still resolves. The bind policy is
  unaffected.

### Unclassified commands — open item

An IL scan of `Terminal.InitTerminal` on 1.0.7 found **46 registered commands in no
ServerGuard tier**:

```
achievements adrenaline altbioms biomeinfo catch cinematic cinematicsleep clear
clearpopups confirmcheats die exclusivefullscreen filtercraft findbiome findbiometp
fov getstat haslocation height help hidebetatext info inventoryclean inventorysize
killenemycreatures lodbias maxfps nospawn pevents ping printnetobj removekeyplayer
repairall resetbuildui respawntime say setkeyplayer snow sortcraft stats stopfire
stopsmoke time tombstone tutorialreset updatecover vegetation
```

Most are display/UI/chat commands that this file's design deliberately leaves alone.
The ones worth a look are the world-state and progression ones — `setkeyplayer`,
`removekeyplayer`, `findbiometp`, `nospawn`, `repairall`, `killenemycreatures`, `time`,
`respawntime`. Two caveats before touching the tiers:

1. **Whether any of these are *new* in 1.0 is unknown.** The pre-1.0 assembly was
   overwritten by Steam before a command-list diff could be taken. Several are almost
   certainly long-standing and were considered when the tiers were curated.
2. **An attempt to read each command's `isCheat` argument out of the IL drifted** (a
   linear opcode walk with an incomplete operand table produced `findbiometp → 7` for a
   bool). That output was *not* acted on. If a command is `isCheat: true` it is already
   refused by vanilla on a dedicated-server client regardless (see *Two consequences*
   above) and caught dynamically here; only a non-cheat, state-changing command would be
   a real gap in `RiskyCommands`. Establish the flag properly — at runtime from
   `Terminal.commands`, or with a real IL decoder — before adding anything.

---

## Staff dev commands (2.0)

The guard's inverse: letting staff run the commands the guard exists to block.
Owners get everything; moderators get `moderatorDevcommands`. Settings:
`enableOwnerDevcommands`, `enableModeratorDevcommands`, `moderatorDevcommands`
(see `settings-reference.md`).

### Why vanilla refuses cheats on a dedicated-server client

From `Terminal` / `ZNet`, Valheim 1.0.7 (`ilspycmd` on `assembly_valheim.dll`):

```
Terminal.IsCheatsEnabled()   = m_cheat && ZNet.instance && ZNet.instance.IsServer()
ConsoleCommand.IsValid(ctx)  = (!IsCheat || ctx.IsCheatsEnabled())
                            && (ctx.isAllowedCommand(this) || skipAllowedCheck)   // stub -> true
                            && (!IsNetwork || ZNet.instance)
                            && (!OnlyServer || ZNet.instance.IsServer())
ConsoleCommand ctor          : OnlyServer = onlyServer || onlyAdmin
Terminal.TryRunCommand(text) : valid -> RunAction
                               else if RemoteCommand && !IsServer -> ZNet.RemoteCommand(text)
                               else "not valid in the current context"
ZNet.RPC_RemoteCommand       : ListContainsId(m_adminList, host) ? InternalCommand : "You are not admin"
devcommands handler          : if (!IsServer) ZNet.RemoteCommand("devcommands");  m_cheat = !m_cheat
```

So on a client **two** terms fail: `IsCheat` (via `IsCheatsEnabled`) and `OnlyServer`
(every `onlyAdmin: true` command — `fly`, `god`, `spawn`, `goto`, `heal`, ... — sets it).
A vanilla admin in `adminlist.txt` gets only the `RemoteCommand` subset, executed on the
server console, and only after their `devcommands` has flipped the **server's** global
`m_cheat` (that forwarded `devcommands` is how). `Terminal.m_cheat` is `public static`.

Command flags on 1.0.7, extracted from `InitTerminal` (`scratchpad/decomp/flags.py`):

| Group | Commands | Client-side fate under a grant |
|---|---|---|
| `isCheat` + `onlyAdmin`/`onlyServer`, **not** remote | `fly god ghost heal puke goto spawn itemset recall tame aggravate killall killenemies killtame forcedelete debugmode nocost tod wind resetwind env resetenv snow exploremap resetmap timescale randomevent stopevent resetcharacter setkeyplayer removekeyplayer inventorysize pevents tombstone stopfire stopsmoke setfuel nextseed adrenaline clearstatus` | `IsValid` re-evaluated → runs **locally**, as for a listen-server host |
| `onlyAdmin`, not cheat, not remote | `location vegetation addstatus setpower noportals` | same |
| `isCheat` only | `dpsdebug printcreatures printlocations find findtp nospawn unlockcinematics repairall respawntime biomeinfo altbioms` | passes once `IsCheatsEnabled` reads `m_cheat` — **must** be caught by the moderator list in `ShouldBlockConsoleCommand` |
| `remoteCommand` | `skiptime sleep players resetkeys listkeys genloc removekey resetworldkeys setworldpreset setworldmodifier setkey confirmcheats restartparty` | left invalid on purpose → vanilla forwards → server half authorises |
| routed through `RandEventSystem` | `randomevent stopevent` | run locally, then a routed RPC hits the server's `IsAdmin` check → server half patches those two RPCs |

### Client half

Both patches check `DevAccessGranted` (`devMode == all`, or `list` with a non-empty
set) **and** `IsActiveMultiplayerClient()`, so single-player and hosting are untouched.

- `Patch_Terminal_IsCheatsEnabled` (prefix) — returns `Terminal.m_cheat`, the local
  `devcommands` toggle. Also what lights up the `debugmode` hotkeys (`Player.Update`
  Z/B/K/L, `Minimap` Ctrl+click teleport, `Chat`), which read it directly.
- `Patch_ConsoleCommand_IsValid` (postfix) — for a **permitted**, non-`RemoteCommand`
  command that vanilla rejected: `__result = (!IsCheat || m_cheat) && (!IsNetwork || ZNet.instance)`.
  The `IsCheat` / `OnlyServer` terms are dropped, nothing else is. This also drives
  `ShowCommand`, so `help` and tab-completion list the granted commands after
  `updateCommandList()`.
- `ShouldBlockConsoleCommand` — **first** rule, before every exemption: under
  `devMode == list`, a dev-only command (`CheatCommands` ∪ registered `IsCheat` ∪
  registered `OnlyServer`) is allowed if permitted, else blocked with category
  `moderator`. Permitted commands return early so a moderator who is *not*
  console-guard exempt doesn't hit the cheat tier for `fly`. `devcommands` is always
  permitted once a grant exists. Owners never reach this: `exempt` is `1` for them.
- `AnnounceDevGrant` — one console line when the grant changes (connect or
  hot-reload): what they got, and "type `devcommands`".
- `ResetConsolePolicy` clears `_devMode` / `_devCommands` on `ZNet.Shutdown`.

### Server half (`ServerPlugin.cs`, "Staff dev commands")

- `IsDevcommandAllowed(pid, cmd)` — the one authorisation question. Owner →
  `enableOwnerDevcommands`; moderator → `enableModeratorDevcommands` and not in
  `ModeratorReservedCommands` (`fly debugmode spawn itemset nocost noplacementcost
  location` — never grantable, also filtered out of the pushed list) and (`devcommands`
  or in the list); player → false.
- `Patch_ZNet_RPC_RemoteCommand` (prefix) → `TryHandleStaffRemoteCommand`. For staff
  with a grant: `devcommands` is acknowledged via `RemotePrint` and **not** executed
  (no global `m_cheat` flip on the server); anything else runs on the server
  `Console` with `Terminal.m_cheat` forced `true` for the duration of the call and
  restored after, then log + admin Discord post + `RemotePrint` ack. Staff *without*
  a grant for that command fall through to vanilla, so an `adminlist.txt` entry still
  behaves exactly as pre-2.0.
- `Patch_ZNet_ListContainsId` (postfix) — when `list` is `m_adminList` (reference
  compare via reflection) and the id is an owner with the grant on, returns true.
  This is every `adminlist.txt` check in `ZNet`: `IsAdmin`, `RPC_Kick/Ban/Unban/Save/
  PrintBanned`, `RPC_RemoteCommand`. **Owners are therefore full vanilla admins**
  without touching the file. Moderators are deliberately not promoted here — the
  vanilla admin RPCs are all-or-nothing and can't honour a per-command list.
- `Patch_RandEvent_ConsoleStart` / `_ConsoleReset` (prefix) — `randomevent` /
  `stopevent` for a moderator whose list has them (owners already pass `IsAdmin`).
  Calls the public `StartRandomEvent()` / `ResetRandomEvent()`, so the existing raid
  Discord posts still fire.
- `SendConsolePolicy` appends `devMode|devCsv`; `sg status` prints a `DevCmds` line.
- `OnDevcommandAttemptReceived` handles category `moderator`: admin post, no strike.

### Things worth knowing

- `debugmode` on a moderator's list implicitly grants `fly` (Z) and `nocost` (B) —
  those hotkeys call `ToggleDebugFly` / `ToggleNoPlacementCost` directly, never
  `TryRunCommand`. K/L go through `TryRunCommand("killenemies"/"removedrops")` and
  are list-checked. Documented in the settings template.
- Server-side command output goes to the server console/log, not back to the player
  (vanilla `InternalCommand` behaviour). The player gets the ServerGuard ack line.
- The server-side anti-cheat rules keep applying to what the commands *produce*:
  a moderator's `spawn` (if listed) still meets the inventory check; owners are
  exempt from everything via `AddViolation`. Nothing here loosens those.
- `Server Devcommands` (JereKuusela) covers the same ground with its own permission
  files; running both is redundant. ServerGuard's prefix on `RPC_RemoteCommand`
  returns `false` for granted staff, which also skips that mod's prefix.

---

## Reporting

A blocked command sends `ServerGuard_DevcommandAttempt` with
`"<command>|<category>"`, category ∈ `cheat` / `risky` / `bind` / `notallowed` / `moderator`.

| Category | Server reaction |
|---|---|
| `cheat` | Public Discord post + `DevcommandAttempt` violation (counts toward auto-ban by default). |
| `risky`, `bind`, `notallowed` | Admin channel only + `ConsoleCommandBlocked` violation (informational by default — a curious player typing `bind` should not accrue strikes). |
| `moderator` *(2.0)* | Admin channel only, **no violation** — a moderator asked for a dev command outside `moderatorDevcommands`. |

Companions from 1.6.3 and earlier send the bare command name with no `|category`;
the server treats a missing category as `cheat`, matching the old behaviour.

Set `consoleGuardReportAttempts: false` to keep the blocking but silence the
reporting.
