using System.Collections.Generic;
using UnityEngine;

namespace GunAndParry
{
    public sealed class Enemy
    {
        public int Uid;
        public EnemyDef Def;
        public EnemyKind Kind => Def.Kind;
        public GameObject Go;
        public Transform T;
        public MeshRenderer[] Renderers;
        public Material[] BaseMaterials;
        public Transform Spinner;   // optional rotating part
        public Vector3 Pos, Knock;
        public float Hp, MaxHp, Radius, Speed, Damage;
        public bool Elite, Alive, Flashing;
        public float FireTimer, StunUntil, FlashUntil, FuseAt = -1f, LaunchedUntil;
        public int ParriedWindow = -1;
        public float Strafe;
        public BossAI Boss;
        // Telegraphed lunge (Brutes and melee elites): windup -> lunge -> recover. Parrying the strike BREAKs the enemy.
        public bool HasLunge;
        public LungePhase Lunge;
        public float LungeTime, LungeCooldown;
        public Vector3 LungeDir;
        public bool InStrike => Lunge == LungePhase.Strike || (Lunge == LungePhase.Windup && LungeTime >= EnemyManager.WindupTime - 0.25f);
    }

    public enum LungePhase { Idle, Windup, Strike, Recover }

    public sealed class EnemyManager
    {
        public const float MaxGridRadius = 1.5f;
        public const float WindupTime = 0.65f;
        const float StrikeTime = 0.22f, RecoverTime = 0.5f, LungeRange = 3.4f, LungeSpeed = 13f;
        static readonly Color Warning = new(1f, 0.2f, 0.15f);
        static Material warningMat;
        const float PlayerRadius = 0.45f;
        const float BomberBlastRadius = 2.6f;
        static readonly Color Gold = new(1f, 0.82f, 0.3f);

        public readonly List<Enemy> Active = new();
        public Enemy Boss { get; private set; }
        public int AliveCount { get; private set; }

        readonly List<Enemy> big = new();
        readonly List<Enemy> gridItems = new();
        readonly List<Enemy> dead = new();
        readonly List<Enemy> scratch = new();
        readonly List<int> idx = new();
        readonly SpatialGrid grid = new(2f);
        readonly Dictionary<(EnemyKind, bool), Stack<Enemy>> pools = new();
        readonly Transform root;
        int nextUid = 1;

        public EnemyManager(Transform parent)
        {
            root = new GameObject("Enemies").transform;
            root.SetParent(parent, false);
        }

        // ---------- Spawning ----------

        public Enemy Spawn(EnemyKind kind, Vector3 pos, bool elite = false)
        {
            var key = (kind, elite);
            if (!pools.TryGetValue(key, out var pool)) pools[key] = pool = new Stack<Enemy>();
            var e = pool.Count > 0 ? pool.Pop() : Create(kind, elite);
            var def = GameData.Enemy(kind);
            float minutes = Game.I.Run.Time / 60f;

            e.Uid = nextUid++;
            e.Def = def;
            e.Elite = elite;
            e.MaxHp = GameData.IsBoss(kind) ? def.Hp : def.Hp * GameData.HpScale(minutes) * (elite ? 12f : 1f);
            e.Hp = e.MaxHp;
            e.Radius = def.Radius * (elite ? 1.6f : 1f);
            e.Speed = def.Speed * (elite ? 0.85f : 1f) * Random.Range(0.9f, 1.1f);
            e.Damage = def.Damage * GameData.DamageScale(minutes) * (elite ? 1.5f : 1f);
            e.FireTimer = def.FireInterval * Random.Range(0.5f, 1f);
            e.Knock = Vector3.zero;
            e.StunUntil = e.FlashUntil = e.LaunchedUntil = 0f;
            e.FuseAt = -1f;
            e.ParriedWindow = -1;
            e.Strafe = Random.value < 0.5f ? -1f : 1f;
            e.HasLunge = kind == EnemyKind.Brute || (elite && (kind == EnemyKind.Crawler || kind == EnemyKind.Runner));
            e.Lunge = LungePhase.Idle;
            e.LungeCooldown = Random.Range(0.8f, 2f);
            e.Pos = new Vector3(pos.x, 0, pos.z);
            e.T.position = e.Pos;
            e.T.localScale = Vector3.one * (elite ? 1.6f : 1f);
            RestoreMaterials(e);
            e.Go.SetActive(true);
            e.Alive = true;
            AliveCount++;
            Active.Add(e);
            if (e.Radius > MaxGridRadius) big.Add(e);
            if (GameData.IsBoss(kind))
            {
                e.Boss = kind == EnemyKind.Colossus ? new ColossusBrain(e) : new BossBrain(e);
                Boss = e;
            }
            return e;
        }

