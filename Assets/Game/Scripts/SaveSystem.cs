using System;
using System.IO;
using UnityEngine;

namespace GunAndParry
{
    [Serializable]
    public sealed class SaveData
    {
        public int version = 1;
        public int gold;
        public int[] meta = new int[Enum.GetValues(typeof(MetaId)).Length];

        public float masterVolume = 0.8f;
        public float musicVolume = 0.55f;
        public float sfxVolume = 0.8f;
        public bool fullscreen = true;
        public bool vsync = true;
        public bool screenShake = true;
        public bool damageNumbers = true;

        public int runs, wins, totalKills, totalParries;
        public float bestTime;

        public int Meta(MetaId id) => meta[(int)id];
    }

    /// <summary>JSON save in persistentDataPath (Steam Auto-Cloud can sync this folder).</summary>
    public static class SaveSystem
    {
        public static SaveData Data { get; private set; } = new SaveData();
        public static string DefaultPath => Path.Combine(Application.persistentDataPath, "save.json");

        public static void Load() => Data = Load(DefaultPath);
        public static void Save() => Save(Data, DefaultPath);

        public static SaveData Load(string path)
        {
            SaveData data = null;
            try
            {
                if (File.Exists(path)) data = JsonUtility.FromJson<SaveData>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                // Keep the broken file for support instead of silently overwriting the player's progress.
                Debug.LogWarning($"Save file unreadable, backing up: {e.Message}");
                try { File.Copy(path, path + ".corrupt", true); } catch { /* best effort */ }
            }
            data ??= new SaveData();
            int metaCount = Enum.GetValues(typeof(MetaId)).Length;
            if (data.meta == null || data.meta.Length != metaCount)
            {
                var fixedMeta = new int[metaCount];
                if (data.meta != null) Array.Copy(data.meta, fixedMeta, Mathf.Min(data.meta.Length, metaCount));
                data.meta = fixedMeta;
            }
            return data;
        }

        public static void Save(SaveData data, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // Write to a temp file first so a crash mid-write never destroys the existing save.
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(data, true));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to save: {e}");
            }
        }

        public static bool CanBuy(SaveData data, MetaDef def) =>
            data.meta[(int)def.Id] < def.MaxRank && data.gold >= def.Cost(data.meta[(int)def.Id]);

        public static bool TryBuy(SaveData data, MetaDef def)
        {
            if (!CanBuy(data, def)) return false;
            data.gold -= def.Cost(data.meta[(int)def.Id]);
            data.meta[(int)def.Id]++;
            return true;
        }

        public static void ApplySettings()
        {
            var d = Data;
            AudioListener.volume = d.masterVolume;
            Sfx.SetVolumes(d.musicVolume, d.sfxVolume);
            QualitySettings.vSyncCount = d.vsync ? 1 : 0;
            Application.targetFrameRate = d.vsync ? -1 : 240;
            if (!Application.isEditor)
            {
                var mode = d.fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
                if (Screen.fullScreenMode != mode)
                {
                    if (d.fullscreen) Screen.SetResolution(Display.main.systemWidth, Display.main.systemHeight, mode);
                    else Screen.SetResolution(1600, 900, mode);
                }
            }
        }
    }
}
