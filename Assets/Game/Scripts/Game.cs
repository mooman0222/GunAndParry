using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GunAndParry
{
    public enum GameState { Title, Playing, LevelUp, Paused, Results }

    public sealed class RunState
    {
        public float Time;
        public int Level = 1;
        public float Xp;
        public int Kills, Parries, PerfectParries, Breaks, Chests, Rerolls;
        public float Gold;
        public bool BossSpawned, Victory;
        /// <summary>Boss practice runs grant no gold and don't touch saved stats.</summary>
        public bool Practice;
        public float BossSpawnTime = -1f, BossKillTime = -1f;
        public int GoldEarned;
        public int Seed;
        public readonly float[] Damage = new float[ProjectileManager.SourceCount];
        public int XpToNext => GameData.XpToNext(Level);
    }

    /// <summary>
    /// Entry point and orchestrator. Builds the world in code, owns the game state machine,
    /// and ticks every subsystem in a fixed, explicit order.
    /// </summary>
    public sealed class Game : MonoBehaviour
    {
        public static Game I { get; private set; }
        /// <summary>Simulation clock: advances only while a run is being simulated (pauses, level-ups and menus freeze it).</summary>
        public static float Now { get; private set; }
        /// <summary>Dev soak tests: when &gt; 0, each frame simulates this many fixed 1/60s steps instead of one variable step.</summary>
        public static int DebugSubsteps;

        public GameState State { get; private set; } = GameState.Title;
        public GameInput Input { get; private set; }
        public Camera Cam { get; private set; }
        public CameraRig Rig { get; private set; }
        public Fx Fx { get; private set; }
        public UIRoot UI { get; private set; }
        public Player Player { get; private set; }
        public EnemyManager Enemies { get; private set; }
        public ProjectileManager Projectiles { get; private set; }
        public PickupManager Pickups { get; private set; }
        public Loadout Loadout { get; private set; } = new Loadout();
        public RunState Run { get; private set; } = new RunState();
        public Transform WorldRoot { get; private set; }

        static readonly Color Background = new(0.02f, 0.025f, 0.05f);

        readonly Spawner spawner = new();
        ChestSequence chest;
        System.Random rng = new();
        Transform floor;
        Vignette vignette;
        ChromaticAberration chromatic;
        float hitstopUntil, slowmoUntil, hurtPulse, chromaPulse;
        int pendingLevelUps;
        float victoryAt = -1f;
        bool clearAfterBoss;
        int resumeFrame;

        void Awake()
        {
            if (I != null) { Destroy(gameObject); return; }
            I = this;

            SaveSystem.Load();
            Art.Init();
            Sfx.Init(gameObject);
            Input = new GameInput();

            BuildWorld();
            Fx = new GameObject("Fx").AddComponent<Fx>();
            Fx.transform.SetParent(WorldRoot, false);
            Fx.Init();
            Enemies = new EnemyManager(WorldRoot);
            Projectiles = new ProjectileManager(WorldRoot);
            Pickups = new PickupManager(WorldRoot);
            Player = new Player(WorldRoot);
            chest = new ChestSequence(WorldRoot);
            Player.ResetForRun(PlayerStats.Compute(Loadout, SaveSystem.Data));
            Player.T.gameObject.SetActive(false);
            UI = UIRoot.Create(transform);

            SaveSystem.ApplySettings();
            ShowTitle();
        }

        void OnDestroy()
        {
            if (I != this) return;
            Input?.Dispose();
            I = null;
        }

        void OnApplicationFocus(bool focus)
        {
            if (!focus && State == GameState.Playing) Pause();
        }

        void OnApplicationQuit() => SaveSystem.Save();

        // ---------- World ----------

        void BuildWorld()
        {
            WorldRoot = new GameObject("World").transform;

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            Cam = camGo.AddComponent<Camera>();
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = Background;
            Cam.fieldOfView = 38f;
            Cam.nearClipPlane = 0.5f;
            Cam.farClipPlane = 120f;
            camGo.AddComponent<AudioListener>();
            var camData = Cam.GetUniversalAdditionalCameraData();
            camData.renderPostProcessing = true;
            camData.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            camData.antialiasingQuality = AntialiasingQuality.High;
            Rig = camGo.AddComponent<CameraRig>();
            Rig.Cam = Cam;

            var lightGo = new GameObject("Sun");
            lightGo.transform.SetParent(WorldRoot, false);
            lightGo.transform.rotation = Quaternion.Euler(55f, -20f, 0f);
            var sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(0.75f, 0.82f, 1f);
            sun.intensity = 0.9f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.65f;

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.18f, 0.2f, 0.3f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = Background;
            RenderSettings.fogStartDistance = 26f;
            RenderSettings.fogEndDistance = 62f;

            var volumeGo = new GameObject("PostProcess");
            volumeGo.transform.SetParent(WorldRoot, false);
            var volume = volumeGo.AddComponent<Volume>();
            volume.isGlobal = true;
            var template = Resources.Load<VolumeProfile>("GameVolumeProfile");
            volume.profile = template != null ? Instantiate(template) : ScriptableObject.CreateInstance<VolumeProfile>();
            volume.profile.TryGet(out vignette);
            volume.profile.TryGet(out chromatic);

            var floorMat = new Material(Art.Lit(Color.white, Color.white));
            floorMat.SetTexture("_BaseMap", Art.Grid);
            floorMat.SetTexture("_EmissionMap", Art.Grid);
            floorMat.SetColor("_EmissionColor", new Color(0.4f, 0.4f, 0.4f));
            floorMat.SetFloat("_Smoothness", 0.7f);
            floorMat.mainTextureScale = new Vector2(25, 25);
            var floorGo = Art.Part("Floor", WorldRoot, Art.Quad, floorMat, Vector3.zero, new Vector3(200, 200, 1));
            floorGo.transform.rotation = Quaternion.Euler(90, 0, 0);
            floorGo.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            floor = floorGo.transform;
        }

        void UpdateFloor()
        {
            // Snap to the 8m texture period so the infinite floor never visibly slides.
            var focus = State == GameState.Title ? Vector3.zero : Player.Pos;
            floor.position = new Vector3(Mathf.Round(focus.x / 8f) * 8f, 0, Mathf.Round(focus.z / 8f) * 8f);
        }

        // ---------- Loop ----------

        void Update()
        {
            Input.Update();
            HandleMenuInput();
            HandleDebugKeys();

            if (State == GameState.Playing) chest.Tick(Time.unscaledDeltaTime, Input);
            if (State == GameState.Playing)
            {
                if (DebugSubsteps > 0 && Debug.isDebugBuild)
                    for (int i = 0; i < DebugSubsteps && State == GameState.Playing; i++) TickRun(1f / 60f);
                else TickRun(Time.deltaTime);
            }

            UpdateFloor();
            // Pull the camera back for the giant final boss.
            Rig.Zoom = State != GameState.Title && Enemies.Boss != null && Enemies.Boss.Boss != null && Enemies.Boss.Boss.IsFinal ? 1.3f : 1f;
            Fx.Tick();
            UpdateTimeScale();
            UpdatePost();
            UI.Tick();
            Cursor.visible = State != GameState.Playing;
            Cursor.lockState = State == GameState.Playing && !Application.isEditor ? CursorLockMode.Confined : CursorLockMode.None;
        }

        void TickRun(float dt)
        {
            Now += dt;
            Run.Time += dt;
            Player.Tick(dt, Input, Cam, Time.frameCount != resumeFrame);
            if (State != GameState.Playing) return;
            if (!Run.BossSpawned || Enemies.Boss != null) spawner.Tick(dt, Run.Time, Player.Pos);
            Enemies.Tick(dt);
            Projectiles.Tick(dt);
            Pickups.Tick(dt);
            if (State != GameState.Playing) return;

            if (clearAfterBoss)
            {
                clearAfterBoss = false;
                Enemies.KillAllInRadius(Player.Pos, 200f, false, 0);
                Projectiles.ClearHostile();
                Pickups.AttractAllGems();
            }
            if (victoryAt > 0 && Time.unscaledTime >= victoryAt) { EndRun(true); return; }
            if (pendingLevelUps > 0 && !chest.Active) OpenLevelUp();   // never interrupt a cache opening
        }

        void HandleMenuInput()
        {
            if (State == GameState.Playing)
            {
                if (Input.PausePressed) Pause();
                return;
            }
            bool back = Input.PausePressed || (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame);
            if (back) UI.Back();
        }

        void HandleDebugKeys()
        {
            if (!Debug.isDebugBuild || Keyboard.current == null || State != GameState.Playing) return;
            var k = Keyboard.current;
            if (k.f1Key.wasPressedThisFrame) Run.Time += 60f;
            if (k.f2Key.wasPressedThisFrame) AddXp(Run.XpToNext - Mathf.FloorToInt(Run.Xp));
            if (k.f3Key.wasPressedThisFrame) { Player.God = !Player.God; UI.Hud.Banner(Player.God ? "GOD ON" : "GOD OFF", Color.white, 1f); }
            if (k.f4Key.wasPressedThisFrame) Run.Time = Mathf.Max(Run.Time, GameData.RunDuration - 1f);
            if (k.f5Key.wasPressedThisFrame) { SaveSystem.Data.gold += 1000; SaveSystem.Save(); }
            if (k.f7Key.wasPressedThisFrame) { StartBossPractice(); return; }
            if (k.f6Key.wasPressedThisFrame) { Player.Bot = !Player.Bot; UI.Hud.Banner(Player.Bot ? "AUTOPILOT ON" : "AUTOPILOT OFF", Color.white, 1f); }
        }

        void UpdateTimeScale()
        {
            float scale;
            if (State == GameState.Title) scale = 1f;
            else if (State != GameState.Playing) scale = 0f;
            else if (Time.unscaledTime < hitstopUntil) scale = 0.05f;
            else if (chest.Active) scale = 0.25f;
            else if (Time.unscaledTime < slowmoUntil) scale = 0.3f;
            else if (victoryAt > 0) scale = 0.35f;
            else scale = 1f;
            Time.timeScale = scale;
        }

        void UpdatePost()
        {
            float dt = Time.unscaledDeltaTime;
            hurtPulse = Mathf.Max(0, hurtPulse - dt * 2.5f);
            chromaPulse = Mathf.Max(0, chromaPulse - dt * 3f);
            bool overdrive = State == GameState.Playing && Player.InOverdrive;
            float lowHp = State == GameState.Playing ? Mathf.Clamp01(1f - Player.Hp / Player.Stats.MaxHp * 3f) : 0f;
            if (vignette != null)
            {
                vignette.color.Override(Color.Lerp(Color.black, new Color(0.8f, 0f, 0.1f), Mathf.Max(hurtPulse, lowHp * 0.6f)));
                vignette.intensity.Override(0.3f + hurtPulse * 0.25f + lowHp * 0.1f);
            }
            if (chromatic != null) chromatic.intensity.Override(Mathf.Clamp01(chromaPulse + (overdrive ? 0.2f : 0.05f)));
        }

        public void HitStop(float duration) => hitstopUntil = Mathf.Max(hitstopUntil, Time.unscaledTime + duration);
        public void SlowMo(float duration) => slowmoUntil = Mathf.Max(slowmoUntil, Time.unscaledTime + duration);
        public void PulseHurt() => hurtPulse = 1f;
        public void PulseChromatic(float amount) => chromaPulse = Mathf.Max(chromaPulse, amount);
        public void FlashScreen(Color c) => UI.Hud.Flash(c);

        // ---------- State transitions ----------

        public void ShowTitle()
        {
            ClearWorld();
            Player.T.gameObject.SetActive(false);
            Rig.Target = null;
            Rig.Orbit = true;
            State = GameState.Title;
            UI.ShowTitle();
            Sfx.PlayMusic(0);
        }

        public void StartRun()
        {
            ClearWorld();
            Now = 0;
            Run = new RunState { Seed = System.Environment.TickCount };
            rng = new System.Random(Run.Seed);
            Loadout = new Loadout();
            Loadout.Apply(GameData.Weapon(WeaponId.Blaster));
            var stats = PlayerStats.Compute(Loadout, SaveSystem.Data);
            Player.ResetForRun(stats);
            Run.Rerolls = stats.Rerolls;
            Player.T.gameObject.SetActive(true);
            Rig.Orbit = false;
            Rig.Target = Player.T;
            Rig.Snap();
            spawner.Reset();
            pendingLevelUps = 0;
            victoryAt = -1f;
            clearAfterBoss = false;
            hitstopUntil = slowmoUntil = 0;
            resumeFrame = Time.frameCount;
            State = GameState.Playing;
            UI.ShowHud();
            Sfx.PlayMusic(1);
        }

        /// <summary>Restarts in the same mode as the run that just ended.</summary>
        public void Retry()
        {
            if (Run.Practice) StartBossPractice();
            else StartRun();
        }

        /// <summary>
        /// Boss practice: jumps straight to the COLOSSUS with a typical 15-minute build,
        /// skipping all earlier waves/events. No rewards, no saved stats.
        /// </summary>
        public void StartBossPractice()
        {
            StartRun();
            Run.Practice = true;
            // Roughly the build a run has at 15:00 (5 weapons, one evolution).
            (WeaponId id, int level)[] weapons = { (WeaponId.Blaster, 7), (WeaponId.Shotgun, 5), (WeaponId.Railgun, 6), (WeaponId.Missiles, 5), (WeaponId.Tesla, 5) };
            (PassiveId id, int level)[] passives = { (PassiveId.Power, 4), (PassiveId.Trigger, 3), (PassiveId.Plating, 3), (PassiveId.Reflex, 3), (PassiveId.MirrorCoat, 2) };
            foreach (var (id, level) in weapons)
                while (Loadout.Level(GameData.Weapon(id)) < level) Loadout.Apply(GameData.Weapon(id));
            foreach (var (id, level) in passives)
                for (int i = 0; i < level; i++) Loadout.Apply(GameData.Passive(id));
            Loadout.Evolve(GameData.Evolution(WeaponId.Blaster));   // Reflector (Blaster Lv7 + Mirror Coat)
            Player.ResetForRun(PlayerStats.Compute(Loadout, SaveSystem.Data));
            Run.Level = 45;
            // Start right at 15:00 so the boss arrives immediately, before the 14-minute spawn rate can flood the arena.
            Run.Time = GameData.RunDuration - 0.05f;
            spawner.SkipEventsBefore(GameData.RunDuration / 60f);
            // The real fight happens inside the leftover late-game horde; reproduce it so practice matches the real thing.
            spawner.SpawnCrowd(90, Player.Pos);
        }

        void ClearWorld()
        {
            foreach (var w in Loadout.Weapons) w.Dispose();
            Loadout = new Loadout();
            chest.Reset();
            Enemies.Clear();
            Projectiles.Clear();
            Pickups.Clear();
            Fx.Clear();
            Input.StopRumble();
        }

        public void Pause()
        {
            if (State != GameState.Playing) return;
            State = GameState.Paused;
            Input.StopRumble();
            UI.ShowPause();
        }

        public void Resume()
        {
            if (State != GameState.Paused && State != GameState.LevelUp) return;
            State = GameState.Playing;
            resumeFrame = Time.frameCount;
            UI.ShowHud();
        }

        public void EndRun(bool victory)
        {
            if (State == GameState.Results || State == GameState.Title) return;
            State = GameState.Results;
            Run.Victory = victory;
            Run.GoldEarned = Run.Practice ? 0 : Mathf.RoundToInt(Run.Gold) + (victory ? 300 : 0);
            if (!Run.Practice)
            {
                var save = SaveSystem.Data;
                save.runs++;
                save.gold += Run.GoldEarned;
                save.totalKills += Run.Kills;
                save.totalParries += Run.Parries;
                save.bestTime = Mathf.Max(save.bestTime, Run.Time);
                if (victory) save.wins++;
                SaveSystem.Save();
            }
            Input.StopRumble();
            Sfx.Play(victory ? SfxId.Victory : SfxId.Defeat, 0.9f);
            Sfx.PlayMusic(0);
            UI.ShowResults(Run);
        }

        public void OnBossDefeated(Vector3 pos, bool final)
        {
            Fx.Explosion(pos, new Color(1f, 0.3f, 0.4f), 4f);
            Fx.Ring(pos, Color.white, 1f, 30f, 1f);
            Rig.Shake(1f);
            Sfx.Play(SfxId.Explosion, 1f, 0.5f);
            if (final)
            {
                Run.BossKillTime = Run.Time;
                victoryAt = Time.unscaledTime + 3f;
                clearAfterBoss = true;
                UI.Hud.Banner("COLOSSUS DESTROYED", new Color(1f, 0.85f, 0.4f), 3f);
                return;
            }
            // Mid-boss: big rewards, the run continues.
            UI.Hud.Banner("WARDEN DESTROYED", new Color(1f, 0.85f, 0.4f), 3f);
            Pickups.Spawn(PickupType.Chest, pos, 1);
            for (int i = 0; i < 4; i++) Pickups.Spawn(PickupType.XpLarge, pos + Random.insideUnitSphere * 2.5f, 25);
            for (int i = 0; i < 10; i++) Pickups.Spawn(PickupType.Gold, pos + Random.insideUnitSphere * 3f, 3);
            Sfx.PlayMusic(1);
        }

        // ---------- Progression ----------

        public void AddXp(float amount)
        {
            Run.Xp += amount * Player.Stats.XpMult;
            while (Run.Xp >= Run.XpToNext)
            {
                Run.Xp -= Run.XpToNext;
                Run.Level++;
                pendingLevelUps++;
            }
        }

        void OpenLevelUp()
        {
            State = GameState.LevelUp;
            Input.StopRumble();
            var choices = UpgradePicker.Roll(Loadout, 3, rng);
            UI.ShowLevelUp(choices, Run.Rerolls);
            Sfx.Play(SfxId.LevelUp, 0.8f);
            if (Player.Bot) ChooseUpgrade(choices[rng.Next(choices.Count)]);
        }

        public void ChooseUpgrade(UpgradeChoice choice)
        {
            if (State != GameState.LevelUp) return;
            ApplyChoice(choice);
            pendingLevelUps--;
            if (pendingLevelUps <= 0) { Resume(); return; }
            var next = UpgradePicker.Roll(Loadout, 3, rng);
            if (Player.Bot) ChooseUpgrade(next[rng.Next(next.Count)]);
            else UI.ShowLevelUp(next, Run.Rerolls);
        }

        public void Reroll()
        {
            if (State != GameState.LevelUp || Run.Rerolls <= 0) return;
            Run.Rerolls--;
            UI.ShowLevelUp(UpgradePicker.Roll(Loadout, 3, rng), Run.Rerolls);
        }

        void ApplyChoice(UpgradeChoice c)
        {
            switch (c.Fallback)
            {
                case FallbackReward.Heal: Player.Heal(30f); return;
                case FallbackReward.Gold: Run.Gold += 25f; return;
            }
            if (c.Evolution != null)
            {
                Loadout.Evolve(c.Evolution);
                UI.Hud.Banner($"EVOLVED:  {c.Evolution.Def.Name.ToUpperInvariant()}", c.Evolution.Def.Color, 2.5f);
                Fx.Ring(Player.Pos, c.Evolution.Def.Color, 1f, 12f, 0.6f);
                Sfx.Play(SfxId.Chest, 0.8f, 1.12f);
                return;
            }
            Loadout.Apply(c.Def);
            Player.SetStats(PlayerStats.Compute(Loadout, SaveSystem.Data));
        }

        /// <summary>A collected cache plays its in-world opening sequence; rewards are granted as its shards are absorbed.</summary>
        public void OpenChest(Vector3 pos) => chest.Open(pos, rng);

        public bool ChestOpening => chest.Active;

        /// <summary>Grants one cache reward: a ready evolution always comes first, otherwise a random upgrade.</summary>
        public UpgradeChoice GrantCacheReward()
        {
            var ready = System.Array.Find(GameData.Evolutions, Loadout.CanEvolve);
            var choice = ready != null
                ? new UpgradeChoice { Def = GameData.Weapon(ready.Weapon), Evolution = ready }
                : UpgradePicker.Roll(Loadout, 1, rng)[0];
            ApplyChoice(choice);
            return choice;
        }
    }
}
