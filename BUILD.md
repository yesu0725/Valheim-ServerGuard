# Building Valheim ServerGuard

This guide walks through building `Valheim-ServerGuard.dll` from source on Windows.

---

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| **.NET SDK** | 6.0+ (or 8.0+) | Provides the `dotnet` CLI. The project itself targets `net462`, but the SDK builds it. |
| **Valheim (dedicated or client install)** | Any current version | Needed for reference assemblies (`assembly_valheim.dll`, `UnityEngine.dll`, `assembly_utils.dll`). |
| **BepInEx 5.4.22** | Pre-installed on the target server | Runtime host for the plugin. |
| **Git** (optional) | latest | For cloning the repo. |

> The NuGet packages (`BepInEx.Core`, `HarmonyX`, `YamlDotNet`, `Newtonsoft.Json`) are restored automatically — no manual download required.

---

## 1. Clone the repository

```powershell
git clone https://github.com/yesu0725/Valheim-ServerGuard.git
cd Valheim-ServerGuard
```

---

## 2. Set the `VALHEIM_PATH` environment variable

The `.csproj` resolves Valheim DLLs via `$(VALHEIM_PATH)`. Point it at your Valheim install root (the folder containing `valheim.exe` or `valheim_server.exe`).

### PowerShell — current session only

```powershell
$env:VALHEIM_PATH = "C:\Program Files (x86)\Steam\steamapps\common\Valheim"
```

### PowerShell — persist for the user

```powershell
[Environment]::SetEnvironmentVariable("VALHEIM_PATH", "C:\Program Files (x86)\Steam\steamapps\common\Valheim", "User")
```

Restart the shell after setting it persistently.

**Verify the path resolves:**

```powershell
Test-Path "$env:VALHEIM_PATH\valheim_Data\Managed\assembly_valheim.dll"
# Should print: True
```

If you only have a dedicated server install, point at the server folder — its `valheim_server_Data\Managed` directory holds the same assemblies. In that case, edit `Valheim-ServerGuard.csproj` and change `valheim_Data` to `valheim_server_Data`.

---

## 3. Restore and build

From the repo root:

```powershell
dotnet restore
dotnet build -c Release
```

The output DLL lands at:

```
bin\Release\net462\Valheim-ServerGuard.dll
```

Single-line equivalent:

```powershell
dotnet build Valheim-ServerGuard.csproj -c Release
```

---

## 4. Deploy to the server

Copy the built DLL into the BepInEx plugins folder of your dedicated server:

```powershell
Copy-Item "bin\Release\net462\Valheim-ServerGuard.dll" `
          "<server-root>\BepInEx\plugins\Valheim-ServerGuard.dll"
```

Restart the server. On first launch, ServerGuard creates its config tree under:

```
<server-root>\BepInEx\config\ServerGuard\conf\
```

See [DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md) for full server-side configuration steps.

---

## Troubleshooting

### `error MSB3245: Could not resolve this reference. Could not locate the assembly "assembly_valheim"`
`VALHEIM_PATH` is unset, wrong, or points at a server install while the `.csproj` expects a client install. Verify with the `Test-Path` check in step 2, or edit the `<HintPath>` entries in [Valheim-ServerGuard.csproj](Valheim-ServerGuard.csproj) directly.

### `The TargetFramework value 'net462' was not recognized`
Install the .NET Framework 4.6.2 targeting pack, or use a recent .NET SDK (6+) which ships with the necessary reference assemblies on Windows.

### Plugin loads but does nothing
Confirm BepInEx itself is installed and running — check `BepInEx\LogOutput.log` for a line like `[Info: ServerGuard] Loaded (v1.3.0). ...`.

### Mismatched HarmonyX / BepInEx versions
The project pins `BepInEx.Core 5.4.22` and `HarmonyX 2.10.1`. If your server runs BepInEx 6 (IL2CPP / Mono pre-release), you must update those package versions and possibly adjust patch signatures.

---

## Build matrix (quick reference)

| Goal | Command |
|---|---|
| Debug build | `dotnet build -c Debug` |
| Release build | `dotnet build -c Release` |
| Clean | `dotnet clean` |
| Restore only | `dotnet restore` |
| Rebuild from scratch | `dotnet build -c Release --no-incremental` |
