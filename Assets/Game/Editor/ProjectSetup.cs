using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GunAndParry.EditorTools
{
    /// <summary>
    /// One-click, re-runnable project setup: material templates (so shader variants survive stripping),
    /// post-processing profile, the boot scene, build settings and Steam/Windows player settings.
    /// </summary>
    public static class ProjectSetup
    {
        const string ResourcesDir = "Assets/Game/Resources";
        const string ScenePath = "Assets/Game/Scenes/Main.unity";

        [MenuItem("Gun & Parry/Setup Project")]
        public static void Setup()
        {
            CreateMaterials();
            CreateVolumeProfile();
            CreateScene();
            ConfigurePlayer();
            AssetDatabase.SaveAssets();
            Debug.Log("[GunAndParry] Project setup complete.");
        }

        static void CreateMaterials()
        {
            Directory.CreateDirectory(ResourcesDir + "/Materials");
            Mat("M_LitEmissive", "Universal Render Pipeline/Lit", m =>
            {
                // URP derives _EMISSION from the GI flags whenever it validates a material (import, inspector, upgrade):
                // the flags must say "emissive" or URP silently strips the keyword again.
                m.SetColor("_EmissionColor", Color.white);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                BaseShaderGUI.SetMaterialKeywords(m);
                if (!m.IsKeywordEnabled("_EMISSION")) Debug.LogError("[GunAndParry] M_LitEmissive: _EMISSION could not be enabled.");
            });
            Mat("M_Unlit", "Universal Render Pipeline/Unlit", _ => { });
            Mat("M_UnlitTransparent", "Universal Render Pipeline/Unlit", m =>
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                BaseShaderGUI.SetupMaterialBlendMode(m);
            });
            Mat("M_ParticleAdditive", "Universal Render Pipeline/Particles/Unlit", m =>
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 2f);
                BaseShaderGUI.SetupMaterialBlendMode(m);
            });
        }

        static void Mat(string name, string shader, System.Action<Material> setup)
        {
            string path = $"{ResourcesDir}/Materials/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(Shader.Find(shader));
                AssetDatabase.CreateAsset(m, path);
            }
            m.shader = Shader.Find(shader);
            setup(m);
            EditorUtility.SetDirty(m);
        }

        static void CreateVolumeProfile()
        {
            string path = ResourcesDir + "/GameVolumeProfile.asset";
            if (AssetDatabase.LoadAssetAtPath<VolumeProfile>(path) != null) AssetDatabase.DeleteAsset(path);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);

            var bloom = Add<Bloom>(profile);
            bloom.threshold.Override(1.1f);
            bloom.intensity.Override(0.7f);
            bloom.scatter.Override(0.72f);
            bloom.highQualityFiltering.Override(true);

            Add<Tonemapping>(profile).mode.Override(TonemappingMode.ACES);

            var vignette = Add<Vignette>(profile);
            vignette.intensity.Override(0.3f);
            vignette.smoothness.Override(0.45f);
            vignette.color.Override(Color.black);

            Add<ChromaticAberration>(profile).intensity.Override(0.05f);

            var color = Add<ColorAdjustments>(profile);
            color.contrast.Override(12f);
            color.saturation.Override(12f);

            EditorUtility.SetDirty(profile);
        }

        static T Add<T>(VolumeProfile profile) where T : VolumeComponent
        {
            var c = profile.Add<T>(true);
            c.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(c, profile);
            return c;
        }

        static void CreateScene()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Game").AddComponent<Game>();
            // Fog is enabled in the scene so fog shader variants are kept in builds.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        static void ConfigurePlayer()
        {
            PlayerSettings.productName = "Gun & Parry";
            PlayerSettings.bundleVersion = "0.1.0";
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            PlayerSettings.defaultIsNativeResolution = true;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = false;
            PlayerSettings.visibleInBackground = true;
            PlayerSettings.usePlayerLog = true;
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
        }

        /// <summary>Writes every synthesized SFX variation as a WAV under SfxPreview/ for auditioning outside Unity.</summary>
        [MenuItem("Gun & Parry/Export SFX Preview")]
        public static void ExportSfx()
        {
            const string dir = "SfxPreview";
            Directory.CreateDirectory(dir);
            int files = 0;
            foreach (var r in SfxBank.Build())
                for (int v = 0; v < r.Stereo.Length; v++, files++)
                    WriteWav(Path.Combine(dir, $"{r.Patch.Id}_{v}.wav"), r.Stereo[v]);
            Debug.Log($"[GunAndParry] Exported {files} SFX to {Path.GetFullPath(dir)}");
        }

        static void WriteWav(string path, float[] interleaved)
        {
            using var w = new BinaryWriter(File.Create(path));
            int bytes = interleaved.Length * 2;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)2);
            w.Write(Synth.Rate); w.Write(Synth.Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (var x in interleaved) w.Write((short)Mathf.Clamp(Mathf.RoundToInt(x * 32767f), -32768, 32767));
        }

        [MenuItem("Gun & Parry/Build Windows (Steam)")]
        public static void BuildWindows() => Build(BuildTarget.StandaloneWindows64, "Builds/Windows/GunAndParry.exe");

        [MenuItem("Gun & Parry/Build macOS (local test)")]
        public static void BuildMac() => Build(BuildTarget.StandaloneOSX, "Builds/macOS/GunAndParry.app");

        static void Build(BuildTarget target, string output)
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = output,
                target = target,
                options = BuildOptions.None,
            });
            var s = report.summary;
            if (s.result == BuildResult.Succeeded) Debug.Log($"[GunAndParry] Build OK: {output} ({s.totalSize / (1024 * 1024)} MB)");
            else Debug.LogError($"[GunAndParry] Build failed: {s.result}, errors={s.totalErrors}");
        }
    }
}