        Enemy Create(EnemyKind kind, bool elite)
        {
            var def = GameData.Enemy(kind);
            var go = new GameObject(def.Name);
            go.transform.SetParent(root, false);
            var t = go.transform;
            float r = def.Radius;
            // Body keeps the type colour (elites glow brighter); gold is reserved for the elite's trim so types stay recognisable.
            var body = Art.Lit(def.Color * 0.3f, Art.Hdr(def.Color, elite ? 1.0f : 0.75f));
            var hot = Art.Unlit(Art.Hdr(elite ? Gold : Color.white, 1.4f));
            Transform spinner = null;

            switch (def.Shape)
            {
                case EnemyShape.Cube:
                    Art.Part("Body", t, Art.Cube, body, new Vector3(0, r * 0.85f, 0), Vector3.one * r * 1.7f);
                    Art.Part("Visor", t, Art.Cube, hot, new Vector3(0, r * 1.05f, r * 0.86f), new Vector3(r * 1.2f, r * 0.22f, r * 0.12f), false);
                    break;
                case EnemyShape.Spike:
                    Art.Part("Body", t, Art.Spike, body, new Vector3(0, 0.05f, 0), Vector3.one * r * 2.4f);
                    break;
                case EnemyShape.Octa:
                    spinner = Art.Part("Body", t, Art.Octa, body, new Vector3(0, r * 1.4f, 0), Vector3.one * r * 2.4f).transform;
                    Art.Part("Core", t, Art.Sphere, hot, new Vector3(0, r * 1.4f, 0), Vector3.one * r * 0.6f, false);
                    break;
                case EnemyShape.Cylinder:
                    Art.Part("Base", t, Art.Cylinder, body, new Vector3(0, r * 0.35f, 0), new Vector3(r * 2f, r * 0.35f, r * 2f));
                    spinner = Art.Part("Head", t, Art.Octa, body, new Vector3(0, r * 1.15f, 0), Vector3.one * r * 1.4f).transform;
                    Art.Part("Ring", t, Art.Cylinder, hot, new Vector3(0, r * 0.72f, 0), new Vector3(r * 2.05f, 0.03f, r * 2.05f), false);
                    break;
                case EnemyShape.Sphere:
                    Art.Part("Body", t, Art.Sphere, body, new Vector3(0, r, 0), Vector3.one * r * 2f);
                    Art.Part("Band", t, Art.Cylinder, hot, new Vector3(0, r, 0), new Vector3(r * 2.08f, 0.04f, r * 2.08f), false);
                    break;
                case EnemyShape.Colossus:
                    ColossusBrain.BuildRig(t, def);
                    break;
                case EnemyShape.Boss:
                    Art.Part("Core", t, Art.Octa, body, new Vector3(0, 2.6f, 0), new Vector3(3.6f, 4.4f, 3.6f));
                    Art.Part("Heart", t, Art.Sphere, hot, new Vector3(0, 2.6f, 0), Vector3.one * 1.1f, false);
                    spinner = new GameObject("Ring").transform;
                    spinner.SetParent(t, false);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI / 4f;
                        var part = Art.Part("Blade", spinner, Art.Cube, body, new Vector3(Mathf.Cos(a) * 3f, 1.1f, Mathf.Sin(a) * 3f), new Vector3(0.6f, 0.6f, 1.6f));
                        part.transform.localRotation = Quaternion.Euler(0, -a * Mathf.Rad2Deg, 45f);
                    }
                    break;
            }

            var e = new Enemy { Go = go, T = t, Spinner = spinner };
            e.Renderers = go.GetComponentsInChildren<MeshRenderer>();
            e.BaseMaterials = new Material[e.Renderers.Length];
            for (int i = 0; i < e.Renderers.Length; i++) e.BaseMaterials[i] = e.Renderers[i].sharedMaterial;
            go.SetActive(false);
            return e;
        }

        // ---------- Simulation ----------

