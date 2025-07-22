using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Newtonsoft.Json;

[BepInPlugin("com.taeguk.valheim.anticheat", "Valheim AntiCheat Server", "1.4.0")]
public class Plugin : BaseUnityPlugin
{
    public static Plugin Instance { get; private set; }
    const int MAX_VIOLATIONS = 3;
    const float MAX_TELEPORT_DIST = 200f; // Change as needed (meters)

    //─── In-memory config ───
    public HashSet<string> Admins        = new();
    public Dictionary<string,string> Reg = new();
    public HashSet<string> AllowedMods   = new();
    public Dictionary<string,int>    Viol= new();

    public string DiscordWebhookUrl = "";

    IDeserializer _deserializer;
    FileSystemWatcher _adminsWatcher, _regWatcher, _modsWatcher, _mainWatcher;
    static readonly HttpClient _httpClient = new HttpClient();

    // --- For teleport validation ---
    private Dictionary<string, Vector3> LastKnownPositions = new();

    void Awake()
    {
        Instance = this;

        try
        {
            Logger.LogInfo("[AntiCheat] Awake starting…");

            _deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            EnsureConfig("anticheat_config.yaml",
@"# Main AntiCheat settings
# Put your Discord webhook URL here (only one):
# webhook_url: ""https://discord.com/api/webhooks/XXXXXXXX/XXXXXXXX""
webhook_url: """"
");
            EnsureConfig("anticheat_admins.yaml",
@"# List of admin Steam IDs (exempt from all checks)
# - ""76561198000000000""
[]
");
            EnsureConfig("anticheat_registered_chars.yaml",
@"# Registered characters mapping: characterName: SteamID
# MyHero: ""76561198000000000""
{}
");
            EnsureConfig("anticheat_allowed_mods.yaml",
@"# Allowed mods (optional list of client-reported mod names)
# - ""EpicLoot""
[]
");
            Logger.LogInfo("[AntiCheat] Config files created or already present.");

            // load all configs
            LoadConfigs();
            Logger.LogInfo("[AntiCheat] Configs loaded.");

            // watch for edits
            SetupWatchers();
            Logger.LogInfo("[AntiCheat] File watchers set.");

            var harmony = new Harmony("com.taeguk.valheim.anticheat");
            // Peer checks (mods/registration/ban)
            harmony.Patch(
                AccessTools.Method(typeof(ZNet), "RPC_PeerInfo"),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(RPC_PeerInfo_Postfix))
            );
            Logger.LogInfo("[AntiCheat] Patched ZNet.RPC_PeerInfo postfix.");

            // Teleport cheat detection (server-side, doesn't block vanilla portal or admin)
            var teleportTo = AccessTools.Method(typeof(Player), "TeleportTo", new Type[] { typeof(Vector3), typeof(float), typeof(bool) });
            if (teleportTo != null)
            {
                harmony.Patch(
                    teleportTo,
                    prefix: new HarmonyMethod(typeof(Plugin), nameof(Player_TeleportTo_Prefix))
                );
                Logger.LogInfo("[AntiCheat] Patched Player.TeleportTo prefix.");
            }
            else
            {
                Logger.LogWarning("[AntiCheat] Could not patch Player.TeleportTo (method not found, check Valheim version).");
            }
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
            Logger.LogInfo($"[AntiCheat] Created default {fileName}");
        }
    }

    void LoadConfigs()
    {
        string dir = Paths.ConfigPath;

        // Main config (webhook)
        try
        {
            var mainText = File.ReadAllText(Path.Combine(dir, "anticheat_config.yaml"));
            var mainDict = _deserializer.Deserialize<Dictionary<string,string>>(mainText)
                           ?? new Dictionary<string,string>();
            mainDict.TryGetValue("webhook_url", out DiscordWebhookUrl);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AntiCheat] Failed loading main config: {ex}");
        }

        // Admins
        try
        {
            var list = _deserializer.Deserialize<List<string>>(
                File.ReadAllText(Path.Combine(dir, "anticheat_admins.yaml"))
            ) ?? new List<string>();
            Admins = new HashSet<string>(list);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AntiCheat] Failed loading admins: {ex}");
        }

        // Registered chars
        try
        {
            var dict = _deserializer.Deserialize<Dictionary<string,string>>(
                File.ReadAllText(Path.Combine(dir, "anticheat_registered_chars.yaml"))
            ) ?? new Dictionary<string,string>();
            Reg = dict;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AntiCheat] Failed loading registered_chars: {ex}");
        }

        // Allowed mods
        try
        {
            var list = _deserializer.Deserialize<List<string>>(
                File.ReadAllText(Path.Combine(dir, "anticheat_allowed_mods.yaml"))
            ) ?? new List<string>();
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

        _mainWatcher = new FileSystemWatcher(dir, "anticheat_config.yaml");
        _adminsWatcher = new FileSystemWatcher(dir, "anticheat_admins.yaml");
        _regWatcher    = new FileSystemWatcher(dir, "anticheat_registered_chars.yaml");
        _modsWatcher   = new FileSystemWatcher(dir, "anticheat_allowed_mods.yaml");

        foreach (var w in new[]{ _mainWatcher, _adminsWatcher, _regWatcher, _modsWatcher })
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
            Logger.LogInfo($"[AntiCheat] Reloaded config: {e.Name}");
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AntiCheat] Error reloading {e.Name}: {ex}");
        }
    }

    // --- Peer checks (mods, registration, auto-ban) ---
    public static void RPC_PeerInfo_Postfix(ZNet __instance, ZRpc rpc, ZPackage pkg)
    {
        var peer = __instance.GetPeers().LastOrDefault(p => p.m_rpc == rpc);
        if (peer == null) return;

        string steamId    = rpc.GetSocket().GetHostName();
        string playerName = peer.m_playerName ?? "";

        if (Instance.Admins.Contains(steamId)) return;

        Instance.Viol.TryGetValue(steamId, out int count);
        bool kicked = false;

        // Allowed-mods
        var mf = peer.GetType()
                     .GetField("m_mods", BindingFlags.NonPublic | BindingFlags.Instance);
        var mods = mf?.GetValue(peer) as List<string>;
        if (mods != null && mods.Count > 0)
        {
            var bad = mods.Except(Instance.AllowedMods).ToList();
            if (bad.Count > 0)
            {
                kicked = true; count++;
                string msg = $"[AntiCheat] Unauthorized mods by {steamId}: {string.Join(", ", bad)}";
                Plugin.Instance.Logger.LogWarning(msg);
                _ = Plugin.Instance.SendDiscordLogAsync(msg);
            }
        }

        // Registration
        if (!Instance.Reg.TryGetValue(playerName, out var owner) || owner != steamId)
        {
            kicked = true; count++;
            string msg = $"[AntiCheat] Unregistered character '{playerName}' ({steamId})";
            Plugin.Instance.Logger.LogWarning(msg);
            _ = Plugin.Instance.SendDiscordLogAsync(msg);
        }

        // Auto-ban
        if (count >= MAX_VIOLATIONS)
        {
            kicked = true;
            string msg = $"[AntiCheat] {steamId} exceeded {count} violations — banning";
            Plugin.Instance.Logger.LogError(msg);
            _ = Plugin.Instance.SendDiscordLogAsync(msg);
        }

        Instance.Viol[steamId] = count;

        if (kicked)
            peer.m_rpc.Invoke("Error", 3);
        else
            Plugin.Instance.Logger.LogInfo($"[AntiCheat] {playerName} ({steamId}) passed checks");
    }

    /// <summary>
    /// Sends a log message to Discord via webhook, if configured.
    /// </summary>
    public async Task SendDiscordLogAsync(string content)
    {
        if (string.IsNullOrEmpty(DiscordWebhookUrl)) return;

        try
        {
            var payload = new { content };
            var json = JsonConvert.SerializeObject(payload);
            using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
            await _httpClient.PostAsync(DiscordWebhookUrl, httpContent);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AntiCheat] Failed to send Discord log: {ex}");
        }
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
            Plugin.Instance.Logger.LogInfo($"[AntiCheat] Registered '{name}' → {sid}");

            // persist YAML mapping
            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            File.WriteAllText(
                Path.Combine(Paths.ConfigPath, "anticheat_registered_chars.yaml"),
                serializer.Serialize(Plugin.Instance.Reg)
            );
        }
    }

    // --- Teleport validation patch ---
    public static bool Player_TeleportTo_Prefix(Player __instance, Vector3 pos, float yaw, bool distantTeleport)
    {
        try
        {
            // Only do checks on server (if player is a remote peer)
            if (!ZNet.instance || !ZNet.instance.IsServer()) return true;
            if (__instance == null) return true;

            string playerName = __instance.GetPlayerName();
            string steamId = __instance.GetZDOID().ToString(); // fallback if needed

            // Try to get SteamID for this player, safer if peer is available
            ZNetPeer peer = ZNet.instance.GetPeers().FirstOrDefault(p => p.m_playerName == playerName);
            if (peer != null)
                steamId = peer.m_rpc.GetSocket().GetHostName();

            // Admins always allowed
            if (Plugin.Instance.Admins.Contains(steamId))
                return true;

            // Portal/distant teleport: skip check (allowed)
            if (distantTeleport)
                return true;

            // Track last known position for this player
            if (!Plugin.Instance.LastKnownPositions.TryGetValue(steamId, out Vector3 lastPos))
                lastPos = __instance.transform.position;

            float dist = Vector3.Distance(lastPos, pos);

            // Arbitrary: large instant jump, likely a cheat, unless using a portal/distantTeleport
            if (dist > MAX_TELEPORT_DIST)
            {
                string msg = $"[AntiCheat] {playerName} ({steamId}) tried illegal teleport from {lastPos} to {pos} ({dist:F1}m)";
                Plugin.Instance.Logger.LogWarning(msg);
                _ = Plugin.Instance.SendDiscordLogAsync(msg);

                // Option: Kick the player? Or just warn?
                ZNetPeer offender = ZNet.instance.GetPeers().FirstOrDefault(p => p.m_playerName == playerName);
                if (offender != null)
                {
                    // Kick
                    offender.m_rpc.Invoke("Error", 3);
                }
                // Block teleport
                return false;
            }

            // If allowed, update last known position
            Plugin.Instance.LastKnownPositions[steamId] = pos;
        }
        catch (Exception ex)
        {
            Plugin.Instance.Logger.LogError($"[AntiCheat] Exception in TeleportTo: {ex}");
        }
        return true;
    }
}
