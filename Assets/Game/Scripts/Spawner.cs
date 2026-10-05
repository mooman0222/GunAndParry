using UnityEngine;

namespace GunAndParry
{
    /// <summary>Time-driven enemy spawning from the wave table plus scripted events.</summary>
    public sealed class Spawner
    {
        float accumulator, eliteTimer;
        int nextEvent;

        public void Reset()
        {
            accumulator = 0;
            nextEvent = 0;
            eliteTimer = 100f;
        }

        /// <summary>Marks every event before <paramref name="minute"/> as already fired (used by boss practice).</summary>
        public void SkipEventsBefore(float minute)
        {
            nextEvent = 0;
            while (nextEvent < GameData.Events.Length && GameData.Events[nextEvent].Minute < minute) nextEvent++;
            eliteTimer = float.MaxValue;
        }

        /// <summary>Spawns <paramref name="count"/> enemies from the late-game mix around the player.</summary>
        public void SpawnCrowd(int count, Vector3 playerPos)
        {
            var wave = GameData.WaveAt(13f);
            for (int i = 0; i < count; i++) Game.I.Enemies.Spawn(Pick(wave), RingPosition(playerPos, Random.Range(10f, GameData.SpawnRadius)));
        }

        public void Tick(float dt, float time, Vector3 playerPos)
        {
            var enemies = Game.I.Enemies;
            float minutes = time / 60f;

            while (nextEvent < GameData.Events.Length && minutes >= GameData.Events[nextEvent].Minute)
                Fire(GameData.Events[nextEvent++], playerPos);

            float rate = SpawnRate(minutes);
            if (enemies.Boss != null) rate *= enemies.Boss.Boss != null && enemies.Boss.Boss.IsFinal ? 0.25f : 0.5f;
            accumulator += rate * dt;
            var wave = GameData.WaveAt(minutes);
            while (accumulator >= 1f)
            {
                accumulator -= 1f;
                if (enemies.AliveCount < GameData.MaxEnemies) enemies.Spawn(Pick(wave), RingPosition(playerPos, GameData.SpawnRadius));
            }

            eliteTimer -= dt;
            if (eliteTimer <= 0 && minutes >= 2f && minutes < 15f)
            {
                eliteTimer = 100f;
                enemies.Spawn(Pick(wave), RingPosition(playerPos, GameData.SpawnRadius), true);
            }
        }

        /// <summary>Linear interpolation between wave segments so difficulty ramps smoothly.</summary>
        public static float SpawnRate(float minutes)
        {
            var waves = GameData.Waves;
            for (int i = waves.Length - 1; i >= 0; i--)
            {
                if (minutes < waves[i].StartMinute) continue;
                if (i == waves.Length - 1) return waves[i].SpawnsPerSecond;
                float t = Mathf.InverseLerp(waves[i].StartMinute, waves[i + 1].StartMinute, minutes);
                // Don't interpolate into the boss segment (it intentionally drops the rate).
                if (i + 1 == waves.Length - 1) t = 0;
                return Mathf.Lerp(waves[i].SpawnsPerSecond, waves[i + 1].SpawnsPerSecond, t);
            }
            return waves[0].SpawnsPerSecond;
        }

        static EnemyKind Pick(WaveSegment wave)
        {
            float total = 0;
            foreach (var w in wave.Weights) total += w;
            float r = Random.value * total;
            for (int i = 0; i < wave.Kinds.Length; i++)
            {
                r -= wave.Weights[i];
                if (r <= 0) return wave.Kinds[i];
            }
            return wave.Kinds[^1];
        }

        static Vector3 RingPosition(Vector3 center, float radius)
        {
            float a = Random.value * Mathf.PI * 2f;
            float r = radius + Random.value * 3f;
            return center + new Vector3(Mathf.Cos(a) * r, 0, Mathf.Sin(a) * r);
        }

        void Fire(WaveEvent ev, Vector3 playerPos)
        {
            var g = Game.I;
            switch (ev.Type)
            {
                case WaveEventType.Swarm:
                    for (int i = 0; i < ev.Count; i++)
                    {
                        float a = i * Mathf.PI * 2f / ev.Count;
                        g.Enemies.Spawn(ev.Kind, playerPos + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 17f);
                    }
                    g.UI.Hud.Banner("SWARM INCOMING", new Color(1f, 0.6f, 0.3f), 2f);
                    Sfx.Play(SfxId.Warning, 0.5f);
                    break;
                case WaveEventType.Elite:
                    for (int i = 0; i < ev.Count; i++) g.Enemies.Spawn(ev.Kind, RingPosition(playerPos, GameData.SpawnRadius), true);
                    g.UI.Hud.Banner("ELITE APPROACHING", new Color(1f, 0.82f, 0.3f), 2f);
                    Sfx.Play(SfxId.Warning, 0.5f, 1.2f);
                    break;
                case WaveEventType.Boss:
                    bool final = ev.Kind == EnemyKind.Colossus;
                    if (final)
                    {
                        g.Run.BossSpawned = true;
                        g.Run.BossSpawnTime = g.Run.Time;
                        // A still-living WARDEN withdraws so the arena belongs to the final boss.
                        var old = g.Enemies.Boss;
                        if (old != null) g.Enemies.Dismiss(old);
                    }
                    g.Enemies.Spawn(ev.Kind, RingPosition(playerPos, final ? 14f : 15f));
                    if (final) g.UI.Hud.Banner("WARNING: COLOSSUS  —  PARRY TO BREAK ITS ARMOR", new Color(1f, 0.45f, 0.2f), 3.5f);
                    else g.UI.Hud.Banner("WARNING: WARDEN AWAKENS", new Color(1f, 0.2f, 0.3f), 3f);
                    Sfx.Play(SfxId.Warning, 1f, 0.8f);
                    if (final) Sfx.Play(SfxId.Roar, 1f);
                    Sfx.PlayMusic(2);
                    break;
            }
        }
    }
}