        public void Tick(float dt)
        {
            var player = Game.I.Player;
            var ppos = player.Pos;
            float now = Game.Now;

            for (int i = 0; i < Active.Count; i++)
            {
                var e = Active[i];
                if (!e.Alive) continue;

                var toPlayer = ppos - e.Pos;
                toPlayer.y = 0;
                float dist = toPlayer.magnitude;
                var dir = dist > 0.001f ? toPlayer / dist : Vector3.forward;
                bool stunned = now < e.StunUntil;
                bool launched = now < e.LaunchedUntil;
                Vector3 desired = Vector3.zero;

                if (e.LaunchedUntil > 0 && !launched) { Detonate(e, false); continue; }
                if (e.Boss != null) desired = e.Boss.Tick(dt, dir, dist);
                else if (launched) { if (TickLaunched(e)) continue; }
                else if (!stunned && e.FuseAt < 0) desired = e.HasLunge ? TickLunge(e, dir, dist, dt, now) : Steering(e, dir, dist, now);

                // Separation from neighbours keeps hordes readable.
                var push = Vector3.zero;
                if (e.Radius <= MaxGridRadius)
                {
                    idx.Clear();
                    grid.Query(new Vector2(e.Pos.x, e.Pos.z), e.Radius + MaxGridRadius * 0.7f, idx);
                    int checkedCount = 0;
                    foreach (int j in idx)
                    {
                        var o = gridItems[j];
                        if (o == e || !o.Alive) continue;
                        var d = e.Pos - o.Pos; d.y = 0;
                        float min = e.Radius + o.Radius;
                        float sq = d.sqrMagnitude;
                        if (sq < min * min && sq > 1e-6f)
                        {
                            float m = Mathf.Sqrt(sq);
                            push += d / m * (min - m);
                        }
                        if (++checkedCount >= 10) break;
                    }
                }

                e.Pos += (desired + push * 5f + e.Knock) * dt;
                e.Pos.y = 0;
                if (!launched) e.Knock *= Mathf.Exp(-9f * dt);
                e.T.position = e.Pos;

                var face = e.Lunge == LungePhase.Windup ? e.LungeDir : desired.sqrMagnitude > 0.01f ? desired : dir;
                if (e.Kind != EnemyKind.Turret && !stunned)
                    e.T.rotation = Quaternion.Slerp(e.T.rotation, Quaternion.LookRotation(face), 1f - Mathf.Exp(-10f * dt));
                if (e.Spinner != null) e.Spinner.Rotate(0, (e.Boss != null ? 40f : 120f) * dt, 0, Space.World);

                // Contact damage
                if (!stunned && e.FuseAt < 0 && dist < e.Radius + PlayerRadius) player.TryHit(e.Lunge == LungePhase.Strike ? e.Damage * 1.8f : e.Damage);

                // Ranged attacks
                if (e.Def.FireInterval > 0 && e.Boss == null && !stunned && dist < 16f)
                {
                    e.FireTimer -= dt;
                    if (e.FireTimer <= 0)
                    {
                        e.FireTimer = e.Def.FireInterval * Random.Range(0.85f, 1.15f);
                        Shoot(e, dir);
                    }
                }

                // Bomber fuse
                if (e.FuseAt >= 0)
                {
                    float pulse = 1f + Mathf.Abs(Mathf.Sin(now * 30f)) * 0.25f;
                    e.T.localScale = Vector3.one * (e.Elite ? 1.6f : 1f) * pulse;
                    if (now >= e.FuseAt) { Detonate(e, true); continue; }
                }

                if (e.Flashing && now >= e.FlashUntil) RestoreMaterials(e);

                // Recycle stragglers in front of the player.
                if (dist > GameData.DespawnRadius && e.Boss == null)
                {
                    e.Pos = ppos + Quaternion.Euler(0, Random.Range(-50f, 50f), 0) * dir * GameData.SpawnRadius;
                    e.T.position = e.Pos;
                }
            }

            Compact();
            RebuildGrid();
        }

        Vector3 Steering(Enemy e, Vector3 dir, float dist, float now)
        {
            switch (e.Kind)
            {
                case EnemyKind.Gunner:
                case EnemyKind.Turret:
                    float range = e.Def.PreferredRange;
                    if (dist > range) return dir * e.Speed;
                    if (dist < range - 2.5f) return -dir * e.Speed * 0.6f;
                    return Vector3.Cross(Vector3.up, dir) * e.Strafe * e.Speed * 0.5f;
                case EnemyKind.Bomber:
                    if (dist < 2.1f)
                    {
                        e.FuseAt = now + 0.75f;
                        Game.I.Fx.Telegraph(e.Pos, BomberBlastRadius, 0.75f);
                        return Vector3.zero;
                    }
                    return dir * e.Speed;
                default:
                    return dir * e.Speed;
            }
        }

