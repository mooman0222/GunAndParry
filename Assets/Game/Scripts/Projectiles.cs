using System.Collections.Generic;
using UnityEngine;

namespace GunAndParry
{
    public enum ShotStyle { Bullet, Pellet, Drone, Missile, Reflected, Perfect, Counter, EnemyOrb, EnemyGold, EnemyHeavy }

    public sealed class Projectile
    {
        public Transform T;
        public MeshFilter Filter;
        public MeshRenderer Renderer;
        public TrailRenderer Trail;
        public Vector3 Pos, Vel;
        public float Damage, Radius, Life, Knockback, ExplodeRadius;
        public int Pierce, Source, Bounces;
        public bool Hostile, Parryable;
        /// <summary>Gold boss shots: only a PERFECT parry deflects them; otherwise they hit even through a parry.</summary>
        public bool PerfectOnly;
        public ShotStyle Style;
        public Color Color;
        public Enemy Target;
        public readonly List<int> Hit = new(4);
    }

    /// <summary>Owns every projectile. Player shots collide with enemies through the spatial grid.</summary>
    public sealed class ProjectileManager
    {
        public const int SourceParry = 6, SourceNova = 7, SourcePerfect = 8, SourceCount = 9;
        const float PlayerRadius = 0.45f;

        readonly Transform root;
        readonly List<Projectile> active = new();
        readonly Stack<Projectile> pool = new();
        readonly List<Enemy> candidates = new();
        static readonly Color ParryColor = new(0.55f, 1f, 1f);
        public static readonly Color PerfectColor = new(1f, 0.85f, 0.35f);
        static readonly Color OrbColor = new(1f, 0.25f, 0.7f);
        static readonly Color HeavyColor = new(1f, 0.1f, 0.1f);

        public int Count => active.Count;
        // Code reached from inside Tick (a hit breaking boss armor, a revive...) must not shrink the list being iterated.
        bool ticking, clearHostilePending;

        public ProjectileManager(Transform parent)
        {
            root = new GameObject("Projectiles").transform;
            root.SetParent(parent, false);
        }

        public Projectile Fire(Vector3 pos, Vector3 dir, float speed, float damage, ShotStyle style, Color color, int source,
            float radius = 0.2f, float life = 1.2f, int pierce = 0, float knockback = 1f)
        {
            var p = Get(style, color);
            p.Pos = pos;
            p.Vel = dir * speed;
            p.Damage = damage;
            p.Radius = radius;
            p.Life = life;
            p.Pierce = pierce;
            p.Knockback = knockback;
            p.Source = source;
            p.Hostile = false;
            p.Parryable = false;
            p.PerfectOnly = false;
            p.Target = null;
            p.ExplodeRadius = 0;
            p.Bounces = 0;
            Place(p);
            return p;
        }

        public void FireMissile(Vector3 pos, Vector3 dir, float damage, float explodeRadius, int source, Enemy target, Color color)
        {
            var p = Fire(pos, dir, 9f, damage, ShotStyle.Missile, color, source, 0.3f, 3f, 0, 1.5f);
            p.ExplodeRadius = explodeRadius;
            p.Target = target;
        }

        /// <summary>Gold counter round loaded by a parry: fast, explodes on the first enemy it touches.</summary>
        public void FireCounter(Vector3 pos, Vector3 dir, float damage)
        {
            // Counts as parry damage: it bypasses boss armor and wears the armor gauge down.
            var p = Fire(pos, dir, 32f, damage, ShotStyle.Counter, PerfectColor, SourceParry, 0.35f, 0.9f, 0, 4f);
            p.ExplodeRadius = 2.2f;
        }

        public void FireEnemy(Vector3 pos, Vector3 dir, float speed, float damage, bool parryable, bool perfectOnly = false)
        {
            var style = perfectOnly ? ShotStyle.EnemyGold : parryable ? ShotStyle.EnemyOrb : ShotStyle.EnemyHeavy;
            var p = Get(style, perfectOnly ? PerfectColor : parryable ? OrbColor : HeavyColor);
            p.Pos = new Vector3(pos.x, 0.8f, pos.z);
            dir.y = 0;
            p.Vel = dir.normalized * speed;
            p.Damage = damage;
            p.Radius = parryable ? 0.28f : 0.32f;
            p.Life = 7f;
            p.Hostile = true;
            p.Parryable = parryable || perfectOnly;
            p.PerfectOnly = perfectOnly;
            p.Target = null;
            p.ExplodeRadius = 0;
            p.Bounces = 0;
            Place(p);
        }

