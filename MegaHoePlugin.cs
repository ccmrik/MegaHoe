using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MegaHoe
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class MegaHoePlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.rik.megahoe";
        public const string PluginName = "Mega Hoe";
        public const string PluginVersion = "4.8.3";

        private static ManualLogSource _logger;
        private static Harmony _harmony;
        private static FileSystemWatcher _configWatcher;
        private static ConfigFile _config;

        public static ConfigEntry<KeyCode> TerrainFlattenKey;
        public static ConfigEntry<KeyCode> TerrainResetKey;
        public static ConfigEntry<float> OperationRadius;
        public static ConfigEntry<KeyCode> BiomePaintKey;
        public static ConfigEntry<KeyCode> BiomePaintCycleKey;
        public static ConfigEntry<float> BiomePaintRadius;
        public static ConfigEntry<KeyCode> HeightLimitBypassKey;
        public static ConfigEntry<bool> DebugMode;

        public static bool HeightLimitBypassed = false;

        private static string _lastWorldName = "";

        private void Awake()
        {
            _logger = Logger;
            _logger.LogInfo($"{PluginName} v{PluginVersion} loading...");

            try
            {
                if (File.Exists(Config.ConfigFilePath))
                {
                    MigrateConfig(Config.ConfigFilePath);
                    Config.Reload();
                }

                TerrainFlattenKey = Config.Bind("1. Hotkeys", "TerrainFlattenKey", KeyCode.LeftControl, 
                    "Hold while using Hoe to flatten terrain to the height where you're standing");
                TerrainResetKey = Config.Bind("1. Hotkeys", "TerrainResetKey", KeyCode.LeftAlt, 
                    "Hold while using Hoe to reset terrain to original world height");
                BiomePaintKey = Config.Bind("1. Hotkeys", "BiomePaintKey", KeyCode.LeftShift,
                    "Hold while using Hoe to paint biome grass");
                BiomePaintCycleKey = Config.Bind("1. Hotkeys", "BiomePaintCycleKey", KeyCode.G,
                    "Press to cycle biome grass paint selection (while Hoe is equipped)");

                OperationRadius = Config.Bind("2. Hoe", "OperationRadius", 4f, 
                    new ConfigDescription("Radius for flatten/reset operations", new AcceptableValueRange<float>(1f, 20f)));
                BiomePaintRadius = Config.Bind("2. Hoe", "BiomePaintRadius", 4f,
                    new ConfigDescription("Radius for biome grass painting", new AcceptableValueRange<float>(1f, 50f)));
                HeightLimitBypassKey = Config.Bind("1. Hotkeys", "HeightLimitBypassKey", KeyCode.H,
                    "Press to toggle height limit bypass (while Hoe is equipped) - removes terrain raise/dig caps");

                DebugMode = Config.Bind("3. Debug", "DebugMode", false,
                    "Enable verbose debug logging to BepInEx console/log");

                _config = Config;
                SetupConfigWatcher();

                _harmony = new Harmony(PluginGUID);
                ApplyPatches();

                _logger.LogInfo($"{PluginName} loaded!");
                _logger.LogInfo($"CTRL+Hoe = Level | ALT+Hoe = Reset | G = Cycle biome paint | SHIFT+Hoe = Paint grass");
                _logger.LogInfo($"Live config reloading enabled - edit {Config.ConfigFilePath} and save to apply changes!");
            }
            catch (Exception ex)
            {
                _logger.LogError($"{PluginName} FAILED to load: {ex}");
            }
        }

        private void ApplyPatches()
        {
            int applied = 0;
            int failed = 0;
            var patchTypes = new[]
            {
                typeof(Location_IsInsideNoBuildLocation_Patch),
                typeof(TerrainOp_OnPlaced_Patch),
                typeof(Game_Start_LoadBiomePaint),
                typeof(Player_OnSpawned_RebuildClutter),
                typeof(ZNet_SaveWorld_SaveBiomePaint),
                typeof(ZNet_OnDestroy_SaveBiomePaint),
            };

            foreach (var patchType in patchTypes)
            {
                try
                {
                    _harmony.CreateClassProcessor(patchType).Patch();
                    applied++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning($"Patch {patchType.Name} failed: {ex.Message}");
                }
            }

            // ClutterSystem patches need special handling - method signatures may vary
            applied += PatchClutterSystem();

            // Heightmap.GetBiome patch — biome queries for gameplay logic
            applied += PatchHeightmapGetBiome();

            // Heightmap.RebuildRenderMesh — ground TEXTURE rendering (vertex colors)
            applied += PatchHeightmapRebuildRenderMesh();

            // TerrainComp.DoOperation — height limit bypass
            applied += PatchTerrainCompDoOperation();

            _logger.LogInfo($"Harmony patches: {applied} applied, {failed} failed");
        }

        private int PatchClutterSystem()
        {
            int applied = 0;

            // Patch GetGroundInfo - find actual method and match parameters
            try
            {
                var getGroundInfo = typeof(ClutterSystem).GetMethod("GetGroundInfo",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (getGroundInfo != null)
                {
                    var postfix = new HarmonyMethod(typeof(ClutterSystem_GetGroundInfo_Patch), "Postfix");
                    _harmony.Patch(getGroundInfo, postfix: postfix);
                    applied++;
                    Log($"Patched ClutterSystem.GetGroundInfo ({(getGroundInfo.IsStatic ? "static" : "instance")}, params: {string.Join(", ", getGroundInfo.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                }
                else
                {
                    _logger.LogWarning("ClutterSystem.GetGroundInfo not found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Patch ClutterSystem_GetGroundInfo failed: {ex.Message}");
            }

            // Patch GetPatchBiomes
            try
            {
                var getPatchBiomes = typeof(ClutterSystem).GetMethod("GetPatchBiomes",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (getPatchBiomes != null)
                {
                    var postfix = new HarmonyMethod(typeof(ClutterSystem_GetPatchBiomes_Patch), "Postfix");
                    _harmony.Patch(getPatchBiomes, postfix: postfix);
                    applied++;
                    Log($"Patched ClutterSystem.GetPatchBiomes ({(getPatchBiomes.IsStatic ? "static" : "instance")}, params: {string.Join(", ", getPatchBiomes.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                }
                else
                {
                    _logger.LogWarning("ClutterSystem.GetPatchBiomes not found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Patch ClutterSystem_GetPatchBiomes failed: {ex.Message}");
            }

            return applied;
        }

        private int PatchHeightmapGetBiome()
        {
            int applied = 0;
            try
            {
                // Find the GetBiome(Vector3) overload that takes a world position
                var getBiomeMethods = typeof(Heightmap).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                MethodInfo targetMethod = null;
                foreach (var m in getBiomeMethods)
                {
                    if (m.Name != "GetBiome") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Vector3))
                    {
                        targetMethod = m;
                        break;
                    }
                }

                if (targetMethod != null)
                {
                    var postfix = new HarmonyMethod(typeof(Heightmap_GetBiome_Patch), "Postfix");
                    _harmony.Patch(targetMethod, postfix: postfix);
                    applied++;
                    Log($"Patched Heightmap.GetBiome(Vector3) - ground textures will reflect painted biomes");
                }
                else
                {
                    _logger.LogWarning("Heightmap.GetBiome(Vector3) not found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Patch Heightmap.GetBiome failed: {ex.Message}");
            }
            return applied;
        }

        private int PatchHeightmapRebuildRenderMesh()
        {
            int applied = 0;
            try
            {
                var rbMethod = typeof(Heightmap).GetMethod("RebuildRenderMesh",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (rbMethod != null)
                {
                    var postfix = new HarmonyMethod(typeof(Heightmap_RebuildRenderMesh_Patch), "Postfix");
                    _harmony.Patch(rbMethod, postfix: postfix);
                    applied++;
                    Log("Patched Heightmap.RebuildRenderMesh - ground texture will reflect painted biomes");
                }
                else
                {
                    _logger.LogWarning("Heightmap.RebuildRenderMesh not found");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Patch Heightmap.RebuildRenderMesh failed: {ex.Message}");
            }
            return applied;
        }

        private int PatchTerrainCompDoOperation()
        {
            int applied = 0;
            try
            {
                // Height clamping is in TWO layers:
                // 1. Storage: LevelTerrain/RaiseTerrain/SmoothTerrain clamp m_levelDelta to ±8, m_smoothDelta to ±1
                // 2. Rendering: ApplyToHeightmap clamps final height to baseHeight ±8
                // Must bypass BOTH layers for unlimited height to work
                string[] targetNames = { "LevelTerrain", "RaiseTerrain", "SmoothTerrain", "ApplyToHeightmap" };
                var allMethods = typeof(TerrainComp).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                foreach (string name in targetNames)
                {
                    var matches = allMethods.Where(m => m.Name == name).ToArray();
                    if (matches.Length == 0)
                    {
                        _logger.LogWarning($"[HeightBypass] {name} NOT FOUND on TerrainComp");
                        continue;
                    }

                    foreach (var method in matches)
                    {
                        try
                        {
                            int ilSize = 0;
                            try { ilSize = method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0; } catch { }
                            var ps = method.GetParameters();
                            _logger.LogInfo($"[HeightBypass] Patching {name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))}) IL={ilSize} bytes");

                            var transpiler = new HarmonyMethod(typeof(TerrainComp_DoOperation_Patch), "Transpiler");
                            _harmony.Patch(method, transpiler: transpiler);
                            applied++;
                            _logger.LogInfo($"[HeightBypass] {name}: {TerrainComp_DoOperation_Patch.LastReplacedCount} clamp-like calls replaced");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"[HeightBypass] Failed to patch {name}: {ex.Message}");
                        }
                    }
                }

                if (applied == 0)
                {
                    _logger.LogWarning("[HeightBypass] No terrain methods patched! Dumping all TerrainComp methods:");
                    foreach (var m in allMethods.Take(50))
                        _logger.LogInfo($"[HeightBypass]   {m.Name}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Patch TerrainComp height bypass failed: {ex.Message}");
            }
            return applied;
        }

        private void SetupConfigWatcher()
        {
            string configPath = Path.GetDirectoryName(Config.ConfigFilePath);
            string configFile = Path.GetFileName(Config.ConfigFilePath);

            _configWatcher = new FileSystemWatcher(configPath, configFile);
            _configWatcher.Changed += OnConfigChanged;
            _configWatcher.Created += OnConfigChanged;
            _configWatcher.Renamed += OnConfigChanged;
            _configWatcher.IncludeSubdirectories = false;
            _configWatcher.SynchronizingObject = null;
            _configWatcher.EnableRaisingEvents = true;

            _logger.LogInfo($"Config watcher started for: {configFile}");
        }

        private static void OnConfigChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                System.Threading.Thread.Sleep(100);
                _config.Reload();
                _logger.LogInfo("Config reloaded! Changes applied.");

                if (Player.m_localPlayer != null)
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center, "MegaHoe Config Reloaded!");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to reload config: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            if (_configWatcher != null)
            {
                _configWatcher.EnableRaisingEvents = false;
                _configWatcher.Dispose();
                _configWatcher = null;
            }
            _harmony?.UnpatchSelf();
        }

        private static void MigrateConfig(string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return;
                string text = File.ReadAllText(configPath);
                bool changed = false;

                changed |= MigrateCfgSection(ref text, "Hotkeys", "1. Hotkeys");
                changed |= MigrateCfgSection(ref text, "Hoe", "2. Hoe");
                changed |= MigrateCfgSection(ref text, "Debug", "3. Debug");

                if (changed)
                    File.WriteAllText(configPath, text.TrimEnd() + "\n");
            }
            catch { }
        }

        private static bool MigrateCfgSection(ref string text, string oldName, string newName)
        {
            string oldHeader = "[" + oldName + "]";
            int idx = text.IndexOf(oldHeader, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;

            int sectionEnd = text.IndexOf("\n[", idx + oldHeader.Length, StringComparison.Ordinal);

            if (newName == null || text.IndexOf("[" + newName + "]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (sectionEnd < 0)
                    text = text.Substring(0, idx).TrimEnd('\r', '\n');
                else
                    text = text.Substring(0, idx) + text.Substring(sectionEnd + 1);
            }
            else
            {
                text = text.Remove(idx, oldHeader.Length).Insert(idx, "[" + newName + "]");
            }
            return true;
        }

        private void Update()
        {
            // World change detection for biome paint persistence
            bool inWorld = false;
            try { inWorld = ZNet.instance != null; } catch { }

            if (inWorld && string.IsNullOrEmpty(_lastWorldName))
            {
                string worldName = GetWorldName();
                if (!string.IsNullOrEmpty(worldName))
                {
                    _lastWorldName = worldName;
                    BiomePaintManager.SetWorld(worldName);
                    Log($"World detected: {worldName}");
                }
            }
            else if (!inWorld && !string.IsNullOrEmpty(_lastWorldName))
            {
                BiomePaintManager.OnWorldExit();
                _lastWorldName = "";
            }

            // Biome paint cycling + height limit toggle
            var player = Player.m_localPlayer;
            if (player != null && IsUsingHoeOrCultivator(player))
            {
                if (Input.GetKeyDown(BiomePaintCycleKey.Value))
                {
                    BiomePaintManager.CycleSelection();
                    string biomeName = BiomePaintManager.GetDisplayName(BiomePaintManager.SelectedBiome);
                    player.Message(MessageHud.MessageType.Center, "Grass Paint: " + biomeName);
                }

                if (Input.GetKeyDown(HeightLimitBypassKey.Value))
                {
                    HeightLimitBypassed = !HeightLimitBypassed;
                    string state = HeightLimitBypassed ? "ON - No height limits!" : "OFF";
                    player.Message(MessageHud.MessageType.Center, "Height Limit Bypass: " + state);
                }
            }
        }

        private static GUIStyle _cachedBoxStyle;
        private static GUIStyle _cachedHintStyle;

        private void OnGUI()
        {
            var player = Player.m_localPlayer;
            if (player == null || !IsUsingHoeOrCultivator(player)) return;

            bool showBiome = BiomePaintManager.SelectedBiome != BiomePaintType.None;
            bool showHeightBypass = HeightLimitBypassed;
            if (!showBiome && !showHeightBypass) return;

            if (_cachedBoxStyle == null)
            {
                _cachedBoxStyle = new GUIStyle(GUI.skin.box);
                _cachedBoxStyle.fontSize = 16;
                _cachedBoxStyle.fontStyle = FontStyle.Bold;
                _cachedBoxStyle.alignment = TextAnchor.MiddleCenter;
                _cachedBoxStyle.normal.textColor = Color.white;
            }

            if (_cachedHintStyle == null)
            {
                _cachedHintStyle = new GUIStyle(GUI.skin.label);
                _cachedHintStyle.fontSize = 12;
                _cachedHintStyle.alignment = TextAnchor.MiddleCenter;
                _cachedHintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.6f);
            }

            float width = 280f;
            float height = 35f;
            float x = (Screen.width - width) / 2f;
            // Position above the game's "Level Ground" / tool action text
            // Game text sits around 72-78% screen height, so we go above that
            float hintsHeight = 18f;
            float totalHeight = hintsHeight;
            if (showBiome) totalHeight += height + 4f;
            if (showHeightBypass) totalHeight += height + 4f;
            float startY = Screen.height * 0.55f - totalHeight;
            float currentY = startY;
            Color oldBg = GUI.backgroundColor;

            // Height limit bypass indicator
            if (showHeightBypass)
            {
                GUI.backgroundColor = new Color(1f, 0.3f, 0.3f, 0.85f);
                GUI.Box(new Rect(x, currentY, width, height), "Height Limit Bypass: ON", _cachedBoxStyle);
                currentY += height + 4f;
            }

            // Biome paint indicator
            if (showBiome)
            {
                string text = "Grass Paint: " + BiomePaintManager.GetDisplayName(BiomePaintManager.SelectedBiome);
                Color biomeColor = BiomePaintManager.GetBiomeColor(BiomePaintManager.SelectedBiome);
                GUI.backgroundColor = new Color(biomeColor.r, biomeColor.g, biomeColor.b, 0.85f);
                GUI.Box(new Rect(x, currentY, width, height), text, _cachedBoxStyle);
                currentY += height + 2f;
            }

            GUI.backgroundColor = oldBg;
            string hints = "SHIFT+Click = Paint | G = Cycle | H = Height Bypass";
            GUI.Label(new Rect(x, currentY, width, hintsHeight), hints, _cachedHintStyle);
        }

        private static string GetWorldName()
        {
            try
            {
                if (ZNet.instance == null) return "";
                return ZNet.instance.GetWorldName() ?? "";
            }
            catch { return ""; }
        }

        public static bool IsUsingHoeOrCultivator(Player player)
        {
            if (player?.GetInventory() == null) return false;
            foreach (var item in player.GetInventory().GetEquippedItems())
            {
                if (item.m_shared.m_name == "$item_hoe" || item.m_shared.m_name == "$item_cultivator")
                    return true;
            }
            return false;
        }

        public static void Log(string message) { if (DebugMode.Value) _logger?.LogInfo(message); }
        public static void LogAlways(string message) => _logger?.LogInfo(message);
        public static void LogWarning(string message) => _logger?.LogWarning(message);
        public static void LogError(string message) => _logger?.LogError(message);


        public static bool IsModifierKeyHeld()
        {
            return Input.GetKey(TerrainFlattenKey.Value) || Input.GetKey(TerrainResetKey.Value) ||
                   (Input.GetKey(BiomePaintKey.Value) && BiomePaintManager.SelectedBiome != BiomePaintType.None);
        }
    }

    /// <summary>
    /// Bypass the "A mystical force in this area stops you" restriction near the starting runes
    /// when using hoe/cultivator with a modifier key held (CTRL or ALT).
    /// </summary>
    [HarmonyPatch(typeof(Location), "IsInsideNoBuildLocation")]
    public static class Location_IsInsideNoBuildLocation_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (!__result) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            if (MegaHoePlugin.IsModifierKeyHeld() && MegaHoePlugin.IsUsingHoeOrCultivator(player))
            {
                MegaHoePlugin.Log("Bypassing no-build restriction near starting runes");
                __result = false;
            }
        }
    }

    [HarmonyPatch(typeof(TerrainOp), "OnPlaced")]
    public static class TerrainOp_OnPlaced_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(TerrainOp __instance)
        {
            try
            {
                var player = Player.m_localPlayer;
                if (player == null || __instance == null) return true;

                Vector3 toolPos = __instance.transform.position;
                var settings = __instance.m_settings;

                // Debug: log ALL terrain operations regardless of tool
                if (MegaHoePlugin.DebugMode.Value && settings != null)
                {
                    string equippedTool = "unknown";
                    if (player.GetInventory() != null)
                    {
                        foreach (var item in player.GetInventory().GetEquippedItems())
                        {
                            string name = item.m_shared.m_name ?? "";
                            if (name.Contains("pickaxe") || name.Contains("hoe") || name.Contains("cultivator") ||
                                name.Contains("Pickaxe") || name.Contains("Hoe") || name.Contains("Cultivator"))
                            {
                                equippedTool = name;
                                break;
                            }
                        }
                    }

                    float terrainY;
                    Heightmap.GetHeight(toolPos, out terrainY);
                    float playerY;
                    Heightmap.GetHeight(player.transform.position, out playerY);

                    MegaHoePlugin.LogAlways($"[TerrainOp] Tool={equippedTool} Pos=({toolPos.x:F1},{toolPos.y:F1},{toolPos.z:F1}) TerrainH={terrainY:F2} PlayerH={playerY:F2}");
                    MegaHoePlugin.LogAlways($"[TerrainOp] raise={settings.m_raise} (r={settings.m_raiseRadius:F1} delta={settings.m_raiseDelta:F2} power={settings.m_raisePower:F2})");
                    MegaHoePlugin.LogAlways($"[TerrainOp] level={settings.m_level} (r={settings.m_levelRadius:F1} offset={settings.m_levelOffset:F2})");
                    MegaHoePlugin.LogAlways($"[TerrainOp] smooth={settings.m_smooth} (r={settings.m_smoothRadius:F1} power={settings.m_smoothPower:F2})");
                    MegaHoePlugin.LogAlways($"[TerrainOp] HeightBypass={MegaHoePlugin.HeightLimitBypassed} IsHoe={MegaHoePlugin.IsUsingHoeOrCultivator(player)}");

                    // Dump TerrainComp deltas at this exact spot
                    List<Heightmap> debugHmaps = new List<Heightmap>();
                    Heightmap.FindHeightmap(toolPos, 2f, debugHmaps);
                    foreach (var hm in debugHmaps)
                    {
                        if (hm == null) continue;
                        TerrainComp tc = hm.GetAndCreateTerrainCompiler();
                        if (tc == null) continue;
                        float[] ld = TerrainModifier.GetField<float[]>(tc, "m_levelDelta");
                        float[] sd = TerrainModifier.GetField<float[]>(tc, "m_smoothDelta");
                        if (ld != null)
                        {
                            int w = hm.m_width;
                            float sc = hm.m_scale;
                            Vector3 hmPos = hm.transform.position;
                            int cx = Mathf.RoundToInt((toolPos.x - hmPos.x) / sc + w / 2);
                            int cz = Mathf.RoundToInt((toolPos.z - hmPos.z) / sc + w / 2);
                            cx = Mathf.Clamp(cx, 0, w);
                            cz = Mathf.Clamp(cz, 0, w);
                            int idx = cz * (w + 1) + cx;
                            if (idx >= 0 && idx < ld.Length)
                            {
                                float smoothVal = (sd != null && idx < sd.Length) ? sd[idx] : 0f;
                                MegaHoePlugin.LogAlways($"[TerrainOp] Vertex({cx},{cz}) idx={idx} levelDelta={ld[idx]:F4} smoothDelta={smoothVal:F4} totalDelta={ld[idx] + smoothVal:F4}");
                            }
                        }
                    }
                }

                if (!MegaHoePlugin.IsUsingHoeOrCultivator(player)) return true;

                float radius = MegaHoePlugin.OperationRadius.Value;

                // SHIFT + biome selected = Paint biome grass
                if (Input.GetKey(MegaHoePlugin.BiomePaintKey.Value) && BiomePaintManager.SelectedBiome != BiomePaintType.None)
                {
                    float paintRadius = MegaHoePlugin.BiomePaintRadius.Value;
                    Heightmap.Biome biome = BiomePaintManager.ToGameBiome(BiomePaintManager.SelectedBiome);
                    string biomeName = BiomePaintManager.GetDisplayName(BiomePaintManager.SelectedBiome);

                    MegaHoePlugin.LogAlways($"[BiomePaint] Radius={paintRadius}, Biome={biomeName}, Pos=({toolPos.x:F1},{toolPos.z:F1})");

                    BiomePaintManager.PaintArea(toolPos, paintRadius, biome);
                    BiomePaintManager.Save();

                    // Force clutter regeneration - use wider radius to cover full patch boundaries
                    if (ClutterSystem.instance != null)
                    {
                        ClutterSystem.instance.ResetGrass(toolPos, paintRadius + 10f);
                        // Cache force rebuild field lookup
                        if (!TerrainModifier._forceRebuildFieldSearched)
                        {
                            TerrainModifier._forceRebuildFieldSearched = true;
                            TerrainModifier._forceRebuildField = typeof(ClutterSystem).GetField("m_forceRebuild", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
                        }
                        if (TerrainModifier._forceRebuildField != null)
                            TerrainModifier._forceRebuildField.SetValue(ClutterSystem.instance, true);
                    }

                    // Force heightmap ground texture refresh (lava, snow, etc.)
                    List<Heightmap> hmaps = new List<Heightmap>();
                    Heightmap.FindHeightmap(toolPos, paintRadius + 10f, hmaps);
                    foreach (var hm in hmaps)
                    {
                        if (hm != null)
                            hm.Poke(false);
                    }

                    player.Message(MessageHud.MessageType.Center, $"Painted {biomeName} (r={paintRadius})");

                    UnityEngine.Object.Destroy(__instance.gameObject);
                    return false;
                }

                // CTRL = Level terrain to player's ground height
                if (Input.GetKey(MegaHoePlugin.TerrainFlattenKey.Value))
                {
                    // Get player's ground height
                    float playerGroundHeight;
                    Heightmap.GetHeight(player.transform.position, out playerGroundHeight);
                    
                    MegaHoePlugin.Log($"=== FLATTEN TO PLAYER HEIGHT ===");
                    MegaHoePlugin.Log($"Player ground: {playerGroundHeight:F2}");
                    
                    // Use direct vertex manipulation to avoid smoothDelta drift
                    TerrainModifier.LevelTerrainDirect(toolPos, radius, playerGroundHeight);
                    
                    player.Message(MessageHud.MessageType.Center, $"Flattened to {playerGroundHeight:F1}m");
                    
                    // Destroy the TerrainOp - we handled it
                    UnityEngine.Object.Destroy(__instance.gameObject);
                    return false;
                }

                // ALT = Reset terrain to world default
                if (Input.GetKey(MegaHoePlugin.TerrainResetKey.Value))
                {
                    MegaHoePlugin.Log($"=== RESET TERRAIN ===");
                    MegaHoePlugin.Log($"Position: {toolPos}, Radius: {radius:F1}");
                    
                    TerrainModifier.ResetTerrainToDefault(toolPos, radius);
                    
                    player.Message(MessageHud.MessageType.Center, $"Terrain Reset");
                    
                    UnityEngine.Object.Destroy(__instance.gameObject);
                    return false;
                }

                // Height bypass: just let vanilla handle it — the transpiler disables Mathf.Clamp
                // in DoOperation when HeightLimitBypassed is true. Log for diagnosis.
                if (MegaHoePlugin.HeightLimitBypassed)
                {
                    MegaHoePlugin.LogAlways($"[HeightBypass] PASSTHROUGH to vanilla (transpiler handles unclamping). settings null={__instance.m_settings == null}");
                    if (__instance.m_settings != null)
                    {
                        var s = __instance.m_settings;
                        MegaHoePlugin.LogAlways($"[HeightBypass] raise={s.m_raise}(r={s.m_raiseRadius:F1} d={s.m_raiseDelta:F2}) level={s.m_level}(r={s.m_levelRadius:F1} o={s.m_levelOffset:F2})");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                MegaHoePlugin.LogError($"Error: {ex.Message}\n{ex.StackTrace}");
                return true;
            }
        }
    }

    public static class TerrainModifier
    {
        // Cached reflection members
        private static MethodInfo _operationMethod;
        private static bool _operationMethodSearched;
        private static MethodInfo _saveMethod;
        private static bool _saveMethodSearched;
        internal static FieldInfo _forceRebuildField;
        internal static bool _forceRebuildFieldSearched;
        private static readonly Dictionary<string, FieldInfo> _fieldCache = new Dictionary<string, FieldInfo>();

        /// <summary>
        /// Apply a raise or level operation WITHOUT height clamping.
        /// Directly manipulates TerrainComp delta arrays to bypass the game's height limits.
        /// </summary>
        public static void ApplyUnclampedOperation(Vector3 center, TerrainOp.Settings settings)
        {
            float maxRadius = Mathf.Max(settings.m_raiseRadius, settings.m_levelRadius) + 5f;
            List<Heightmap> heightmaps = new List<Heightmap>();
            Heightmap.FindHeightmap(center, maxRadius, heightmaps);

            MegaHoePlugin.LogAlways($"[HeightBypass] Found {heightmaps.Count} heightmap(s)");

            foreach (Heightmap hmap in heightmaps)
            {
                if (hmap == null) continue;

                TerrainComp tc = hmap.GetAndCreateTerrainCompiler();
                if (tc == null) continue;

                float[] levelDelta = GetPrivateField<float[]>(tc, "m_levelDelta");
                float[] smoothDelta = GetPrivateField<float[]>(tc, "m_smoothDelta");
                bool[] modifiedHeight = GetPrivateField<bool[]>(tc, "m_modifiedHeight");
                if (levelDelta == null || modifiedHeight == null)
                {
                    MegaHoePlugin.LogAlways($"[HeightBypass] FAILED: levelDelta={levelDelta != null} modifiedHeight={modifiedHeight != null}");
                    continue;
                }

                MegaHoePlugin.LogAlways($"[HeightBypass] Heightmap width={hmap.m_width} scale={hmap.m_scale} levelDelta.len={levelDelta.Length}");

                int width = hmap.m_width;
                float scale = hmap.m_scale;
                Vector3 hmPos = hmap.transform.position;
                int modified = 0;

                if (settings.m_raise)
                {
                    float radius = settings.m_raiseRadius;
                    for (int z = 0; z <= width; z++)
                    {
                        for (int x = 0; x <= width; x++)
                        {
                            float wx = hmPos.x + (x - width / 2) * scale;
                            float wz = hmPos.z + (z - width / 2) * scale;
                            float dx = wx - center.x;
                            float dz = wz - center.z;
                            float distSq = dx * dx + dz * dz;
                            if (distSq > radius * radius) continue;

                            int idx = z * (width + 1) + x;
                            if (idx < 0 || idx >= levelDelta.Length) continue;

                            float dist = Mathf.Sqrt(distSq);
                            float t = 1f - dist / radius;
                            float falloff = t * t * (3f - 2f * t); // smoothstep
                            float raise = settings.m_raiseDelta * falloff;

                            levelDelta[idx] += raise; // NO CLAMP
                            modifiedHeight[idx] = true;
                            modified++;
                        }
                    }
                }

                if (settings.m_level)
                {
                    float radius = settings.m_levelRadius;
                    float targetY = center.y + settings.m_levelOffset;

                    for (int z = 0; z <= width; z++)
                    {
                        for (int x = 0; x <= width; x++)
                        {
                            float wx = hmPos.x + (x - width / 2) * scale;
                            float wz = hmPos.z + (z - width / 2) * scale;
                            float dx = wx - center.x;
                            float dz = wz - center.z;
                            float distSq = dx * dx + dz * dz;
                            if (distSq > radius * radius) continue;

                            int idx = z * (width + 1) + x;
                            if (idx < 0 || idx >= levelDelta.Length) continue;

                            // Compute base height: current delta tells us how far from world-gen we are
                            // For level, we want: newDelta = targetY - baseY
                            // baseY ≈ worldGenHeight at this vertex
                            // worldGenHeight = currentTerrainY - currentDelta
                            // We approximate currentTerrainY from the heightmap
                            float currentDelta = levelDelta[idx] + (smoothDelta != null ? smoothDelta[idx] : 0f);
                            float worldX = wx;
                            float worldZ = wz;
                            float terrainY;
                            Heightmap.GetHeight(new Vector3(worldX, 0f, worldZ), out terrainY);
                            float baseY = terrainY - currentDelta;
                            float newDelta = targetY - baseY;

                            float dist = Mathf.Sqrt(distSq);
                            float blend = 1f;
                            float edgeStart = radius * 0.8f;
                            if (dist > edgeStart)
                            {
                                blend = 1f - (dist - edgeStart) / (radius - edgeStart);
                                blend = Mathf.Clamp01(blend);
                                blend = blend * blend * (3f - 2f * blend);
                            }

                            levelDelta[idx] = Mathf.Lerp(levelDelta[idx], newDelta, blend); // NO CLAMP
                            modifiedHeight[idx] = true;
                            modified++;
                        }
                    }
                }

                if (modified > 0)
                {
                    MegaHoePlugin.LogAlways($"[HeightBypass] Modified {modified} vertices on heightmap at ({hmPos.x:F0},{hmPos.z:F0})");
                    SetPrivateField(tc, "m_modified", true);

                    if (!_saveMethodSearched)
                    {
                        _saveMethodSearched = true;
                        _saveMethod = typeof(TerrainComp).GetMethod("Save", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    _saveMethod?.Invoke(tc, null);
                    hmap.Poke(true);
                }
            }
        }
        /// <summary>
        /// Level terrain using the game's own terrain system via reflection
        /// </summary>
        public static void LevelTerrainUsingGameSystem(Vector3 center, float radius, float targetHeight)
        {
            // Create level settings - this mimics what the game does
            TerrainOp.Settings settings = new TerrainOp.Settings();
            settings.m_level = true;
            settings.m_levelRadius = radius;
            settings.m_levelOffset = 0f;
            settings.m_smooth = false;
            settings.m_smoothRadius = 0f;
            settings.m_smoothPower = 0f;
            settings.m_raise = false;
            settings.m_raiseRadius = 0f;
            settings.m_raisePower = 0f;
            settings.m_raiseDelta = 0f;
            settings.m_paintCleared = false;
            settings.m_paintHeightCheck = false;
            settings.m_paintRadius = 0f;
            
            // The position Y determines the target level height
            Vector3 levelPosition = new Vector3(center.x, targetHeight, center.z);
            
            MegaHoePlugin.Log($"Level position Y={targetHeight:F2}, radius={radius:F1}");
            
            // Find all heightmaps in range
            List<Heightmap> heightmaps = new List<Heightmap>();
            Heightmap.FindHeightmap(center, radius + 10f, heightmaps);
            
            MegaHoePlugin.Log($"Found {heightmaps.Count} heightmap(s)");
            
            foreach (Heightmap hmap in heightmaps)
            {
                if (hmap == null) continue;
                
                TerrainComp tc = hmap.GetAndCreateTerrainCompiler();
                if (tc == null) continue;
                
                // Cache the operation method on first use
                if (!_operationMethodSearched)
                {
                    _operationMethodSearched = true;
                    var methods = typeof(TerrainComp).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var m in methods)
                    {
                        var parameters = m.GetParameters();
                        if (parameters.Length >= 2 && 
                            parameters[0].ParameterType == typeof(Vector3) &&
                            parameters[1].ParameterType == typeof(TerrainOp.Settings))
                        {
                            _operationMethod = m;
                            MegaHoePlugin.Log($"Found method: {m.Name} with {parameters.Length} params");
                            break;
                        }
                    }
                }
                
                if (_operationMethod != null)
                {
                    try
                    {
                        object result = _operationMethod.Invoke(tc, new object[] { levelPosition, settings });
                        MegaHoePlugin.Log($"Called {_operationMethod.Name}, result: {result}");
                    }
                    catch (Exception ex)
                    {
                        MegaHoePlugin.LogError($"Failed to call {_operationMethod.Name}: {ex.Message}");
                    }
                }
                else if (!_operationMethodSearched)
                {
                    // Fallback: list all methods for debugging
                    MegaHoePlugin.Log("Available TerrainComp methods:");
                    var allMethods = typeof(TerrainComp).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var m in allMethods.Take(20))
                    {
                        MegaHoePlugin.Log($"  {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                    }
                }
            }
            
            // Reset grass
            if (ClutterSystem.instance != null)
                ClutterSystem.instance.ResetGrass(center, radius);
        }
        
        /// <summary>
        /// Level terrain by directly manipulating vertex deltas.
        /// Unlike the game's DoOperation, this properly handles m_smoothDelta to prevent
        /// height drift on repeated level operations (smoothDelta was being double-counted).
        /// </summary>
        public static void LevelTerrainDirect(Vector3 center, float radius, float targetHeight)
        {
            List<Heightmap> heightmaps = new List<Heightmap>();
            Heightmap.FindHeightmap(center, radius + 5f, heightmaps);
            
            MegaHoePlugin.Log($"Level direct: target={targetHeight:F2}, radius={radius:F1}, hmaps={heightmaps.Count}");
            
            foreach (Heightmap hmap in heightmaps)
            {
                if (hmap == null) continue;
                
                TerrainComp tc = hmap.GetAndCreateTerrainCompiler();
                if (tc == null) continue;
                
                float[] levelDelta = GetPrivateField<float[]>(tc, "m_levelDelta");
                float[] smoothDelta = GetPrivateField<float[]>(tc, "m_smoothDelta");
                bool[] modifiedHeight = GetPrivateField<bool[]>(tc, "m_modifiedHeight");
                
                if (levelDelta == null || modifiedHeight == null) continue;
                
                int width = hmap.m_width;
                float scale = hmap.m_scale;
                Vector3 hmPos = hmap.transform.position;
                int modified = 0;
                
                for (int z = 0; z <= width; z++)
                {
                    for (int x = 0; x <= width; x++)
                    {
                        float wx = hmPos.x + (x - width / 2) * scale;
                        float wz = hmPos.z + (z - width / 2) * scale;
                        float dx = wx - center.x;
                        float dz = wz - center.z;
                        float distSq = dx * dx + dz * dz;
                        if (distSq > radius * radius) continue;
                        
                        int idx = z * (width + 1) + x;
                        if (idx < 0 || idx >= levelDelta.Length) continue;
                        
                        // Get existing smooth delta for this vertex
                        float curSmooth = (smoothDelta != null && idx < smoothDelta.Length) ? smoothDelta[idx] : 0f;
                        
                        // Compute base world-gen height: terrain height minus all deltas
                        float terrainY;
                        Heightmap.GetHeight(new Vector3(wx, 0f, wz), out terrainY);
                        float baseY = terrainY - levelDelta[idx] - curSmooth;
                        
                        // Total delta needed to reach target from world-gen base
                        float desiredDelta = targetHeight - baseY;
                        
                        // Edge blending for smooth border transition
                        float dist = Mathf.Sqrt(distSq);
                        float blend = 1f;
                        float edgeStart = radius * 0.8f;
                        if (dist > edgeStart)
                        {
                            blend = 1f - (dist - edgeStart) / (radius - edgeStart);
                            blend = Mathf.Clamp01(blend);
                            blend = blend * blend * (3f - 2f * blend); // smoothstep
                        }
                        
                        // Blend total delta from current toward desired
                        float currentTotal = levelDelta[idx] + curSmooth;
                        float newTotal = Mathf.Lerp(currentTotal, desiredDelta, blend);
                        
                        // Blend smoothDelta toward zero (absorb into levelDelta)
                        float newSmooth = Mathf.Lerp(curSmooth, 0f, blend);
                        
                        // levelDelta carries the remainder so total is preserved
                        levelDelta[idx] = newTotal - newSmooth;
                        if (smoothDelta != null && idx < smoothDelta.Length)
                            smoothDelta[idx] = newSmooth;
                        
                        modifiedHeight[idx] = true;
                        modified++;
                    }
                }
                
                if (modified > 0)
                {
                    MegaHoePlugin.Log($"Leveled {modified} vertices to {targetHeight:F2}m");
                    SetPrivateField(tc, "m_modified", true);
                    
                    if (!_saveMethodSearched)
                    {
                        _saveMethodSearched = true;
                        _saveMethod = typeof(TerrainComp).GetMethod("Save", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    _saveMethod?.Invoke(tc, null);
                    hmap.Poke(true);
                }
            }
            
            // Reset grass
            if (ClutterSystem.instance != null)
                ClutterSystem.instance.ResetGrass(center, radius);
        }
        
        /// <summary>
        /// Reset terrain to original world-generated height
        /// </summary>
        public static void ResetTerrainToDefault(Vector3 center, float radius)
        {
            List<Heightmap> heightmaps = new List<Heightmap>();
            Heightmap.FindHeightmap(center, radius + 5f, heightmaps);
            
            MegaHoePlugin.Log($"Found {heightmaps.Count} heightmap(s)");
            
            foreach (Heightmap hmap in heightmaps)
            {
                if (hmap == null) continue;
                
                TerrainComp tc = hmap.GetAndCreateTerrainCompiler();
                if (tc == null) continue;
                
                int count = ApplyResetOperation(tc, hmap, center, radius);
                
                if (count > 0)
                {
                    MegaHoePlugin.Log($"Reset {count} vertices");
                    
                    SetPrivateField(tc, "m_modified", true);
                    
                    if (!_saveMethodSearched)
                    {
                        _saveMethodSearched = true;
                        _saveMethod = typeof(TerrainComp).GetMethod("Save", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                    _saveMethod?.Invoke(tc, null);
                    
                    hmap.Poke(true);
                }
            }
            
            if (ClutterSystem.instance != null)
                ClutterSystem.instance.ResetGrass(center, radius);
        }
        
        private static int ApplyResetOperation(TerrainComp tc, Heightmap hmap, Vector3 center, float radius)
        {
            float[] levelDelta = GetPrivateField<float[]>(tc, "m_levelDelta");
            float[] smoothDelta = GetPrivateField<float[]>(tc, "m_smoothDelta");
            bool[] modifiedHeight = GetPrivateField<bool[]>(tc, "m_modifiedHeight");
            bool[] modifiedPaint = GetPrivateField<bool[]>(tc, "m_modifiedPaint");
            Color[] paintMask = GetPrivateField<Color[]>(tc, "m_paintMask");
            
            if (levelDelta == null || smoothDelta == null || modifiedHeight == null)
            {
                MegaHoePlugin.LogError("Failed to get TerrainComp arrays");
                return 0;
            }
            
            int width = hmap.m_width;
            float scale = hmap.m_scale;
            Vector3 hmapCenter = hmap.transform.position;
            
            int modified = 0;
            
            for (int z = 0; z <= width; z++)
            {
                for (int x = 0; x <= width; x++)
                {
                    float worldX = hmapCenter.x + (x - width / 2) * scale;
                    float worldZ = hmapCenter.z + (z - width / 2) * scale;
                    
                    float dx = worldX - center.x;
                    float dz = worldZ - center.z;
                    float distSq = dx * dx + dz * dz;
                    
                    if (distSq > radius * radius) continue;
                    
                    int idx = z * (width + 1) + x;
                    if (idx < 0 || idx >= levelDelta.Length) continue;
                    
                    float dist = Mathf.Sqrt(distSq);
                    float blend = 1f;
                    float edgeStart = radius * 0.8f;
                    if (dist > edgeStart)
                    {
                        blend = 1f - (dist - edgeStart) / (radius - edgeStart);
                        blend = Mathf.Clamp01(blend);
                        blend = blend * blend * (3f - 2f * blend);
                    }
                    
                    // Blend toward zero
                    levelDelta[idx] = Mathf.Lerp(levelDelta[idx], 0f, blend);
                    smoothDelta[idx] = Mathf.Lerp(smoothDelta[idx], 0f, blend);
                    
                    bool isZero = Mathf.Abs(levelDelta[idx]) < 0.001f && Mathf.Abs(smoothDelta[idx]) < 0.001f;
                    modifiedHeight[idx] = !isZero;
                    
                    if (blend > 0.99f)
                    {
                        if (modifiedPaint != null && idx < modifiedPaint.Length)
                            modifiedPaint[idx] = false;
                        if (paintMask != null && idx < paintMask.Length)
                            paintMask[idx] = Color.clear;
                    }
                    
                    modified++;
                }
            }
            
            return modified;
        }
        
        public static T GetField<T>(object obj, string fieldName)
        {
            return GetPrivateField<T>(obj, fieldName);
        }

        private static T GetPrivateField<T>(object obj, string fieldName)
        {
            if (obj == null) return default(T);
            string cacheKey = obj.GetType().FullName + "." + fieldName;
            FieldInfo field;
            if (!_fieldCache.TryGetValue(cacheKey, out field))
            {
                field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fieldCache[cacheKey] = field;
            }
            if (field == null) return default(T);
            return (T)field.GetValue(obj);
        }
        
        private static void SetPrivateField(object obj, string fieldName, object value)
        {
            if (obj == null) return;
            string cacheKey = obj.GetType().FullName + "." + fieldName;
            FieldInfo field;
            if (!_fieldCache.TryGetValue(cacheKey, out field))
            {
                field = obj.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _fieldCache[cacheKey] = field;
            }
            field?.SetValue(obj, value);
        }
    }
}