        /// <summary>Wind up (glowing red, telegraph on the ground), lunge, recover. The strike is the moment to parry.</summary>
        Vector3 TickLunge(Enemy e, Vector3 dir, float dist, float dt, float now)
        {
            switch (e.Lunge)
            {
                case LungePhase.Idle:
                    e.LungeCooldown -= dt;
                    if (e.LungeCooldown <= 0 && dist < LungeRange)
                    {
                        e.Lunge = LungePhase.Windup;
                        e.LungeTime = 0;
                        e.LungeDir = dir;
                        Game.I.Fx.LineTelegraph(e.Pos, dir, LungeSpeed * StrikeTime + e.Radius * 2f, e.Radius * 2f, WindupTime);
                        Sfx.Play(SfxId.Windup, 0.5f);
                        return Vector3.zero;
                    }
                    return Steering(e, dir, dist, now);
                case LungePhase.Windup:
                    e.LungeTime += dt;
                    bool lit = (int)(e.LungeTime * (e.LungeTime > WindupTime - 0.25f ? 30f : 14f)) % 2 == 0;
                    if (!e.Flashing) SetMaterials(e, lit ? WarningMaterial : null);
                    if (e.LungeTime >= WindupTime)
                    {
                        e.Lunge = LungePhase.Strike;
                        e.LungeTime = 0;
                        if (!e.Flashing) RestoreMaterials(e);
                    }
                    return -e.LungeDir * 0.4f; // slight lean back before the strike
                case LungePhase.Strike:
                    e.LungeTime += dt;
                    if (e.LungeTime >= StrikeTime) { e.Lunge = LungePhase.Recover; e.LungeTime = 0; }
                    return e.LungeDir * LungeSpeed;
                default:
                    e.LungeTime += dt;
                    if (e.LungeTime >= RecoverTime) { e.Lunge = LungePhase.Idle; e.LungeCooldown = Random.Range(2.2f, 3.4f); }
                    return dir * e.Speed * 0.3f;
            }
        }

        static Material WarningMaterial => warningMat ??= Art.Unlit(Art.Hdr(Warning, 1.8f));

        static void SetMaterials(Enemy e, Material m)
        {
            if (m == null) { RestoreMaterials(e); return; }
            foreach (var r in e.Renderers) r.sharedMaterial = m;
        }

        void CancelLunge(Enemy e, float cooldown)
        {
            if (e.Lunge == LungePhase.Windup && !e.Flashing) RestoreMaterials(e);
            e.Lunge = LungePhase.Idle;
            e.LungeCooldown = cooldown;
        }

        void Shoot(Enemy e, Vector3 dir)
        {
            var shots = Game.I.Projectiles;
            var origin = e.Pos + dir * e.Radius;
            switch (e.Kind)
            {
                case EnemyKind.Gunner:
                    int n = e.Elite ? 3 : 1;
                    for (int i = 0; i < n; i++)
                        shots.FireEnemy(origin, Quaternion.Euler(0, (i - (n - 1) * 0.5f) * 15f, 0) * dir, 7.5f, e.Damage, true);
                    break;
                case EnemyKind.Turret:
                    int ring = e.Elite ? 16 : 8;
                    float offset = Random.Range(0f, 360f);
                    for (int i = 0; i < ring; i++)
                        shots.FireEnemy(e.Pos, Quaternion.Euler(0, offset + i * 360f / ring, 0) * Vector3.forward, 5f, e.Damage, true);
                    break;
            }
            Sfx.Play(SfxId.EnemyShoot, 0.3f);
        }

        /// <summary>A parried bomber flies away and blows up on the first enemy it touches.</summary>
        bool TickLaunched(Enemy e)
        {
            idx.Clear();
            grid.Query(new Vector2(e.Pos.x, e.Pos.z), e.Radius + MaxGridRadius, idx);
            foreach (int j in idx)
            {
                var o = gridItems[j];
                if (o == e || !o.Alive) continue;
                if ((o.Pos - e.Pos).sqrMagnitude < (o.Radius + e.Radius) * (o.Radius + e.Radius))
                {
                    Detonate(e, false);
                    return true;
                }
            }
            foreach (var o in big)
            {
                var d = o.Pos - e.Pos; d.y = 0;
                if (o.Alive && d.magnitude < o.Radius + e.Radius) { Detonate(e, false); return true; }
            }
            if (Game.Now + Time.deltaTime >= e.LaunchedUntil)
            {
                Detonate(e, false);
                return true;
            }
            return false;
        }