        Projectile Get(ShotStyle style, Color color)
        {
            var p = pool.Count > 0 ? pool.Pop() : Create();
            p.Style = style;
            p.Color = color;
            p.Hit.Clear();
            var (mesh, scale, trailTime, trailWidth, intensity) = style switch
            {
                ShotStyle.Bullet => (Art.Diamond, new Vector3(0.3f, 0.3f, 0.75f), 0.08f, 0.2f, 2f),
                ShotStyle.Pellet => (Art.Sphere, Vector3.one * 0.2f, 0.06f, 0.15f, 2f),
                ShotStyle.Drone => (Art.Diamond, new Vector3(0.22f, 0.22f, 0.5f), 0.06f, 0.12f, 2f),
                ShotStyle.Missile => (Art.Diamond, new Vector3(0.3f, 0.3f, 0.6f), 0.3f, 0.25f, 1.8f),
                ShotStyle.Reflected => (Art.Sphere, Vector3.one * 0.45f, 0.18f, 0.4f, 3f),
                ShotStyle.Perfect => (Art.Octa, Vector3.one * 0.6f, 0.25f, 0.5f, 2.4f),
                ShotStyle.Counter => (Art.Octa, new Vector3(0.5f, 0.5f, 0.9f), 0.2f, 0.45f, 2.2f),
                ShotStyle.EnemyOrb => (Art.Sphere, Vector3.one * 0.5f, 0f, 0f, 1.6f),
                ShotStyle.EnemyGold => (Art.Octa, Vector3.one * 0.75f, 0f, 0f, 1.8f),
                _ => (Art.Octa, Vector3.one * 0.7f, 0f, 0f, 1.6f),
            };
            p.Filter.sharedMesh = mesh;
            p.Renderer.sharedMaterial = Art.Unlit(Art.Hdr(color, intensity));
            p.T.localScale = scale;
            p.Trail.enabled = trailTime > 0;
            p.Trail.time = trailTime;
            p.Trail.widthMultiplier = trailWidth;
            var c = Art.Hdr(color, 1.5f);
            p.Trail.startColor = c;
            c.a = 0;
            p.Trail.endColor = c;
            return p;
        }

        Projectile Create()
        {
            var go = new GameObject("Shot");
            go.transform.SetParent(root, false);
            var p = new Projectile { T = go.transform };
            p.Filter = go.AddComponent<MeshFilter>();
            p.Renderer = go.AddComponent<MeshRenderer>();
            p.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            p.Renderer.receiveShadows = false;
            p.Renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            p.Trail = go.AddComponent<TrailRenderer>();
            p.Trail.sharedMaterial = Art.Trail;
            p.Trail.minVertexDistance = 0.15f;
            p.Trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            p.Trail.receiveShadows = false;
            p.Trail.alignment = LineAlignment.View;
            return p;
        }

        void Place(Projectile p)
        {
            p.T.gameObject.SetActive(true);
            p.T.position = p.Pos;
            if (p.Vel.sqrMagnitude > 0.001f) p.T.rotation = Quaternion.LookRotation(p.Vel);
            p.Trail.Clear();
            active.Add(p);
        }

        void Release(int index)
        {
            var p = active[index];
            p.T.gameObject.SetActive(false);
            p.Target = null;
            // Swap-remove keeps this O(1); iteration runs backwards so order doesn't matter.
            active[index] = active[^1];
            active.RemoveAt(active.Count - 1);
            pool.Push(p);
        }

        public void Clear()
        {
            for (int i = active.Count - 1; i >= 0; i--) Release(i);
        }

        /// <summary>Removes all enemy shots. Safe to call from anywhere: during Tick it is deferred to the next Tick.</summary>
        public void ClearHostile()
        {
            if (ticking) { clearHostilePending = true; return; }
            for (int i = active.Count - 1; i >= 0; i--)
                if (active[i].Hostile) { Game.I.Fx.Glow(active[i].Pos, active[i].Color, 0.5f); Release(i); }
        }

