using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimServerGuard
{
    // The single BepInEx entry point for ServerGuard. One DLL, one GUID, installed on
    // the dedicated server AND on every player's client.
    //
    // At Awake it decides which half of the mod this process needs and attaches only
    // that half as a sibling component on the BepInEx manager object:
    //
    //   dedicated server (headless, no graphics device)  ->  ServerPlugin
    //   everything else (a player's game)                ->  ClientPlugin
    //
    // Each half owns its own Harmony instance and patches ONLY its own nested patch
    // classes (see PatchNested), so no server-side patch is ever applied on a client
    // and vice versa. The two halves never run in the same process: a player hosting a
    // listen server from their own game gets the client half, exactly as before the
    // merge (the server half has always been for dedicated servers).
    [BepInPlugin(GUID, NAME, VERSION)]
    public class ServerGuardPlugin : BaseUnityPlugin
    {
        public const string GUID    = "com.taeguk.valheim.serverguard";
        public const string NAME    = "Valheim ServerGuard";
        public const string VERSION = "2.0.0";

        // Pre-2.0 GUID of the separate client companion package. Still recognised by
        // the server's allowed_mods.yaml parser so existing `required_mods:` entries keep
        // working after the merge (see ServerPlugin.ParseAllowedList).
        public const string LEGACY_CLIENT_GUID = "com.taeguk.valheim.serverguard.client";

        internal static ServerGuardPlugin Instance;
        internal static ManualLogSource Log;

        // Which half is running in this process. Set once in Awake, read by the two
        // halves' self-tests and log lines.
        internal static bool IsServerSide  { get; private set; }
        internal static bool IsClientSide  { get; private set; }

        private ConfigEntry<string> _modeOverride;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // BepInEx config (BepInEx/config/com.taeguk.valheim.serverguard.cfg). The only
            // knob here is the side selector; every real setting lives in the YAML files
            // under BepInEx/config/ServerGuard/ as before.
            _modeOverride = Config.Bind("General", "Mode", "auto",
                "Which half of ServerGuard to run in this process.\n" +
                "auto   - dedicated server (headless) runs the server half, a normal game runs the client half (recommended)\n" +
                "server - force the server half\n" +
                "client - force the client half");

            var mode = (_modeOverride.Value ?? "auto").Trim().ToLowerInvariant();
            bool headless = IsHeadless();
            bool runServer = mode == "server" || (mode != "client" && headless);

            IsServerSide = runServer;
            IsClientSide = !runServer;

            Log.LogInfo($"[ServerGuard] v{VERSION} starting as {(runServer ? "SERVER" : "CLIENT")} " +
                        $"(mode={mode}, headless={headless}).");

            try
            {
                if (runServer) gameObject.AddComponent<ServerPlugin>();
                else           gameObject.AddComponent<ClientPlugin>();
            }
            catch (Exception ex)
            {
                Log.LogError($"[ServerGuard] Failed to start the {(runServer ? "server" : "client")} half: {ex}");
            }
        }

        // A Valheim dedicated server runs with `-batchmode -nographics`, which leaves
        // Unity with no graphics device at all. A player's game always has one, even
        // when hosting a listen server. This is the same test every dual-sided Valheim
        // mod uses.
        private static bool IsHeadless()
        {
            try { return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null; }
            catch { return false; }
        }

        // Applies every [HarmonyPatch] class nested (at any depth) inside `outer`, and
        // nothing else. Harmony.PatchAll() would sweep the whole assembly and apply the
        // OTHER half's patches too; Harmony.PatchAll(Type) only looks at that one type
        // and ignores nested classes. This walks the nesting explicitly.
        internal static int PatchNested(Harmony harmony, Type outer)
        {
            int applied = 0;
            foreach (var t in outer.GetNestedTypes(AccessTools.all))
            {
                try
                {
                    if (t.GetCustomAttributes(typeof(HarmonyAttribute), true).Any())
                    {
                        harmony.CreateClassProcessor(t).Patch();
                        applied++;
                    }
                }
                catch (Exception ex)
                {
                    Log?.LogError($"[ServerGuard] Harmony patch class {t.Name} failed to apply: {ex}");
                }
                applied += PatchNested(harmony, t);
            }
            return applied;
        }
    }
}