        void Detonate(Enemy e, bool hurtsPlayer)
        {
            var pos = e.Pos;
            var player = Game.I.Player;
            if (hurtsPlayer && (player.Pos - pos).sqrMagnitude < (BomberBlastRadius + PlayerRadius) * (BomberBlastRadius + PlayerRadius))
                player.TryHit(e.Damage);

            float dmg = hurtsPlayer ? 30f : 60f * player.Stats.DamageMult;
            int source = hurtsPlayer ? ProjectileManager.SourceNova : ProjectileManager.SourceParry;
            scratch.Clear();
            Query(pos, BomberBlastRadius + MaxGridRadius, scratch);
            foreach (var o in scratch)
            {
                if (o == e || !o.Alive) continue;
                var d = o.Pos - pos; d.y = 0;
                if (d.magnitude <= BomberBlastRadius + o.Radius) Damage(o, dmg, d.normalized, 6f, source, false, hurtsPlayer ? 0f : 35f);
            }
            Game.I.Fx.Explosion(pos, e.Def.Color, 1.6f);
            Game.I.Fx.Ring(pos, e.Def.Color, 0.5f, BomberBlastRadius * 2f, 0.35f);
            Game.I.Rig.Shake(0.2f);
            Sfx.Play(SfxId.Explosion, 0.6f);
            Kill(e, false);
        }

        // ---------- Damage ----------

        /// <param name="armorDamage">Armor gauge damage for armored bosses; -1 derives it from the source.</param>
        public void Damage(Enemy e, float amount, Vector3 dir, float knockback, int source, bool canCrit = true, float armorDamage = -1f)
        {
            if (!e.Alive) return;
            var stats = Game.I.Player.Stats;
            bool crit = canCrit && Random.value < stats.Crit;
            if (crit) amount *= stats.CritMult;
            if (e.Boss != null) amount = e.Boss.ModifyDamage(amount, source, armorDamage);
            Game.I.Run.Damage[source] += Mathf.Min(amount, e.Hp);
            e.Hp -= amount;
            e.Knock += dir * (knockback * (1f - e.Def.KnockbackResist) * 1.5f);
            if (e.Boss == null || !e.Boss.IsFinal) Flash(e);   // a constantly-hit giant would just be white
            Game.I.UI.Hud.DamageNumber(e.Pos + Vector3.up * (e.Radius * 2f + 0.4f), amount, crit);
            if (e.Hp <= 0) Kill(e, true);
            else Sfx.Play(SfxId.Hit, 0.35f);
        }

        void Flash(Enemy e)
        {
            e.FlashUntil = Game.Now + 0.07f;
            if (e.Flashing) return;
            e.Flashing = true;
            foreach (var r in e.Renderers) r.sharedMaterial = Art.Flash;
        }

        static void RestoreMaterials(Enemy e)
        {
            e.Flashing = false;
            for (int i = 0; i < e.Renderers.Length; i++) e.Renderers[i].sharedMaterial = e.BaseMaterials[i];
        }

        public void Kill(Enemy e, bool effects)
        {
            if (!e.Alive) return;
            e.Alive = false;
            AliveCount--;
            e.Go.SetActive(false);
            dead.Add(e);
            Game.I.Run.Kills++;
            if (effects)
            {
                Game.I.Fx.Explosion(e.Pos, e.Elite ? Gold : e.Def.Color, Mathf.Clamp(e.Radius * 1.5f, 0.6f, 3f));
                Sfx.Play(SfxId.Kill, 0.5f);
                if (e.Elite) Game.I.Rig.Shake(0.3f);
            }
            Game.I.Pickups.DropFor(e);
            if (e.Boss != null)
            {
                bool final = e.Boss.IsFinal;
                e.Boss = null;
                if (Boss == e) Boss = null;
                Game.I.OnBossDefeated(e.Pos, final);
            }
        }

