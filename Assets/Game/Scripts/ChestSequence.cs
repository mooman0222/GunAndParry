using System.Collections.Generic;
using UnityEngine;

namespace GunAndParry
{
    /// <summary>
    /// In-world "supply cache" opening: the game keeps running in slow motion while the cache bursts,
    /// reward shards orbit the player and are absorbed one by one. A shrinking gold ring offers an optional
    /// PARRY timing bonus (+1 reward). Runs on unscaled time so the bonus timing is real-time.
    /// </summary>
    public sealed class ChestSequence
    {
        public enum Tier { Standard, Rare, Prime }

        const float SlowScale = 0.25f;
        const float OrbitIn = 0.45f;          // shards fly from the cache into orbit
        const float PromptStart = 0.3f, PromptEnd = 1.25f, PromptWindow = 0.13f;
        const float AbsorbStart = 1.45f, AbsorbGap = 0.17f, AbsorbFlight = 0.16f;
        const float PromptTo = Player.PerfectRadius * 2f;

        sealed class Shard
        {
            public Transform T;
            public TrailRenderer Trail;
            public bool Absorbed;
            public float AbsorbAt;
        }

        readonly Transform root;
        readonly List<Shard> shards = new();
        Transform lid;
        Vector3 origin, lidVel;
        float time;
        bool bonusJudged, promptActive;
        int rewardsGiven, queued;
        Tier tier;
        Color color;

        public bool Active { get; private set; }

        public ChestSequence(Transform parent)
        {
            root = new GameObject("ChestSequence").transform;
            root.SetParent(parent, false);
        }

        public static Color TierColor(Tier t) => t switch
        {
            Tier.Prime => new Color(0.55f, 1f, 1f),
            Tier.Rare => new Color(1f, 0.45f, 0.95f),
            _ => new Color(1f, 0.82f, 0.35f),
        };

        /// <summary>Starts the sequence for a collected cache (queues it if one is already playing).</summary>
        public void Open(Vector3 pos, System.Random rng)
        {
            if (Active) { queued++; return; }
            double r = rng.NextDouble();
            tier = r < 0.06 ? Tier.Prime : r < 0.26 ? Tier.Rare : Tier.Standard;
            int count = tier == Tier.Prime ? 3 : tier == Tier.Rare ? 2 : 1;
            Begin(pos, count);
        }

        void Begin(Vector3 pos, int count)
        {
            var g = Game.I;
            Active = true;
            time = 0;
            bonusJudged = false;
            rewardsGiven = 0;
            origin = new Vector3(pos.x, 0, pos.z);
            color = TierColor(tier);
            g.Run.Chests++;

            // Burst: freeze-frame, light pillar with angled rays, shockwave that clears space around the player.
            g.HitStop(0.08f);
            g.Rig.Punch(1.2f);
            g.Rig.Shake(0.35f);
            g.Input.Rumble(0.5f, 0.7f, 0.25f);
            g.Fx.Explosion(origin + Vector3.up * 0.6f, color, 1.6f);
            g.Fx.Ring(origin, Color.white, 1f, 14f, 0.45f);
            g.Fx.Ring(origin, color, 0.5f, 9f, 0.7f);
            g.Fx.Beam(new[] { origin, origin + Vector3.up * 16f }, 2, color, 1.8f, 0.9f);
            g.Fx.Beam(new[] { origin, origin + Vector3.up * 16f }, 2, Color.white, 0.5f, 0.7f);
            for (int i = 0; i < 6; i++)
            {
                var d = Quaternion.Euler(0, i * 60f + Random.Range(-10f, 10f), 0) * Vector3.forward;
                g.Fx.Beam(new[] { origin + Vector3.up * 0.5f, origin + d * 5f + Vector3.up * 9f }, 2, color, 0.22f, 0.6f);
            }
            g.Enemies.Repel(origin, 7f, 16f, 0.8f);
            g.Projectiles.ClearHostile();
            g.UI.Hud.Banner(tier switch { Tier.Prime => "PRIME CACHE", Tier.Rare => "RARE CACHE", _ => "SUPPLY CACHE" }, color, 1.6f);
            Sfx.Play(SfxId.ChestBurst, 0.85f, tier == Tier.Prime ? 1.12f : tier == Tier.Rare ? 1.06f : 1f, 0f);

            // The lid is blown off and tumbles away.
            lid = Art.Part("Lid", root, Art.Cube, Art.Lit(new Color(0.72f, 0.52f, 0.14f), new Color(0.4f, 0.28f, 0.06f)), origin + Vector3.up * 0.8f, new Vector3(1.16f, 0.22f, 0.86f)).transform;
            lidVel = new Vector3(Random.Range(-2f, 2f), 11f, Random.Range(-2f, 2f));

            for (int i = 0; i < count; i++) AddShard();
            CreatePrompt();
        }

        void AddShard()
        {
            var go = Art.Part("Shard", root, Art.Gem, Art.Unlit(Art.Hdr(color, 2.2f)), origin + Vector3.up, Vector3.one * 1.6f, false);
            var tr = go.AddComponent<TrailRenderer>();
            tr.sharedMaterial = Art.Trail;
            tr.time = 0.4f;
            tr.widthMultiplier = 0.45f;
            tr.startColor = Art.Hdr(color, 1.6f);
            tr.endColor = new Color(color.r, color.g, color.b, 0f);
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            shards.Add(new Shard { T = go.transform, Trail = tr });
        }

        // The bonus prompt is a foreground HUD element (dimmed screen, target + closing ring, button glyph), not an in-world decal.
        void CreatePrompt() => promptActive = true;

