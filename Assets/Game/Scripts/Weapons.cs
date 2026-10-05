using System.Collections.Generic;
using UnityEngine;

namespace GunAndParry
{
    public static class Geo
    {
        public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return Vector2.Distance(p, a + ab * t);
        }
    }

    public abstract class Weapon
    {
        public WeaponId Id { get; private set; }
        public int Level = 1;
        public bool Evolved;
        protected float timer;

        public UpgradeDef Def => GameData.Weapon(Id);
        /// <summary>Display data: the evolved form once evolved.</summary>
        public UpgradeDef Display => Evolved ? GameData.Evolution(Id).Def : Def;
        protected int Source => (int)Id;
        protected Color Color => Evolved ? GameData.Evolution(Id).Def.Color : Def.Color;
        protected bool L(int level) => Level >= level;

        public static Weapon Create(WeaponId id)
        {
            Weapon w = id switch
            {
                WeaponId.Blaster => new Blaster(),
                WeaponId.Shotgun => new Shotgun(),
                WeaponId.Drones => new Drones(),
                WeaponId.Railgun => new Railgun(),
                WeaponId.Missiles => new Missiles(),
                _ => new Tesla(),
            };
            w.Id = id;
            return w;
        }

        public abstract void Tick(Player p, float dt);
        /// <summary>Successful parry hook (evolutions turn parries into attacks).</summary>
        public virtual void OnParry(Player p, bool perfect) { }
        public virtual void Dispose() { }

        protected static ProjectileManager Shots => Game.I.Projectiles;
        protected static EnemyManager Enemies => Game.I.Enemies;

        /// <summary>Calls fire for <paramref name="count"/> directions fanned around <paramref name="dir"/>.</summary>
        protected static void Fan(Vector3 dir, int count, float spreadDeg, System.Action<Vector3> fire)
        {
            float start = -spreadDeg * (count - 1) * 0.5f;
            for (int i = 0; i < count; i++) fire(Quaternion.Euler(0, start + spreadDeg * i, 0) * dir);
        }
    }

    sealed class Blaster : Weapon
    {
        public override void Tick(Player p, float dt)
        {
            timer -= dt;
            if (timer > 0) return;
            timer += 0.3f * (L(4) ? 0.8f : 1f) * p.CooldownMult;
            if (timer < 0) timer = 0;
            int count = 1 + (L(2) ? 1 : 0) + (L(6) ? 1 : 0) + p.Stats.Amount;
            float dmg = 12f * (L(3) ? 1.3f : 1f) * (L(7) ? 1.4f : 1f) * p.Stats.DamageMult;
            int pierce = (L(5) ? 1 : 0) + (L(7) ? 1 : 0) + (p.InOverdrive ? 1 : 0);
            var muzzle = p.Muzzle;
            Fan(p.AimDir, count, 6f, d =>
            {
                var shot = Shots.Fire(muzzle, d, 26f, dmg, ShotStyle.Bullet, Color, Source, 0.22f, 1.0f, pierce, 1.2f);
                if (Evolved) shot.Bounces = 2;   // Reflector
            });
            Game.I.Fx.Glow(muzzle, Color, 0.35f);
            if (p.TryConsumeCounterRound())
            {
                // Parry-loaded round: rides on top of the normal volley.
                Shots.FireCounter(muzzle, p.AimDir, dmg * 4f);
                Game.I.Fx.Glow(muzzle, ProjectileManager.PerfectColor, 0.7f);
                p.Kick(0.9f);
                Sfx.Play(SfxId.CounterShot, 0.55f);
            }
            else p.Kick(0.35f);
            Sfx.Play(SfxId.Shoot, 0.45f);
        }
    }

    sealed class Shotgun : Weapon
    {
        public override void Tick(Player p, float dt)
        {
            timer -= dt;
            if (timer > 0) return;
            timer = 1.5f * (L(4) ? 0.8f : 1f) * p.CooldownMult;
            int pellets = 5 + (L(2) ? 2 : 0) + (L(6) ? 3 : 0) + p.Stats.Amount * 2;
            float dmg = PelletDamage(p);
            float life = 0.3f * (L(5) ? 1.3f : 1f);
            var muzzle = p.Muzzle;
            Fan(p.AimDir, pellets, 44f / pellets, d =>
                Shots.Fire(muzzle, Quaternion.Euler(0, Random.Range(-2f, 2f), 0) * d, Random.Range(19f, 25f), dmg, ShotStyle.Pellet, Color, Source, 0.25f, life, 1, 5f));
            Game.I.Fx.Sparks(muzzle, Color, 10, 10f);
            Game.I.Rig.Shake(0.12f);
            p.Kick(1f);
            Sfx.Play(SfxId.Shotgun, 0.7f);
        }

        float PelletDamage(Player p) => 8f * (L(3) ? 1.3f : 1f) * (L(7) ? 1.5f : 1f) * p.Stats.DamageMult;

        // Nova Burst: a full pellet ring on every parry.
        public override void OnParry(Player p, bool perfect)
        {
            if (!Evolved) return;
            var center = p.Pos + Vector3.up * 1.2f;
            for (int ring = 0; ring < (perfect ? 2 : 1); ring++)
            {
                const int n = 20;
                for (int i = 0; i < n; i++)
                {
                    var d = Quaternion.Euler(0, (i + ring * 0.5f) * 360f / n, 0) * Vector3.forward;
                    Shots.Fire(center, d, 21f + ring * 4f, PelletDamage(p) * 1.5f, ShotStyle.Pellet, Color, Source, 0.28f, 0.4f, 1, 6f);
                }
            }
            Game.I.Fx.Ring(p.Pos, Color, 0.5f, 9f, 0.3f);
            Sfx.Play(SfxId.Shotgun, 0.6f, 0.9f);
        }
    }

    sealed class Drones : Weapon
    {
        readonly List<Transform> drones = new();
        readonly List<float> timers = new();
        readonly List<float> guard = new();
        float angle;

        public override void Tick(Player p, float dt)
        {
            int count = 1 + (L(2) ? 1 : 0) + (L(5) ? 1 : 0) + (L(7) ? 1 : 0) + p.Stats.Amount;
            while (drones.Count < count)
            {
                var go = new GameObject("Drone");
                go.transform.SetParent(Game.I.WorldRoot, false);
                Art.Part("Body", go.transform, Art.Octa, Art.Lit(new Color(0.1f, 0.2f, 0.1f), Art.Hdr(Def.Color, 1.5f)), Vector3.zero, Vector3.one * 0.45f, false);
                Art.Part("Eye", go.transform, Art.Sphere, Art.Unlit(Art.Hdr(Def.Color, 2.6f)), new Vector3(0, 0, 0.2f), Vector3.one * 0.15f, false);
                drones.Add(go.transform);
                timers.Add(Random.value * 0.5f);
                guard.Add(0f);
            }

            float cd = 0.9f * (L(4) ? 0.75f : 1f) * p.CooldownMult;
            float dmg = 9f * (L(3) ? 1.3f : 1f) * (L(6) ? 1.4f : 1f) * p.Stats.DamageMult;
            angle += dt * 2.2f;
            for (int i = 0; i < drones.Count; i++)
            {
                float a = angle + i * Mathf.PI * 2f / drones.Count;
                var pos = p.Pos + new Vector3(Mathf.Cos(a) * 1.8f, 1.3f + Mathf.Sin(Game.Now * 3f + i) * 0.15f, Mathf.Sin(a) * 1.8f);
                var t = drones[i];
                t.position = pos;
                timers[i] -= dt;
                guard[i] -= dt;

                // Guardian Drones: each drone parries enemy bullets that come near it.
                if (Evolved && guard[i] <= 0 && Shots.AnyHostileWithin(pos, 1.6f))
                {
                    int n = Shots.Reflect(pos, 1.6f, 0f, p.Stats, out _);
                    if (n > 0)
                    {
                        guard[i] = 0.8f;
                        p.Heal(n, false);
                        Game.I.Run.Parries += n;
                        Game.I.Fx.Ring(new Vector3(pos.x, 0, pos.z), Color, 0.3f, 3.2f, 0.2f);
                        Sfx.Play(SfxId.Parry, 0.25f, 1.5f, 0f);
                    }
                }

                var target = Enemies.Nearest(pos, 11f);
                if (target != null)
                {
                    var dir = target.Pos - pos; dir.y = 0;
                    t.rotation = Quaternion.LookRotation(dir);
                    if (timers[i] <= 0)
                    {
                        timers[i] = cd;
                        Shots.Fire(pos, dir.normalized, 22f, dmg, ShotStyle.Drone, Color, Source, 0.2f, 0.9f, 0, 0.8f);
                        Sfx.Play(SfxId.Shoot, 0.25f, 1.5f);
                    }
                }
                else t.rotation = Quaternion.Euler(0, a * Mathf.Rad2Deg, 0);
            }
        }

        public override void Dispose()
        {
            foreach (var d in drones) if (d != null) Object.Destroy(d.gameObject);
            drones.Clear();
        }
    }

    sealed class Railgun : Weapon
    {
        static readonly Vector3[] points = new Vector3[2];
        static readonly List<Enemy> hits = new();

        float Damage(Player p) => 45f * (L(2) ? 1.4f : 1f) * (L(6) ? 1.5f : 1f) * p.Stats.DamageMult;
        float Width => 0.8f * (L(4) ? 1.5f : 1f);

        public override void Tick(Player p, float dt)
        {
            timer -= dt;
            if (timer > 0) return;
            timer = 3.2f * (L(3) ? 0.85f : 1f) * (L(7) ? 0.8f : 1f) * p.CooldownMult;
            int beams = 1 + (L(5) ? 2 : 0) + p.Stats.Amount;
            float dmg = Damage(p), width = Width;
            Fan(p.AimDir, beams, 18f, d => FireBeam(p.Muzzle, d, 28f, width, dmg));
            Game.I.Rig.Shake(0.25f);
            p.Kick(1.3f);
            Sfx.Play(SfxId.Rail, 0.7f);
        }

        // Counter Rail: every parry answers with a beam at your aim.
        public override void OnParry(Player p, bool perfect)
        {
            if (!Evolved) return;
            float dmg = Damage(p), width = Width;
            Fan(p.AimDir, perfect ? 3 : 1, 14f, d => FireBeam(p.Muzzle, d, 28f, width, dmg));
            Game.I.Rig.Shake(0.2f);
            p.Kick(1.3f);
            Sfx.Play(SfxId.Rail, 0.5f, 1.15f);
        }

        void FireBeam(Vector3 from, Vector3 dir, float length, float width, float dmg)
        {
            var to = from + dir * length;
            hits.Clear();
            var a = new Vector2(from.x, from.z);
            var b = new Vector2(to.x, to.z);
            foreach (var e in Enemies.Active)
            {
                if (!e.Alive) continue;
                if (Geo.DistanceToSegment(new Vector2(e.Pos.x, e.Pos.z), a, b) <= width * 0.5f + e.Radius) hits.Add(e);
            }
            foreach (var e in hits) Enemies.Damage(e, dmg, dir, 2f, Source);
            points[0] = from; points[1] = to;
            Game.I.Fx.Beam(points, 2, Color, width, 0.35f);
            Game.I.Fx.Beam(points, 2, Color.white, width * 0.35f, 0.25f);
            Game.I.Fx.Sparks(from, Color, 12, 12f);
        }
    }

    sealed class Missiles : Weapon
    {
        static readonly List<Enemy> targets = new();

        float Damage(Player p) => 16f * (L(3) ? 1.3f : 1f) * p.Stats.DamageMult;
        float Radius => 2.2f * (L(4) ? 1.3f : 1f);

        public override void Tick(Player p, float dt)
        {
            timer -= dt;
            if (timer > 0) return;
            if (Enemies.Nearest(p.Pos, 16f) == null) { timer = 0.2f; return; }
            // Overdrive Swarm doubles the fire rate during Overdrive.
            timer = 2.4f * (L(6) ? 0.8f : 1f) * p.CooldownMult * (Evolved && p.InOverdrive ? 0.5f : 1f);
            int count = 2 + (L(2) ? 1 : 0) + (L(5) ? 1 : 0) + (L(7) ? 2 : 0) + p.Stats.Amount;
            Launch(p, count);
            Sfx.Play(SfxId.Missile, 0.6f);
        }

        // Overdrive Swarm: every parry launches a salvo.
        public override void OnParry(Player p, bool perfect)
        {
            if (!Evolved) return;
            Launch(p, perfect ? 5 : 3);
            Sfx.Play(SfxId.Missile, 0.5f, 1.2f);
        }

        void Launch(Player p, int count)
        {
            float dmg = Damage(p), radius = Radius;
            targets.Clear();
            Enemies.Query(p.Pos, 16f, targets);
            for (int i = 0; i < count; i++)
            {
                var target = targets.Count > 0 ? targets[Random.Range(0, targets.Count)] : null;
                var dir = Quaternion.Euler(0, Random.Range(-100f, 100f), 0) * p.AimDir;
                Shots.FireMissile(p.Pos + Vector3.up * 1.2f, dir, dmg, radius, Source, target, Color);
            }
        }
    }

    sealed class Tesla : Weapon
    {
        static readonly Vector3[] points = new Vector3[8];
        static readonly List<int> chainHit = new();
        static readonly List<Enemy> nearby = new();

        float Damage(Player p) => 14f * (L(3) ? 1.3f : 1f) * (L(6) ? 1.4f : 1f) * p.Stats.DamageMult;

        public override void Tick(Player p, float dt)
        {
            timer -= dt;
            if (timer > 0) return;
            var first = Enemies.Nearest(p.Pos, 8f);
            if (first == null) { timer = 0.15f; return; }
            timer = 1.5f * (L(4) ? 0.8f : 1f) * p.CooldownMult;
            float dmg = Damage(p);
            int chains = 3 + (L(2) ? 1 : 0) + (L(5) ? 2 : 0) + p.Stats.Amount;
            int bolts = 1 + (L(7) ? 1 : 0);
            for (int b = 0; b < bolts; b++)
            {
                chainHit.Clear();
                var current = b == 0 ? first : Enemies.Nearest(p.Pos, 8f, first);
                var from = p.Pos + Vector3.up;
                for (int c = 0; c <= chains && current != null; c++)
                {
                    var to = current.Pos + Vector3.up * 0.6f;
                    Zap(from, to);
                    chainHit.Add(current.Uid);
                    Enemies.Damage(current, dmg, (to - from).normalized, 0.5f, Source);
                    from = to;
                    current = Enemies.NearestExcluding(to, 5f, chainHit);
                }
            }
            Sfx.Play(SfxId.Tesla, 0.6f);
        }

        // Arc Aegis: every parry discharges into the six closest enemies; PERFECT also stuns them.
        public override void OnParry(Player p, bool perfect)
        {
            if (!Evolved) return;
            nearby.Clear();
            Enemies.Query(p.Pos, 7f, nearby);
            var center = p.Pos;
            nearby.Sort((x, y) => (x.Pos - center).sqrMagnitude.CompareTo((y.Pos - center).sqrMagnitude));
            float dmg = Damage(p) * 2f;
            var from = p.Pos + Vector3.up;
            for (int i = 0; i < nearby.Count && i < 6; i++)
            {
                var e = nearby[i];
                if (!e.Alive) continue;
                Zap(from, e.Pos + Vector3.up * 0.6f);
                if (perfect && e.Boss == null) e.StunUntil = Mathf.Max(e.StunUntil, Game.Now + 1f);
                Enemies.Damage(e, dmg, (e.Pos - center).normalized, 1f, Source);
            }
            Sfx.Play(SfxId.Tesla, 0.7f, 0.9f);
        }

        void Zap(Vector3 from, Vector3 to)
        {
            int n = points.Length;
            var side = Vector3.Cross((to - from).normalized, Vector3.up);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                float jitter = (i == 0 || i == n - 1) ? 0 : Random.Range(-0.35f, 0.35f);
                points[i] = Vector3.Lerp(from, to, t) + side * jitter + Vector3.up * Random.Range(-0.15f, 0.15f);
            }
            Game.I.Fx.Beam(points, n, Color, 0.22f, 0.18f);
            Game.I.Fx.Sparks(to, Color, 4, 6f);
        }
    }
}