        /// <summary>
        /// Parry hit on every enemy inside the radius. Returns how many were parried this call.
        /// Enemies already within <paramref name="perfectRadius"/> count as PERFECT; parrying a lunge mid-strike BREAKs it.
        /// </summary>
        public int Parry(Vector3 center, float radius, float perfectRadius, int window, PlayerStats stats, out int perfect, out int breaks)
        {
            int count = 0;
            perfect = breaks = 0;
            float now = Game.Now;
            scratch.Clear();
            Query(center, radius + MaxGridRadius, scratch);
            foreach (var e in scratch)
            {
                if (!e.Alive || e.ParriedWindow == window) continue;
                var d = e.Pos - center; d.y = 0;
                float dist = d.magnitude;
                if (dist > radius + e.Radius) continue;
                e.ParriedWindow = window;
                var dir = d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.forward;
                bool isPerfect = dist <= perfectRadius + e.Radius;
                count++;
                if (isPerfect) perfect++;
                if (e.Boss != null)
                {
                    // Body-parrying a boss is a small chip only; its armor is broken by parrying its attacks.
                    Damage(e, (isPerfect ? 120f : 60f) * stats.DamageMult, dir, 0, ProjectileManager.SourceParry, false, 0f);
                    continue;
                }
                if (e.HasLunge && e.InStrike)
                {
                    breaks++;
                    Break(e, dir, stats);
                    continue;
                }
                CancelLunge(e, 2f);
                e.StunUntil = now + (isPerfect ? 1.5f : 1f);
                e.FuseAt = -1f;
                Damage(e, (isPerfect ? 30f : 15f) * stats.DamageMult, dir, 0, ProjectileManager.SourceParry, false);
                if (!e.Alive) continue;
                e.Knock = dir * (14f * (1f - e.Def.KnockbackResist * 0.7f));
                if (e.Kind == EnemyKind.Bomber)
                {
                    e.Knock = dir * 18f;
                    e.LaunchedUntil = now + 0.8f;
                    e.T.localScale = Vector3.one * (e.Elite ? 1.6f : 1f);
                }
            }
            return count;
        }

        /// <summary>Punishes a parried lunge: heavy damage, long stun, big knockback and a guaranteed reward drop.</summary>
        void Break(Enemy e, Vector3 dir, PlayerStats stats)
        {
            CancelLunge(e, 4f);
            e.StunUntil = Game.Now + 2.2f;
            float dmg = Mathf.Max(60f * stats.DamageMult, e.MaxHp * (e.Elite ? 0.25f : 0.6f));
            var pos = e.Pos;
            Damage(e, dmg, dir, 0, ProjectileManager.SourceParry, false);
            Game.I.Fx.Explosion(pos + Vector3.up * 0.6f, new Color(1f, 0.55f, 0.2f), 1.4f);
            for (int i = 0; i < 3; i++) Game.I.Pickups.Spawn(PickupType.Gold, pos + new Vector3(Random.Range(-1f, 1f), 0, Random.Range(-1f, 1f)), 2);
            if (Random.value < 0.25f) Game.I.Pickups.Spawn(PickupType.Heal, pos, 0);
            if (e.Alive) e.Knock = dir * (16f * (1f - e.Def.KnockbackResist * 0.5f));
        }

        // ---------- Queries ----------

        void Compact()
        {
            if (dead.Count == 0) return;
            Active.RemoveAll(IsDead);
            big.RemoveAll(IsDead);
            foreach (var e in dead) pools[(e.Kind, e.Elite)].Push(e);
            dead.Clear();
        }

        static readonly System.Predicate<Enemy> IsDead = e => !e.Alive;

        void RebuildGrid()
        {
            grid.Clear();
            gridItems.Clear();
            foreach (var e in Active)
            {
                if (!e.Alive || e.Radius > MaxGridRadius) continue;
                grid.Add(new Vector2(e.Pos.x, e.Pos.z));
                gridItems.Add(e);
            }
        }

        /// <summary>Alive enemies whose center is within radius (large enemies: whose body overlaps).</summary>
        public void Query(Vector3 pos, float radius, List<Enemy> results)
        {
            idx.Clear();
            grid.Query(new Vector2(pos.x, pos.z), radius, idx);
            foreach (int j in idx) if (gridItems[j].Alive) results.Add(gridItems[j]);
            foreach (var e in big)
            {
                var d = e.Pos - pos; d.y = 0;
                if (e.Alive && d.magnitude <= radius + e.Radius) results.Add(e);
            }
        }

