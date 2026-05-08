# 🚀 Valheim ServerGuard - Windows Server Deployment Guide

## Prerequisites

Before starting, ensure you have:
- ✅ Valheim Dedicated Server installed on your computer (from Steam)
- ✅ .NET SDK 6.0 or higher installed ([Download here](https://dotnet.microsoft.com/en-us/download))
- ✅ Visual Studio Code or Visual Studio installed (optional, but recommended)

---

## 📍 Finding Your Valheim Server Directory

### Step 1: Locate Your Valheim Server Installation

**On Windows:**

1. Open **Steam**
2. Go to **Library** → **Tools**
3. Find **Valheim Dedicated Server**
4. Right-click → **Properties** → **Local Files** → **Browse...**
5. You'll see a folder like: `C:\Program Files (x86)\Steam\steamapps\dedicated_servers\valheim_server`

**Note the path!** You'll need it throughout this guide.

Example paths:
```
C:\Program Files (x86)\Steam\steamapps\dedicated_servers\valheim_server
D:\Games\Steam\steamapps\dedicated_servers\valheim_server
```

---

## 🔽 Step 1: Download ServerGuard Plugin

### Option A: Download from GitHub (Recommended)

1. Go to: https://github.com/yesu0725/Valheim-ServerGuard
2. Click **Code** → **Download ZIP**
3. Extract the ZIP file to a folder like `C:\Valheim-ServerGuard`

### Option B: Clone with Git

Open **Command Prompt** or **PowerShell** and run:

```bash
git clone https://github.com/yesu0725/Valheim-ServerGuard.git
cd Valheim-ServerGuard
```

---

## 🛠️ Step 2: Install BepInEx (Plugin Framework)

BepInEx is the framework that ServerGuard runs on. Your Valheim server needs it.

### Step 1: Download BepInEx

1. Visit: https://github.com/BepInEx/BepInEx/releases
2. Download the **latest release** (look for a version number like `5.4.x`)
3. Choose the **Windows x64** version (BepInEx-5.4.x-win-x64.zip)
4. Extract to your Valheim server directory

Your structure should now look like:
```
C:\Program Files (x86)\Steam\steamapps\dedicated_servers\valheim_server\
├── BepInEx/              ← New folder
│   ├── plugins/
│   ├── patchers/
│   └── config/
├── valheim_server.exe    ← Existing
└── ...
```

### Step 2: Verify BepInEx Installation

1. **Run the server once** to initialize BepInEx:
   - Open Command Prompt
   - Navigate to your server directory
   - Run: `valheim_server.exe`
   - Wait 30 seconds
   - Press Ctrl+C to stop

2. Check that `BepInEx/` folder now contains:
   - `plugins/` folder
   - `config/` folder
   - `LogOutput.log` file

✅ If these exist, BepInEx is installed correctly!

---

## 🔧 Step 3: Build ServerGuard Plugin

### Step 1: Open Command Prompt

1. Navigate to where you downloaded ServerGuard
2. Open **Command Prompt** or **PowerShell** in the folder

```bash
cd C:\Valheim-ServerGuard
```

### Step 2: Install Dependencies

Run this command to restore required packages:

```bash
dotnet restore
```

**Expected output:**
```
Restore completed in X.XXs
```

### Step 3: Build the Plugin

```bash
dotnet build -c Release
```

**Expected output:**
```
Build succeeded.
```

**If it fails:**
- Ensure .NET SDK 6.0+ is installed
- Check the error messages carefully
- Verify all files are in the right place

---

## 📦 Step 4: Copy Plugin to Server

After building, you need to copy the plugin files to your server.

### Step 1: Locate the Built Files

The compiled files should be in:
```
C:\Valheim-ServerGuard\bin\Release\net6.0\
```

Look for files named:
- `Plugin.dll` (or the plugin assembly name)
- `YamlDotNet.dll`
- `Newtonsoft.Json.dll`

### Step 2: Copy to BepInEx Plugins Folder

1. Create a subfolder: `BepInEx/plugins/ServerGuard/`
2. Copy these files into it:
   - `Plugin.dll`
   - `YamlDotNet.dll`
   - `Newtonsoft.Json.dll`

**Full path:**
```
C:\Program Files (x86)\Steam\steamapps\dedicated_servers\valheim_server\BepInEx\plugins\ServerGuard\
├── Plugin.dll
├── YamlDotNet.dll
└── Newtonsoft.Json.dll
```

### Step 3: Verify Files Are Copied

- Check the `ServerGuard/` folder has all 3 DLL files
- If any are missing, the plugin won't work

---

## 🚀 Step 5: Initialize ServerGuard Config Files

### Step 1: Run Server to Generate Configs

Open **Command Prompt** in your server directory:

```bash
cd "C:\Program Files (x86)\Steam\steamapps\dedicated_servers\valheim_server"
valheim_server.exe
```

**Wait 2-3 minutes** for the server to fully start.

**Expected output in console:**
```
[ServerGuard] Loaded (YAML). Enforcement: ON
[ServerGuard] settings.yaml loaded
[ServerGuard] admins.yaml loaded
```

✅ If you see these messages, ServerGuard started successfully!

Press **Ctrl+C** to stop the server.

### Step 2: Verify Config Files Created

Check if these files were auto-created:

```
BepInEx/config/ServerGuard/conf/
├── settings.yaml              ← Main settings
├── admins.yaml               ← Admin whitelist
├── ignore_mods.yaml          ← Allowed mod tokens
├── registrations.yaml        ← Character tracking
└── violations.yaml           ← Violation log
```

✅ All files should be present!

---

## ⚙️ Step 6: Configure ServerGuard

Now customize ServerGuard for your server.

### Step 1: Open settings.yaml

Navigate to:
```
BepInEx/config/ServerGuard/conf/settings.yaml
```

Open with **Notepad** or your favorite text editor.

### Step 2: Configure Settings

The file contains these key settings:

```yaml
# Enable/disable enforcement
Enforce: true

# Violation threshold before auto-ban
ViolationThreshold: 3

# Enable mod detection
AggressiveNoModCheck: true

# Character limit per SteamID (default: 1)
CharacterLimit: 1

# Discord webhook URL (optional)
discordWebhookUrl: ""

# Kick message when player violates rules
kickMessage: "You cannot join: server security policy violation. Contact an administrator."

# Ban reason when threshold exceeded
banReason: "Auto-banned due to repeated security violations."
```

**Recommended starting config (Vanilla-only):**
```yaml
Enforce: true
AggressiveNoModCheck: true
ViolationThreshold: 3
CharacterLimit: 1
discordWebhookUrl: ""
```

### Step 3: Save the File

Press **Ctrl+S** in Notepad to save.

---

## 🎮 Step 7: Configure Allowed Mods

### Step 1: Open ignore_mods.yaml

Navigate to:
```
BepInEx/config/ServerGuard/conf/ignore_mods.yaml
```

Open with **Notepad**.

### Step 2: Add Allowed Mods

For **vanilla-only server**, leave default:
```yaml
ignore_mods:
  - Jotunn
  - ServerSync
```

For **modded server**, add mods you allow:
```yaml
ignore_mods:
  - Jotunn
  - ServerSync
  - ValheimPlus
  - PlanBuild
```

### Step 3: Save

Press **Ctrl+S**.

---

## 👤 Step 8: Add Admin Users (Optional)

### Step 1: Open admins.yaml

Navigate to:
```
BepInEx/config/ServerGuard/conf/admins.yaml
```

### Step 2: Get Your Steam ID

1. Visit: https://steamid.io/
2. Enter your Steam username
3. Copy the **steamID64** (looks like: `76561198012345678`)

### Step 3: Add to Admin List

Edit the file:
```yaml
admins:
  - "76561198012345678"  # Your SteamID
  - "76561198087654321"  # Friend's SteamID (optional)
```

**Admins bypass ALL checks!**

### Step 4: Save

Press **Ctrl+S**.

---

## 🔗 Step 9: (Optional) Enable Discord Logging

### Step 1: Create Discord Webhook

1. Open your Discord server
2. Go to **Channel Settings** → **Integrations** → **Webhooks**
3. Click **New Webhook**
4. Copy the **Webhook URL** (looks like: `https://discordapp.com/api/webhooks/...`)

### Step 2: Add to settings.yaml

Open `BepInEx/config/ServerGuard/conf/settings.yaml`

Find and edit:
```yaml
discordWebhookUrl: "https://discordapp.com/api/webhooks/YOUR_WEBHOOK_URL_HERE"
```

### Step 3: Save

Press **Ctrl+S**.

**Now violations will be logged to Discord in real-time!**

---

## ✅ Step 10: Test the Installation

### Step 1: Start the Server

Open **Command Prompt** in your server directory:

```bash
cd "C:\Program Files (x86)\Steam\steamapps\dedicated_servers\valheim_server"
valheim_server.exe
```

**Wait 3-5 minutes** for the server to fully start.

### Step 2: Check Logs

Look for messages like:
```
[ServerGuard] Loaded (YAML). Enforcement: ON
[ServerGuard] settings.yaml loaded
[ServerGuard] admins.yaml loaded
[ServerGuard] ignore_mods.yaml loaded
```

✅ If you see these, ServerGuard is working!

### Step 3: Test with Vanilla Client

1. Launch Valheim on your local computer
2. Connect to your server (localhost)
3. Create a character and join
4. **You should join successfully** ✅

### Step 4: Test with Modded Client (Optional)

1. Install a mod on your client
2. Try to join your server
3. **You should be kicked** (if mod not allowed) or **allowed** (if in ignore list)

---

## 📊 Step 11: Monitor Your Server

### Check Violations

After running the server, check:
```
BepInEx/config/ServerGuard/conf/violations.yaml
```

You should see any rule violations recorded.

### Check Discord (if configured)

Your Discord channel should show:
- Player connections
- Violations
- Auto-bans

### Check Server Logs

Open:
```
BepInEx/LogOutput.log
```

Contains detailed ServerGuard activity.

---

## 🎯 Quick Reference: Common Tasks

### Restart the Server with New Config

1. Stop the server: **Ctrl+C**
2. Edit your YAML files
3. Start the server again: `valheim_server.exe`
4. **Changes auto-reload!** No plugins to reinstall.

### Add a New Mod to Allowlist

1. Edit `ignore_mods.yaml`
2. Add the mod name to the list
3. Save
4. **Changes apply immediately** - no restart needed!

### Disable a Player

1. Edit `violations.yaml`
2. Set their violation count high:
   ```yaml
   "76561198000000000":
     ClientModded: 999
   ```
3. Save
4. They'll be auto-banned on next connection

---

## 🐛 Troubleshooting

### Problem: Server Won't Start

**Error:** "Plugin failed to load"

**Solution:**
1. Check all 3 DLLs are in `BepInEx/plugins/ServerGuard/`
2. Verify .NET SDK 6.0+ is installed
3. Check `BepInEx/LogOutput.log` for errors
4. Rebuild the plugin: `dotnet build -c Release`

---

### Problem: ServerGuard Not Loading

**Error:** No ServerGuard messages in console

**Solution:**
1. Confirm `Plugin.dll` exists in plugins folder
2. Check BepInEx is properly installed (run server once)
3. Look for error messages in `BepInEx/LogOutput.log`
4. Ensure no file path typos

---

### Problem: Server Performance Issues

**Error:** Server gets laggy after ServerGuard starts

**Solution:**
1. Check `BepInEx/LogOutput.log` for errors
2. Verify mod detection settings in `settings.yaml`
3. Try disabling Discord logging temporarily
4. Check system resources (CPU, RAM)

---

## 📝 Checklist for Successful Deployment

- [ ] BepInEx installed in server directory
- [ ] ServerGuard Plugin.dll copied to `BepInEx/plugins/ServerGuard/`
- [ ] YamlDotNet.dll and Newtonsoft.Json.dll also copied
- [ ] Server started once to generate config files
- [ ] `settings.yaml` configured with desired settings
- [ ] `ignore_mods.yaml` configured with allowed mods
- [ ] `admins.yaml` has admin SteamIDs (optional)
- [ ] Discord webhook added to settings (optional)
- [ ] Server started successfully with ServerGuard messages
- [ ] Vanilla client can join successfully
- [ ] `violations.yaml` exists and is empty (before testing)

---

## 🎊 You're Done!

Your Valheim ServerGuard is now deployed and protecting your server! 🚀

**Next Steps:**
1. **Monitor** - Watch violations in YAML and Discord
2. **Tune** - Adjust settings based on your community
3. **Enforce** - Decide on vanilla-only or mod-friendly policy
4. **Communicate** - Tell your players about the security policy

---

## 📞 Need Help?

- **GitHub Repository:** https://github.com/yesu0725/Valheim-ServerGuard
- **Check Server Logs:** `BepInEx/LogOutput.log`
- **Review Config:** `BepInEx/config/ServerGuard/conf/`

---

**Happy protecting your Valheim server!** ⚔️🛡️
