using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

[BepInPlugin("com.taeguk.valheim.anticheat", "Valheim AntiCheat Server", "1.2.3")]
public class Plugin : BaseUnityPlugin
{
    public static Plugin Instance { get; private set; }
    const int MAX_VIOLATIONS = 3;

    public HashSet<string> Admins        = new();
    public Dictionary<string,string> Reg = new();
    public HashSet<string> AllowedMods   = new();
    public Dictionary<string,int>    Viol= new();

    IDeserializer _deserializer;
    FileSystemWatcher _adminsWatcher, _regWatcher, _modsWatcher;

    void Awake()
    {
        Instance = this;
        try
        {
            Logger.LogInfo("[AntiCheat] Awake starting…");

            // Build YAML deserializer
            _deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            // 1) Ensure configs exist with inline YAML + comments
            EnsureConfig("anticheat_admins.yaml", 
@"# List of admin Steam IDs (exempt from checks)
# Example:
# - ""76561199062837584""
[]
");
            EnsureConfig("anticheat_registered_chars.yaml", 
@"# Registered characters mapping: characterName: SteamID
# Example:
# Cheatest: ""76561199062837584""
{}
");
            EnsureConfig("anticheat_allowed_mods.yaml", 
@"# Allowed mods (by mod name)
# Example:
# - ""EpicLoot""
[]
");
            Logger.LogInfo("[AntiCheat] Config files created or already present.");

            // 2) Load them
            LoadConfigs();
            Logger.LogInfo("[AntiCheat] Configs loaded.");

            // 3) Watch for live edits
            SetupWatchers();
            Logger.LogInfo("[AntiCheat] File watchers set.");

            // 4) Patch handshake
            var harmony = new Harmony("com.taeguk.valheim.anticheat");
            harmony.Patch(
                AccessTools.Method(typeof(ZNet), "RPC_PeerInfo"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(RPC_PeerInfo_Postfix))
            );
            Logger.LogInfo("[AntiCheat] Patched ZNet.RPC_PeerInfo postfix.");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AntiCheat] Exception in Awake: {ex}");
        }
    }

    void EnsureConfig(string fileName, string defaultYaml)
    {
        string path = Path.Combine(Paths.ConfigPath, fileName);
        if (!File.Exists(path))
        {
            File.WriteAllText(path, defaultYaml);
            Instance.Logger.LogInfo($"[AntiCheat] Created default {fileName}");
        }
    }

    void LoadConfigs()
    {
        string dir = Paths.ConfigPath;

        // Admins
        try
        {
            var adminsPath = Path.Combine(dir, "anticheat_admins.yaml");
            var list = _deserializer.Deserialize<List<string>>(File.ReadAllText(adminsPath))
                       ?? new List<string>();
            Admins = new HashSet<string>(list);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AntiCheat] Failed loading admins: {ex}");
        }

        // Registered characters
        try
        {
            var regPath = Path.Combine(dir, "anticheat_registered_chars.yaml");
            var dict = _deserializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(regPath))
                       ?? new Dictionary<string,string>();
            Reg = dict;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AntiCheat] Failed loading registered_chars: {ex}");
        }

        // Allowed mods
        try
        {
            var modsPath = Path.Combine(dir, "anticheat_allowed_mods.yaml");
            var list = _deserializer.Deserialize<List<string>>(File.ReadAllText(modsPath))
                       ?? new List<string>();
            AllowedMods = new HashSet<string>(list);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AntiCheat] Failed loading allowed_mods: {ex}");
        }
    }

    void SetupWatchers()
    {
        string dir = Paths.ConfigPath;
        _adminsWatcher = new FileSystemWatcher(dir, "anticheat_admins.yaml");
        _regWatcher    = new FileSystemWatcher(dir, "anticheat_registered_chars.yaml");
        _modsWatcher   = new FileSystemWatcher(dir, "anticheat_allowed_mods.yaml");

        foreach (var w in new[]{ _adminsWatcher, _regWatcher, _modsWatcher })
        {
            w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            w.Changed += OnConfigChanged;
            w.Created += OnConfigChanged;
            w.EnableRaisingEvents = true;
        }
    }

    void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            LoadConfigs();
            Instance.Logger.LogInfo($"[AntiCheat] Reloaded config: {e.Name}");
        }
        catch (Exception ex)
        {
            Instance.Logger.LogError($"[AntiCheat] Error reloading {e.Name}: {ex}");
        }
    }

    public static void RPC_PeerInfo_Postfix(ZNet __instance, ZRpc rpc, ZPackage pkg)
    {
        var peer = __instance.GetPeers().LastOrDefault(p => p.m_rpc == rpc);
        if (peer == null) return;

        string steamId    = rpc.GetSocket().GetHostName();
        string playerName = peer.m_playerName ?? "";

        if (Instance.Admins.Contains(steamId)) return;

        Instance.Viol.TryGetValue(steamId, out int count);
        bool kicked = false;

        // Allowed-mods check
        var mf = peer.GetType()
                     .GetField("m_mods", BindingFlags.NonPublic | BindingFlags.Instance);
        var mods = mf?.GetValue(peer) as List<string>;
        if (mods != null && mods.Count > 0)
        {
            var bad = mods.Except(Instance.AllowedMods).ToList();
            if (bad.Count > 0)
            {
                kicked = true; count++;
                Instance.Logger.LogWarning(
                    $"[AntiCheat] Unauthorized mods by {steamId}: {string.Join(", ", bad)}"
                );
            }
        }

        // Registration check
        if (!Instance.Reg.TryGetValue(playerName, out var owner) || owner != steamId)
        {
            kicked = true; count++;
            Instance.Logger.LogWarning(
                $"[AntiCheat] Unregistered character '{playerName}' ({steamId})"
            );
        }

        // Auto-ban
        if (count >= MAX_VIOLATIONS)
        {
            kicked = true;
            Instance.Logger.LogError(
                $"[AntiCheat] {steamId} exceeded {count} violations — banning"
            );
        }

        Instance.Viol[steamId] = count;

        if (kicked)
            peer.m_rpc.Invoke("Error", 3);
        else
            Instance.Logger.LogInfo(
                $"[AntiCheat] {playerName} ({steamId}) passed checks"
            );
    }

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

            // Persist YAML mapping
            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            File.WriteAllText(
                Path.Combine(Paths.ConfigPath, "anticheat_registered_chars.yaml"),
                serializer.Serialize(Plugin.Instance.Reg)
            );
        }
    }
}
