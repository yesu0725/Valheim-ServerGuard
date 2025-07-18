using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Newtonsoft.Json;

[BepInPlugin("com.taeguk.valheim.anticheat", "Valheim AntiCheat Server", "1.1.4")]
public class Plugin : BaseUnityPlugin
{
    public static Plugin Instance { get; private set; }
    const int MAX_VIOLATIONS = 3;

    // ─── Configuration stores ───
    public HashSet<string> Admins        = new();   // exempt SteamIDs
    public Dictionary<string,string> Reg = new();   // character → SteamID
    public HashSet<string> AllowedMods   = new();   // mod names allowed
    public Dictionary<string,int>    Viol= new();   // SteamID → violation count

    Harmony _harmony;

    void Awake()
    {
        Instance = this;

        // Load config files
        LoadConfig("anticheat_admins.json",             ref Admins);
        LoadConfig("anticheat_registered_chars.json",  ref Reg);
        LoadConfig("anticheat_allowed_mods.json",      ref AllowedMods);

        // Patch RPC_PeerInfo with a postfix
        _harmony = new Harmony("com.taeguk.valheim.anticheat");
        var rpcInfo = AccessTools.Method(typeof(ZNet), "RPC_PeerInfo");
        var postfix = AccessTools.Method(typeof(Plugin), nameof(RPC_PeerInfo_Postfix));
        _harmony.Patch(rpcInfo, postfix: new HarmonyMethod(postfix));
        Logger.LogInfo("[AntiCheat] Patched RPC_PeerInfo postfix");
    }

    void LoadConfig<T>(string filename, ref T target)
    {
        string path = Path.Combine(Paths.ConfigPath, filename);
        if (!File.Exists(path))
            File.WriteAllText(path, JsonConvert.SerializeObject(target, Formatting.Indented));
        try
        {
            target = JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
            Logger.LogInfo($"[AntiCheat] Loaded {filename}");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AntiCheat] Error loading {filename}: {ex.Message}");
        }
    }

    // Runs after Valheim has processed the handshake and added the peer
    public static void RPC_PeerInfo_Postfix(ZNet __instance, ZRpc rpc, ZPackage pkg)
    {
        // Find the peer just added for this rpc
        var peer = __instance.GetPeers()
                     .LastOrDefault(p => p.m_rpc == rpc);
        if (peer == null) return;

        // Grab SteamID and playerName (now populated)
        string steamId    = rpc.GetSocket().GetHostName();
        string playerName = peer.m_playerName ?? "";

        // Admins bypass all checks
        if (Instance.Admins.Contains(steamId)) return;

        // Initialize violation count
        Instance.Viol.TryGetValue(steamId, out int count);
        bool kicked = false;

        // 1) Mod‐whitelist check (if peer.m_mods exists)
        var modsField = peer.GetType()
                            .GetField("m_mods", BindingFlags.NonPublic | BindingFlags.Instance);
        var mods = modsField?.GetValue(peer) as List<string>;
        if (mods != null && mods.Count > 0)
        {
            var bad = mods.Except(Instance.AllowedMods).ToList();
            if (bad.Count > 0)
            {
                kicked = true;
                count++;
                Instance.Logger.LogWarning(
                  $"[AntiCheat] Unauthorized mods by {steamId}: {string.Join(", ", bad)}"
                );
            }
        }

        // 2) Registered-character check
        if (!Instance.Reg.TryGetValue(playerName, out var owner) || owner != steamId)
        {
            kicked = true;
            count++;
            Instance.Logger.LogWarning(
              $"[AntiCheat] Unregistered character '{playerName}' ({steamId})"
            );
        }

        // 3) Auto‐ban threshold
        if (count >= MAX_VIOLATIONS)
        {
            kicked = true;
            Instance.Logger.LogError(
              $"[AntiCheat] {steamId} exceeded {count} violations — banning"
            );
        }

        // Persist updated count
        Instance.Viol[steamId] = count;

        // Kick if needed
        if (kicked)
            peer.m_rpc.Invoke("Error", 3);
        else
            Instance.Logger.LogInfo(
              $"[AntiCheat] {playerName} ({steamId}) passed checks"
            );
    }

    // In-game /register_char chat command
    [HarmonyPatch(typeof(Chat), "OnNewChatMessage")]
    public static class RegisterCmd
    {
        public static void Postfix(Talker.Type type, string sender, string text)
        {
            if (!text.Equals("/register_char", StringComparison.OrdinalIgnoreCase))
                return;

            var pl = Player.m_localPlayer;
            if (pl == null) return;

            string name = pl.GetPlayerName();
            string sid  = ZNet.GetUID().ToString();
            Plugin.Instance.Reg[name] = sid;
            pl.Message(MessageHud.MessageType.TopLeft,
                       $"Registered '{name}' → {sid}");
            Plugin.Instance.Logger.LogInfo(
                $"[AntiCheat] Registered '{name}' → {sid}"
            );

            File.WriteAllText(
                Path.Combine(Paths.ConfigPath, "anticheat_registered_chars.json"),
                JsonConvert.SerializeObject(Plugin.Instance.Reg, Formatting.Indented)
            );
        }
    }
}