using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace MegaHoe
{
    public enum BiomePaintType
    {
        None,
        Meadows,
        BlackForest,
        Swamp,
        Mountain,
        Plains,
        Mistlands,
        Ashlands
    }

    /// <summary>
    /// Manages biome override data for grass painting.
    /// Stores per-cell overrides in a 2m grid and persists them per world.
    /// Saves alongside Valheim world data for cross-computer persistence.
    /// </summary>
    public static class BiomePaintManager
    {
        private static readonly Dictionary<long, Heightmap.Biome> _overrides = new Dictionary<long, Heightmap.Biome>();
        private static BiomePaintType _selectedBiome = BiomePaintType.None;
        private static readonly float _cellSize = 2f;
        private static string _currentWorldName = "";

        public static BiomePaintType SelectedBiome
        {
            get { return _selectedBiome; }
            set { _selectedBiome = value; }
        }

        public static int OverrideCount
        {
            get { return _overrides.Count; }
        }

        public static void CycleSelection()
        {
            BiomePaintType[] values = (BiomePaintType[])Enum.GetValues(typeof(BiomePaintType));
            int current = Array.IndexOf(values, _selectedBiome);
            _selectedBiome = values[(current + 1) % values.Length];
        }

        public static Heightmap.Biome ToGameBiome(BiomePaintType type)
        {
            switch (type)
            {
                case BiomePaintType.Meadows: return Heightmap.Biome.Meadows;
                case BiomePaintType.BlackForest: return Heightmap.Biome.BlackForest;
                case BiomePaintType.Swamp: return Heightmap.Biome.Swamp;
                case BiomePaintType.Mountain: return Heightmap.Biome.Mountain;
                case BiomePaintType.Plains: return Heightmap.Biome.Plains;
                case BiomePaintType.Mistlands: return Heightmap.Biome.Mistlands;
                case BiomePaintType.Ashlands: return Heightmap.Biome.AshLands;
                default: return Heightmap.Biome.None;
            }
        }

        public static Color GetBiomeColor(BiomePaintType type)
        {
            switch (type)
            {
                case BiomePaintType.Meadows: return new Color(0.4f, 0.8f, 0.3f);
                case BiomePaintType.BlackForest: return new Color(0.15f, 0.4f, 0.15f);
                case BiomePaintType.Swamp: return new Color(0.4f, 0.3f, 0.15f);
                case BiomePaintType.Mountain: return new Color(0.9f, 0.9f, 0.95f);
                case BiomePaintType.Plains: return new Color(0.85f, 0.75f, 0.3f);
                case BiomePaintType.Mistlands: return new Color(0.35f, 0.2f, 0.55f);
                case BiomePaintType.Ashlands: return new Color(0.85f, 0.25f, 0.1f);
                default: return Color.gray;
            }
        }

        public static string GetDisplayName(BiomePaintType type)
        {
            switch (type)
            {
                case BiomePaintType.None: return "OFF";
                case BiomePaintType.BlackForest: return "Black Forest";
                default: return type.ToString();
            }
        }

        private static long PackKey(int gx, int gz)
        {
            return ((long)gx << 32) | (uint)gz;
        }

        public static void PaintArea(Vector3 center, float radius, Heightmap.Biome biome)
        {
            int minGX = Mathf.FloorToInt((center.x - radius) / _cellSize);
            int maxGX = Mathf.CeilToInt((center.x + radius) / _cellSize);
            int minGZ = Mathf.FloorToInt((center.z - radius) / _cellSize);
            int maxGZ = Mathf.CeilToInt((center.z + radius) / _cellSize);
            int painted = 0;

            for (int gx = minGX; gx <= maxGX; gx++)
            {
                for (int gz = minGZ; gz <= maxGZ; gz++)
                {
                    float wx = gx * _cellSize;
                    float wz = gz * _cellSize;
                    float dx = wx - center.x;
                    float dz = wz - center.z;
                    if (dx * dx + dz * dz <= radius * radius)
                    {
                        long key = PackKey(gx, gz);
                        if (biome == Heightmap.Biome.None)
                            _overrides.Remove(key);
                        else
                            _overrides[key] = biome;
                        painted++;
                    }
                }
            }

            MegaHoePlugin.Log($"Painted {painted} cells with biome {biome}");
        }

        public static bool TryGetOverride(Vector3 pos, out Heightmap.Biome biome)
        {
            int gx = Mathf.RoundToInt(pos.x / _cellSize);
            int gz = Mathf.RoundToInt(pos.z / _cellSize);
            return _overrides.TryGetValue(PackKey(gx, gz), out biome);
        }

        public static void SetWorld(string worldName)
        {
            MegaHoePlugin.Log($"[BiomePaint] SetWorld called: '{worldName}' (current: '{_currentWorldName}')");
            if (string.IsNullOrEmpty(worldName)) return;
            if (_currentWorldName == worldName)
            {
                MegaHoePlugin.Log($"[BiomePaint] Same world, skipping reload (overrides: {_overrides.Count})");
                return;
            }

            if (!string.IsNullOrEmpty(_currentWorldName))
                Save();

            _currentWorldName = worldName;
            Load();
        }

        public static void OnWorldExit()
        {
            MegaHoePlugin.Log($"[BiomePaint] OnWorldExit: saving {_overrides.Count} overrides for '{_currentWorldName}'");
            Save();
            _overrides.Clear();
            _currentWorldName = "";
        }

        /// <summary>
        /// Primary save path alongside Valheim world saves for cross-computer persistence.
        /// </summary>
        private static string GetWorldSavePath()
        {
            try
            {
                string valheimSave = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "IronGate", "Valheim", "worlds_local");
                if (Directory.Exists(valheimSave))
                    return Path.Combine(valheimSave, "megahoe_" + _currentWorldName + ".dat");
            }
            catch (Exception ex)
            {
                MegaHoePlugin.LogError($"[BiomePaint] Failed to resolve world save path: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Legacy save path in BepInEx config (used for migration).
        /// </summary>
        private static string GetLegacySavePath()
        {
            string dir = Path.Combine(BepInEx.Paths.ConfigPath, "MegaHoe");
            return Path.Combine(dir, "biome_paint_" + _currentWorldName + ".dat");
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(_currentWorldName))
            {
                MegaHoePlugin.Log($"[BiomeSave] Skipped: no world name set");
                return;
            }
            if (_overrides.Count == 0)
            {
                MegaHoePlugin.Log($"[BiomeSave] Skipped: no overrides to save");
                return;
            }
            try
            {
                string path = GetWorldSavePath() ?? GetLegacySavePath();
                string dir = Path.GetDirectoryName(path);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var writer = new BinaryWriter(File.Open(path, FileMode.Create)))
                {
                    writer.Write(2); // version 2 - world save path era
                    writer.Write(_overrides.Count);
                    foreach (var kvp in _overrides)
                    {
                        writer.Write(kvp.Key);
                        writer.Write((int)kvp.Value);
                    }
                }
                MegaHoePlugin.Log($"[BiomeSave] Saved {_overrides.Count} overrides to: {path}");
            }
            catch (Exception ex)
            {
                MegaHoePlugin.LogError($"[BiomeSave] FAILED: {ex.Message}");
            }
        }

        public static void Load()
        {
            _overrides.Clear();
            if (string.IsNullOrEmpty(_currentWorldName))
            {
                MegaHoePlugin.Log($"[BiomeLoad] Skipped: no world name set");
                return;
            }

            // Try world save path first (primary), then legacy BepInEx config path
            string worldPath = GetWorldSavePath();
            string legacyPath = GetLegacySavePath();
            bool isLegacy = false;

            string loadPath = null;
            if (worldPath != null && File.Exists(worldPath))
                loadPath = worldPath;
            else if (File.Exists(legacyPath))
            {
                loadPath = legacyPath;
                isLegacy = true;
                MegaHoePlugin.Log($"[BiomeLoad] Found legacy save at {legacyPath}, will migrate");
            }

            if (loadPath == null)
            {
                MegaHoePlugin.Log($"[BiomeLoad] No saved paint data found");
                return;
            }

            try
            {
                using (var reader = new BinaryReader(File.Open(loadPath, FileMode.Open)))
                {
                    int version = reader.ReadInt32();
                    int count = reader.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        long key = reader.ReadInt64();
                        int biome = reader.ReadInt32();
                        _overrides[key] = (Heightmap.Biome)biome;
                    }
                }
                MegaHoePlugin.Log($"[BiomeLoad] Loaded {_overrides.Count} overrides from {loadPath}");

                // Migrate legacy data to world save path
                if (isLegacy && _overrides.Count > 0)
                {
                    Save();
                    MegaHoePlugin.Log($"[BiomeLoad] Migrated paint data to world save directory");
                }
            }
            catch (Exception ex)
            {
                MegaHoePlugin.LogError($"[BiomeLoad] FAILED to read: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Load biome paint data early during game startup, BEFORE ClutterSystem
    /// generates its first set of grass patches.
    /// </summary>
    [HarmonyPatch(typeof(Game), "Start")]
    public static class Game_Start_LoadBiomePaint
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                if (ZNet.instance == null) return;
                string worldName = ZNet.instance.GetWorldName();
                if (string.IsNullOrEmpty(worldName)) return;

                BiomePaintManager.SetWorld(worldName);
                MegaHoePlugin.Log($"[Early Load] Biome paint data loaded for world '{worldName}' ({BiomePaintManager.OverrideCount} overrides)");
            }
            catch (Exception ex)
            {
                MegaHoePlugin.LogError($"Failed early biome paint load: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Force a full clutter rebuild once ClutterSystem and Player are both ready.
    /// </summary>
    [HarmonyPatch(typeof(Player), "OnSpawned")]
    public static class Player_OnSpawned_RebuildClutter
    {
        [HarmonyPostfix]
        public static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (BiomePaintManager.OverrideCount == 0) return;
            if (ClutterSystem.instance == null) return;

            try
            {
                var patchesField = typeof(ClutterSystem).GetField("m_patches", BindingFlags.NonPublic | BindingFlags.Instance);
                if (patchesField != null)
                {
                    var patches = patchesField.GetValue(ClutterSystem.instance);
                    if (patches != null)
                    {
                        var clearMethod = patches.GetType().GetMethod("Clear");
                        clearMethod?.Invoke(patches, null);
                    }
                }

                var clearAllMethod = typeof(ClutterSystem).GetMethod("ClearAll", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (clearAllMethod != null)
                    clearAllMethod.Invoke(ClutterSystem.instance, null);

                MegaHoePlugin.Log($"Clutter fully cleared on spawn for {BiomePaintManager.OverrideCount} biome overrides");
            }
            catch (Exception ex)
            {
                MegaHoePlugin.LogError($"Failed to clear clutter on spawn: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Save biome paint data whenever the game auto-saves the world.
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "SaveWorld")]
    public static class ZNet_SaveWorld_SaveBiomePaint
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (BiomePaintManager.OverrideCount > 0)
            {
                MegaHoePlugin.Log("[BiomePaint] World save detected - saving biome paint data");
                BiomePaintManager.Save();
            }
        }
    }

    /// <summary>
    /// Ensure biome paint data is saved when ZNet is destroyed (disconnect/quit).
    /// </summary>
    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    public static class ZNet_OnDestroy_SaveBiomePaint
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            MegaHoePlugin.Log("[BiomePaint] ZNet.OnDestroy - saving biome paint data");
            BiomePaintManager.Save();
        }
    }

    /// <summary>
    /// Override patch-level biome mask so the clutter system considers painted biome vegetation.
    /// Patched manually in MegaHoePlugin.PatchClutterSystem() for resilient parameter matching.
    /// </summary>
    public static class ClutterSystem_GetPatchBiomes_Patch
    {
        public static void Postfix(Vector3 center, float halfSize, ref Heightmap.Biome __result)
        {
            if (BiomePaintManager.OverrideCount == 0) return;

            Heightmap.Biome overrideBiome;
            if (BiomePaintManager.TryGetOverride(center, out overrideBiome))
            {
                __result |= overrideBiome;
            }

            // Also check patch corners to catch overrides at patch boundaries
            float step = halfSize;
            Vector3[] samples = new Vector3[]
            {
                new Vector3(center.x - step, center.y, center.z - step),
                new Vector3(center.x + step, center.y, center.z - step),
                new Vector3(center.x - step, center.y, center.z + step),
                new Vector3(center.x + step, center.y, center.z + step)
            };
            for (int i = 0; i < samples.Length; i++)
            {
                if (BiomePaintManager.TryGetOverride(samples[i], out overrideBiome))
                {
                    __result |= overrideBiome;
                }
            }
        }
    }

    /// <summary>
    /// Override per-point biome lookup for individual clutter placement.
    /// Patched manually in MegaHoePlugin.PatchClutterSystem() for resilient parameter matching.
    /// </summary>
    public static class ClutterSystem_GetGroundInfo_Patch
    {
        public static void Postfix(Vector3 p, ref Heightmap.Biome biome)
        {
            if (BiomePaintManager.OverrideCount == 0) return;

            Heightmap.Biome overrideBiome;
            if (BiomePaintManager.TryGetOverride(p, out overrideBiome))
            {
                biome = overrideBiome;
            }
        }
    }
}