        /// <summary>
        /// Turns parryable hostile shots within radius into empowered player shots. Returns how many.
        /// Shots already inside <paramref name="perfectRadius"/> (parried at the last moment) become PERFECT reflections.
        /// </summary>
        public int Reflect(Vector3 center, float radius, float perfectRadius, PlayerStats stats, out int perfect)
        {
            int count = 0;
            perfect = 0;
            float r2 = radius * radius, p2 = perfectRadius * perfectRadius;
            foreach (var p in active)
            {
                if (!p.Hostile || !p.Parryable) continue;
                var d = p.Pos - center; d.y = 0;
                if (d.sqrMagnitude > r2) continue;
                bool isPerfect = d.sqrMagnitude <= p2;
                if (p.PerfectOnly && !isPerfect) continue;

                // During a boss fight reflections fly at the boss: that is how its armor gets broken.
                var boss = Game.I.Enemies.Boss;
                var target = boss != null && (boss.Pos - p.Pos).sqrMagnitude < 30f * 30f ? boss : Game.I.Enemies.Nearest(p.Pos, 16f);
                var dir = target != null ? (target.Pos - p.Pos) : -p.Vel;
                dir.y = 0;
                var color = isPerfect ? PerfectColor : ParryColor;
                p.Hostile = false;
                p.Parryable = false;
                p.Vel = dir.normalized * (isPerfect ? 34f : 28f);
                p.Damage = p.Damage * stats.ReflectMult * stats.DamageMult * (isPerfect ? 2f : 1f);
                p.Pierce = isPerfect ? 99 : 1 + stats.ReflectPierce;
                p.Radius = isPerfect ? 0.45f : 0.35f;
                p.Life = 1.6f;
                p.Knockback = isPerfect ? 5f : 3f;
                p.Source = isPerfect ? SourcePerfect : SourceParry;
                p.PerfectOnly = false;
                p.Hit.Clear();
                p.Style = isPerfect ? ShotStyle.Perfect : ShotStyle.Reflected;
                p.Color = color;
                p.Filter.sharedMesh = isPerfect ? Art.Octa : Art.Sphere;
                p.Renderer.sharedMaterial = Art.Unlit(Art.Hdr(color, isPerfect ? 2.4f : 3f));
                p.T.localScale = Vector3.one * (isPerfect ? 0.6f : 0.45f);
                p.Trail.enabled = true;
                p.Trail.time = isPerfect ? 0.25f : 0.18f;
                p.Trail.widthMultiplier = isPerfect ? 0.5f : 0.4f;
                p.Trail.startColor = Art.Hdr(color, 1.5f);
                p.Trail.endColor = new Color(color.r, color.g, color.b, 0);
                p.Trail.Clear();
                Game.I.Fx.Sparks(p.Pos, color, isPerfect ? 16 : 8, 10f);
                count++;
                if (isPerfect) perfect++;
            }
            return count;
        }

        public bool AnyHostileWithin(Vector3 center, float radius)
        {
            foreach (var p in active)
            {
                if (!p.Hostile || !p.Parryable) continue;
                var d = p.Pos - center; d.y = 0;
                if (d.sqrMagnitude < radius * radius) return true;
            }
            return false;
        }

        public void Tick(float dt)
        {
            var player = Game.I.Player;
            var enemies = Game.I.Enemies;
            var playerPos = player.Pos;
            if (clearHostilePending) { clearHostilePending = false; ClearHostile(); }
            ticking = true;
            try { TickShots(dt, player, enemies, playerPos); }
            finally { ticking = false; }
        }