        /// <summary>Ticked with unscaled time while the game is playing (freezes with pause/level-up screens).</summary>
        public void Tick(float dt, GameInput input)
        {
            if (!Active) return;
            var g = Game.I;
            time += dt;
            var center = g.Player.Pos + Vector3.up * 1.4f;

            // Lid physics (unscaled so it reads during slow motion).
            if (lid != null)
            {
                lidVel.y -= 30f * dt;
                lid.position += lidVel * dt;
                lid.Rotate(new Vector3(400f, 130f, 260f) * dt, Space.Self);
                if (lid.position.y < -1f) { Object.Destroy(lid.gameObject); lid = null; }
            }

            // Parry prompt: the ring shrinks onto the PERFECT zone; parry as it lands for a bonus reward.
            UpdatePrompt(input, g);

            // Shards: fly into orbit, then get absorbed one by one.
            int n = shards.Count;
            for (int i = 0; i < n; i++)
            {
                var s = shards[i];
                if (s.Absorbed) continue;
                float absorbAt = AbsorbStart + i * AbsorbGap;
                float a = time * 4.5f + i * Mathf.PI * 2f / n;
                var orbit = center + new Vector3(Mathf.Cos(a), Mathf.Sin(time * 6f + i) * 0.15f, Mathf.Sin(a)) * 1.9f;
                Vector3 pos;
                if (time < OrbitIn) pos = Vector3.Lerp(origin + Vector3.up * 1.2f, orbit, Smooth(time / OrbitIn)) + Vector3.up * Mathf.Sin(time / OrbitIn * Mathf.PI) * 2.5f;
                else if (time < absorbAt) pos = orbit;
                else
                {
                    float k = Mathf.Clamp01((time - absorbAt) / AbsorbFlight);
                    pos = Vector3.Lerp(orbit, center, k * k);
                    if (k >= 1f) { Absorb(s, i); continue; }
                }
                s.T.position = pos;
                s.T.Rotate(0, 720f * dt, 0, Space.World);
                if (Random.value < 0.6f) g.Fx.Glow(pos, color, 0.45f);
            }

            bool allDone = shards.TrueForAll(s => s.Absorbed);
            if (allDone && time > AbsorbStart) Finish();
        }

        void UpdatePrompt(GameInput input, Game g)
        {
            if (!promptActive || time < PromptStart) return;
            float t = Mathf.Clamp01((time - PromptStart) / (PromptEnd - PromptStart));
            bool inWindow = Mathf.Abs(time - PromptEnd) <= PromptWindow;
            g.UI.Hud.CachePrompt(t, inWindow);

            if (!bonusJudged && input.ParryPressed)
            {
                bonusJudged = true;
                if (inWindow) Bonus(g);
                else Miss(g);
            }
            if (!bonusJudged && time > PromptEnd + PromptWindow) { bonusJudged = true; Miss(g); }
        }

        void Miss(Game g)
        {
            promptActive = false;
            g.UI.Hud.CachePromptResult(false);
        }

        void Bonus(Game g)
        {
            AddShard();
            var s = shards[^1];
            s.T.position = g.Player.Pos + Vector3.up * 1.4f;
            g.Fx.Ring(g.Player.Pos, Color.white, PromptTo, PromptTo * 2.4f, 0.35f);
            g.Fx.Sparks(g.Player.Pos + Vector3.up, color, 30, 14f);
            g.UI.Hud.CachePromptResult(true);
            g.Rig.Punch(1f);
            Sfx.Play(SfxId.Perfect, 0.7f, 1f, 0f);
            Sfx.Play(SfxId.Parry, 0.45f, 1.5f, 0f);
            promptActive = false;
        }

        void FadePrompt()
        {
            if (!promptActive) return;
            promptActive = false;
            Game.I.UI.Hud.HideCachePrompt();
        }

        void Absorb(Shard s, int index)
        {
            var g = Game.I;
            s.Absorbed = true;
            Object.Destroy(s.T.gameObject);
            var choice = g.GrantCacheReward();
            rewardsGiven++;
            g.Fx.Ring(g.Player.Pos, color, 1f, 4.5f, 0.25f);
            g.Fx.Sparks(g.Player.Pos + Vector3.up * 1.2f, color, 14, 9f);
            g.Input.Rumble(0.2f, 0.5f, 0.06f);
            // Pentatonic climb per shard, like chained parries.
            int[] scale = { 0, 2, 4, 7, 9, 12 };
            Sfx.Play(SfxId.Shard, 0.6f, Mathf.Pow(2f, scale[Mathf.Min(index, scale.Length - 1)] / 12f), 0f);
            g.UI.Hud.Reward(choice, color);
        }

        void Finish()
        {
            var g = Game.I;
            g.Fx.Ring(g.Player.Pos, color, 1f, 10f, 0.5f);
            Sfx.Play(SfxId.Chest, 0.55f, 1.2f, 0f);
            Clear();
            if (queued > 0)
            {
                queued--;
                Open(g.Player.Pos, new System.Random());
            }
        }

        public void Clear()
        {
            foreach (var s in shards) if (s.T != null) Object.Destroy(s.T.gameObject);
            shards.Clear();
            if (lid != null) Object.Destroy(lid.gameObject);
            lid = null;
            FadePrompt();
            Active = false;
        }

        public void Reset()
        {
            Clear();
            queued = 0;
        }

        static float Smooth(float x) => x * x * (3f - 2f * x);
    }
}