        /// <summary>Candidates for a circle of the given radius to touch.</summary>
        public void QueryForCollision(Vector3 pos, float radius, List<Enemy> results) => Query(pos, radius + MaxGridRadius, results);

        public Enemy Nearest(Vector3 pos, float maxDist, Enemy exclude = null)
        {
            scratch.Clear();
            Query(pos, maxDist, scratch);
            Enemy best = null;
            float bestSq = float.MaxValue;
            foreach (var e in scratch)
            {
                if (e == exclude) continue;
                var d = e.Pos - pos; d.y = 0;
                float sq = d.sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = e; }
            }
            return best;
        }

        public Enemy NearestExcluding(Vector3 pos, float maxDist, List<int> excludedUids)
        {
            scratch.Clear();
            Query(pos, maxDist, scratch);
            Enemy best = null;
            float bestSq = float.MaxValue;
            foreach (var e in scratch)
            {
                if (excludedUids.Contains(e.Uid)) continue;
                var d = e.Pos - pos; d.y = 0;
                float sq = d.sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = e; }
            }
            return best;
        }

        public void KillAllInRadius(Vector3 pos, float radius, bool includeBoss, float bossDamage)
        {
            scratch.Clear();
            Query(pos, radius, scratch);
            foreach (var e in scratch)
            {
                if (e.Boss != null) { if (includeBoss) Damage(e, bossDamage, Vector3.zero, 0, ProjectileManager.SourceNova, false); continue; }
                Damage(e, e.Hp + 1f, (e.Pos - pos).normalized, 4f, ProjectileManager.SourceNova, false);
            }
        }

        /// <summary>Pushes enemies out of a radius and briefly stuns them (no damage).</summary>
        public void Repel(Vector3 center, float radius, float force, float stun)
        {
            scratch.Clear();
            Query(center, radius, scratch);
            foreach (var e in scratch)
            {
                if (e.Boss != null) continue;
                var d = e.Pos - center; d.y = 0;
                e.Knock = (d.sqrMagnitude > 1e-4f ? d.normalized : Vector3.forward) * force * (1f - e.Def.KnockbackResist * 0.6f);
                e.StunUntil = Mathf.Max(e.StunUntil, Game.Now + stun);
                CancelLunge(e, 1.5f);
            }
        }

        public void DamageInRadius(Vector3 pos, float radius, float damage, int source)
        {
            scratch.Clear();
            Query(pos, radius, scratch);
            foreach (var e in scratch)
            {
                var d = e.Pos - pos; d.y = 0;
                Damage(e, damage, d.normalized, 6f, source, false);
            }
        }

        /// <summary>Removes an enemy without drops, kill credit or boss callbacks (e.g. a boss leaving the arena).</summary>
        public void Dismiss(Enemy e)
        {
            if (!e.Alive) return;
            Game.I.Fx.Explosion(e.Pos + Vector3.up * 2f, e.Def.Color, 2.5f);
            e.Alive = false;
            AliveCount--;
            e.Go.SetActive(false);
            dead.Add(e);
            e.Boss = null;
            if (Boss == e) Boss = null;
        }

        public void Clear()
        {
            foreach (var e in Active)
            {
                if (e.Alive) { e.Alive = false; e.Go.SetActive(false); dead.Add(e); }
                e.Boss = null;
            }
            Compact();
            Active.Clear();
            big.Clear();
            Boss = null;
            AliveCount = 0;
            RebuildGrid();
        }
    }

    /// <summary>WARDEN (mid-boss): bullet spirals, aimed bursts with unparryable shots, telegraphed charges, summons.</summary>
    public sealed class BossBrain : BossAI
    {
        enum Mode { Walk, Spiral, Burst, Windup, Charge }

        Mode mode = Mode.Walk;
        float modeTime, attackTimer = 2.5f, summonTimer = 8f, shotTimer, spiralAngle;
        int volleys, attackIndex;
        Vector3 chargeDir;
        static readonly Vector3[] line = new Vector3[2];

        public BossBrain(Enemy enemy) : base(enemy) { }

        public override string Name => "WARDEN";

        public override Vector3 Tick(float dt, Vector3 dir, float dist)
        {
            var shots = Game.I.Projectiles;
            int phase = Phase;
            float speedUp = phase == 3 ? 1.35f : 1f;
            modeTime += dt * speedUp;

            if (phase >= 2)
            {
                summonTimer -= dt;
                if (summonTimer <= 0)
                {
                    summonTimer = phase == 3 ? 7f : 10f;
                    for (int i = 0; i < 6; i++)
                        Game.I.Enemies.Spawn(i % 2 == 0 ? EnemyKind.Crawler : EnemyKind.Bomber, e.Pos + Quaternion.Euler(0, i * 60f, 0) * Vector3.forward * 3.5f);
                }
            }

            switch (mode)
            {
                case Mode.Walk:
                    attackTimer -= dt * speedUp;
                    if (attackTimer <= 0) StartAttack(phase, dir);
                    if (dist > e.Def.PreferredRange) return dir * e.Speed;
                    return Vector3.Cross(Vector3.up, dir) * e.Speed * 0.6f;

                case Mode.Spiral:
                    shotTimer -= dt * speedUp;
                    if (shotTimer <= 0)
                    {
                        shotTimer = 0.07f;
                        int arms = phase + 1;
                        for (int a = 0; a < arms; a++)
                        {
                            var d = Quaternion.Euler(0, spiralAngle + a * 360f / arms, 0) * Vector3.forward;
                            bool heavy = phase == 3 && a == 0;
                            shots.FireEnemy(e.Pos + d * 2.2f, d, 6f, e.Damage * 0.6f, !heavy);
                        }
                        spiralAngle += 13f;
                        Sfx.Play(SfxId.EnemyShoot, 0.25f);
                    }
                    if (modeTime > 2.2f) EndAttack();
                    return Vector3.zero;

                case Mode.Burst:
                    shotTimer -= dt * speedUp;
                    if (shotTimer <= 0 && volleys > 0)
                    {
                        shotTimer = 0.4f;
                        volleys--;
                        int n = phase == 1 ? 5 : 7;
                        for (int i = 0; i < n; i++)
                        {
                            var d = Quaternion.Euler(0, (i - (n - 1) * 0.5f) * 12f, 0) * dir;
                            shots.FireEnemy(e.Pos + d * 2.2f, d, 9f, e.Damage * 0.7f, i != n / 2);
                        }
                        Sfx.Play(SfxId.EnemyShoot, 0.5f, 0.7f);
                    }
                    if (volleys <= 0 && shotTimer <= 0) EndAttack();
                    return dir * e.Speed * 0.3f;

                case Mode.Windup:
                    if (modeTime > 0.9f)
                    {
                        mode = Mode.Charge;
                        modeTime = 0;
                        Game.I.Rig.Shake(0.3f);
                    }
                    return Vector3.zero;

                case Mode.Charge:
                    if (modeTime > 0.55f)
                    {
                        int ring = 24;
                        for (int i = 0; i < ring; i++)
                            shots.FireEnemy(e.Pos, Quaternion.Euler(0, i * 360f / ring, 0) * Vector3.forward, 6.5f, e.Damage * 0.6f, true);
                        Game.I.Fx.Ring(e.Pos, e.Def.Color, 1f, 8f, 0.4f);
                        Game.I.Rig.Shake(0.4f);
                        Sfx.Play(SfxId.Explosion, 0.8f, 0.6f);
                        EndAttack();
                        return Vector3.zero;
                    }
                    return chargeDir * 22f;
            }
            return Vector3.zero;
        }

        void StartAttack(int phase, Vector3 dir)
        {
            modeTime = 0;
            int options = phase == 1 ? 2 : 3;
            switch (attackIndex++ % options)
            {
                case 0:
                    mode = Mode.Spiral;
                    shotTimer = 0;
                    break;
                case 1:
                    mode = Mode.Burst;
                    volleys = phase == 3 ? 4 : 3;
                    shotTimer = 0.2f;
                    break;
                default:
                    mode = Mode.Windup;
                    chargeDir = dir;
                    line[0] = e.Pos + Vector3.up * 0.1f;
                    line[1] = e.Pos + dir * 13f + Vector3.up * 0.1f;
                    Game.I.Fx.Beam(line, 2, new Color(1f, 0.15f, 0.2f), e.Radius * 2f, 0.9f);
                    Sfx.Play(SfxId.Warning, 0.4f, 1.5f);
                    break;
            }
        }

        void EndAttack()
        {
            mode = Mode.Walk;
            modeTime = 0;
            attackTimer = Phase == 3 ? 1.2f : 2f;
        }
    }
}