        void TickShots(float dt, Player player, EnemyManager enemies, Vector3 playerPos)
        {

            for (int i = active.Count - 1; i >= 0; i--)
            {
                var p = active[i];
                p.Life -= dt;
                if (p.Life <= 0)
                {
                    if (p.ExplodeRadius > 0) Explode(p);
                    Release(i);
                    continue;
                }

                if (p.Style == ShotStyle.Missile) Steer(p, dt);

                var prev = p.Pos;
                p.Pos += p.Vel * dt;
                p.T.position = p.Pos;
                if (p.Style == ShotStyle.Missile) p.T.rotation = Quaternion.LookRotation(p.Vel);
                else if (p.Style == ShotStyle.EnemyHeavy) p.T.Rotate(0, 360f * dt, 0);

                if (p.Hostile)
                {
                    var d = p.Pos - playerPos; d.y = 0;
                    if (d.sqrMagnitude < (p.Radius + PlayerRadius) * (p.Radius + PlayerRadius) && player.TryHit(p.Damage, !p.Parryable || p.PerfectOnly))
                    {
                        Game.I.Fx.Sparks(p.Pos, p.Color, 8);
                        Release(i);
                    }
                    else if (d.sqrMagnitude > GameData.DespawnRadius * GameData.DespawnRadius)
                    {
                        Release(i);
                    }
                    continue;
                }

                if (HitEnemies(p, prev, enemies)) Release(i);
            }
        }

        void Steer(Projectile p, float dt)
        {
            if (p.Target == null || !p.Target.Alive) p.Target = Game.I.Enemies.Nearest(p.Pos, 14f);
            float speed = Mathf.Min(p.Vel.magnitude + 30f * dt, 22f);
            var dir = p.Vel.normalized;
            if (p.Target != null)
            {
                var want = p.Target.Pos + Vector3.up * 0.5f - p.Pos;
                dir = Vector3.RotateTowards(dir, want.normalized, 7f * dt, 0f);
            }
            else dir.y = Mathf.Lerp(dir.y, 0, dt * 3f);
            p.Vel = dir * speed;
            if (p.Pos.y < 0.3f) p.Vel.y = Mathf.Max(p.Vel.y, 0);
        }

        /// <summary>Swept circle test along this frame's movement (no tunnelling at low frame rates). Returns true when consumed.</summary>
        bool HitEnemies(Projectile p, Vector3 prev, EnemyManager enemies)
        {
            var a = new Vector2(prev.x, prev.z);
            var b = new Vector2(p.Pos.x, p.Pos.z);
            var mid = (prev + p.Pos) * 0.5f;
            candidates.Clear();
            enemies.QueryForCollision(mid, p.Radius + Vector2.Distance(a, b) * 0.5f, candidates);
            foreach (var e in candidates)
            {
                if (!e.Alive || p.Hit.Contains(e.Uid)) continue;
                if (Geo.DistanceToSegment(new Vector2(e.Pos.x, e.Pos.z), a, b) > e.Radius + p.Radius) continue;

                if (p.ExplodeRadius > 0) { Explode(p); return true; }

                p.Hit.Add(e.Uid);
                var dir = p.Vel; dir.y = 0;
                enemies.Damage(e, p.Damage, dir.normalized, p.Knockback, p.Source);
                Game.I.Fx.Sparks(p.Pos, p.Color, 3, 7f);
                if (p.Bounces > 0 && Ricochet(p, enemies)) return false;
                if (p.Pierce-- <= 0) return true;
            }
            return false;
        }

        /// <summary>Redirects the shot at the nearest enemy it hasn't hit yet. Returns false when there is none.</summary>
        static bool Ricochet(Projectile p, EnemyManager enemies)
        {
            var next = enemies.NearestExcluding(p.Pos, 9f, p.Hit);
            if (next == null) return false;
            p.Bounces--;
            var d = next.Pos - p.Pos; d.y = 0;
            p.Vel = d.normalized * p.Vel.magnitude;
            p.T.rotation = Quaternion.LookRotation(p.Vel);
            p.Life = Mathf.Max(p.Life, 0.6f);
            return true;
        }

        void Explode(Projectile p)
        {
            var pos = new Vector3(p.Pos.x, 0.3f, p.Pos.z);
            candidates.Clear();
            Game.I.Enemies.Query(pos, p.ExplodeRadius + 1f, candidates);
            foreach (var e in candidates)
            {
                if (!e.Alive) continue;
                var d = e.Pos - pos; d.y = 0;
                if (d.magnitude <= p.ExplodeRadius + e.Radius)
                    Game.I.Enemies.Damage(e, p.Damage, d.normalized, 2f, p.Source);
            }
            Game.I.Fx.Explosion(pos, p.Color, 0.8f);
            Game.I.Fx.Ring(pos, p.Color, 0.5f, p.ExplodeRadius * 2f, 0.3f);
            Sfx.Play(SfxId.Explosion, 0.35f, 1.4f);
        }
    }
}
