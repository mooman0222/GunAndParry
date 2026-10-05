using UnityEngine;

namespace GunAndParry
{
    /// <summary>Behaviour shared by bosses (WARDEN mid-boss, COLOSSUS final boss).</summary>
    public abstract class BossAI
    {
        protected readonly Enemy e;
        protected BossAI(Enemy enemy) { e = enemy; }

        public abstract string Name { get; }
        /// <summary>Killing the final boss wins the run.</summary>
        public virtual bool IsFinal => false;
        public int Phase => e.Hp > e.MaxHp * 0.66f ? 1 : e.Hp > e.MaxHp * 0.33f ? 2 : 3;
        /// <summary>Armor gauge 0..1, or -1 when the boss has none.</summary>
        public virtual float Armor01 => -1f;
        public virtual bool Broken => false;

        /// <summary>Returns the desired velocity for this frame.</summary>
        public abstract Vector3 Tick(float dt, Vector3 dir, float dist);

        /// <summary>Lets a boss scale incoming damage and react to its source.</summary>
        public virtual float ModifyDamage(float amount, int source, float armorDamage) => amount;

        public static bool IsParrySource(int source) => source == ProjectileManager.SourceParry || source == ProjectileManager.SourcePerfect;
    }

    /// <summary>
    /// COLOSSUS — final boss built around parrying. Its armor turns away 90% of gun damage.
    /// Parried attacks (reflected orbs, perfect-parried gold shots, countered hammer slams, bombers launched into it)
    /// wear the armor gauge down; at zero it BREAKs: it collapses, its core opens, and every weapon hits for 2.5x.
    /// </summary>
    public sealed class ColossusBrain : BossAI
    {
        // ArmorMax tuned so a fully parried barrage alone can't break it: a break takes several parried attacks (~25s of good play).
        public const float ArmorMax = 250f, ArmoredMult = 0.1f, BrokenMult = 2.5f, BreakDuration = 6.5f;
        const float SlamRadius = 3.2f, SweepLength = 17f, SweepWidth = 0.7f;
        const float SmashDuration = 0.12f, RecoverDuration = 0.7f;
        /// <summary>Hammer reaches the bottom (-150 -> 0 of the -150 -> +55 smash) this far into SlamRecover.</summary>
        const float SmashImpactAt = SmashDuration * 150f / 205f;

        enum Mode { Walk, Barrage, SlamWindup, SlamRecover, SweepAim, Sweep, GoldVolley, Summon, Broken }

        public override string Name => "COLOSSUS";
        public override bool IsFinal => true;
        public override float Armor01 => armor / ArmorMax;
        public override bool Broken => mode == Mode.Broken;
        /// <summary>Seconds until the current hammer slam lands (or -1). Used by the dev autopilot.</summary>
        public float SlamImpactIn => mode == Mode.SlamWindup ? Mathf.Max(0, windup - modeTime) + SmashImpactAt
            : mode == Mode.SlamRecover && !slamImpactDone ? Mathf.Max(0, SmashImpactAt - modeTime) : -1f;
        public Vector3 SlamTarget { get; private set; }

        float armor = ArmorMax, lastArmorHit, modeTime, shotTimer, sweepAngle, sweepFrom, windup;
        bool slamImpactDone;
        float recoverExtra;
        int step, volleys, slamArm, slamsLeft;
        Mode mode = Mode.Walk;
        float attackDelay = 2.5f;
        readonly Transform body, armL, armR, head, core, plates, laserT;
        readonly LineRenderer laser;
        readonly Material coreMat, eyeMat;
        static readonly Color Orange = new(1f, 0.42f, 0.17f);

        public ColossusBrain(Enemy enemy) : base(enemy)
        {
            var t = e.T;
            body = t.Find("Body");
            armL = t.Find("Body/ArmL");
            armR = t.Find("Body/ArmR");
            head = t.Find("Body/Head");
            core = t.Find("Body/Core");
            plates = t.Find("Body/Plates");
            laserT = t.Find("Body/Head/Laser");
            laser = laserT.GetComponent<LineRenderer>();
            laser.enabled = false;
            coreMat = core.GetComponent<MeshRenderer>().sharedMaterial;
            eyeMat = head.Find("Eye").GetComponent<MeshRenderer>().sharedMaterial;
            plates.gameObject.SetActive(true);
            lastArmorHit = Game.Now;
        }

        // ---------------- Damage / armor ----------------

        /// <summary>Damage actually dealt to the boss per source, split by armored [0] / broken [1] (balancing telemetry).</summary>
        public readonly float[,] DamageLog = new float[ProjectileManager.SourceCount, 2];

        public override float ModifyDamage(float amount, int source, float armorDamage)
        {
            if (mode == Mode.Broken)
            {
                DamageLog[source, 1] += amount * BrokenMult;
                return amount * BrokenMult;
            }
            bool parry = IsParrySource(source);
            if (armorDamage < 0) armorDamage = source == ProjectileManager.SourcePerfect ? 12f : source == ProjectileManager.SourceParry ? 6f : 0f;
            if (armorDamage > 0) DamageArmor(armorDamage);
            float dealt = parry ? amount : amount * ArmoredMult;
            DamageLog[source, mode == Mode.Broken ? 1 : 0] += dealt;
            return dealt;
        }

        void DamageArmor(float amount)
        {
            armor -= amount;
            lastArmorHit = Game.Now;
            Game.I.Fx.Sparks(e.Pos + Vector3.up * 4f, Orange, 10, 12f);
            if (armor <= 0) StartBreak();
        }

        public int BreakCount { get; private set; }

        void StartBreak()
        {
            BreakCount++;
            armor = 0;
            mode = Mode.Broken;
            modeTime = 0;
            laser.enabled = false;
            plates.gameObject.SetActive(false);
            var g = Game.I;
            g.Fx.Explosion(e.Pos + Vector3.up * 4f, Orange, 3.5f);
            g.Fx.Ring(e.Pos, Color.white, 2f, 16f, 0.5f);
            g.Rig.Shake(0.8f);
            g.SlowMo(0.6f);
            g.Projectiles.ClearHostile();
            g.UI.Hud.Banner("ARMOR BROKEN!  OPEN FIRE!", ProjectileManager.PerfectColor, 2.2f);
            Sfx.Play(SfxId.Break, 1f, 0.7f);
            Sfx.Play(SfxId.Explosion, 0.8f, 0.6f);
        }

        void EndBreak()
        {
            armor = ArmorMax;
            lastArmorHit = Game.Now;
            plates.gameObject.SetActive(true);
            mode = Mode.Walk;
            attackDelay = 1.2f;
            // Rising shockwave of orbs: an immediate chance to start breaking it again.
            Ring(18, 6f, true, false);
            Game.I.Fx.Ring(e.Pos, Orange, 2f, 14f, 0.4f);
            Game.I.UI.Hud.Banner("ARMOR RESTORED", Orange, 1.5f);
            Sfx.Play(SfxId.Roar, 0.9f);
        }

        /// <summary>Called when the player's parry window overlaps a hammer impact.</summary>
        void CounterSlam(bool perfect)
        {
            var g = Game.I;
            g.Player.RegisterParry(0, 1, perfect ? 1 : 0, 1);
            DamageArmor(perfect ? 70f : 50f);
            g.Fx.Ring(SlamTarget, ProjectileManager.PerfectColor, 1f, SlamRadius * 2.4f, 0.35f);
            if (mode != Mode.Broken) recoverExtra = 0.7f; // staggered: longer recovery, arm resting low
        }

        // ---------------- Behaviour ----------------

        public override Vector3 Tick(float dt, Vector3 dir, float dist)
        {
            float now = Game.Now;
            int phase = Phase;
            float haste = phase == 3 ? 1.3f : phase == 2 ? 1.12f : 1f;
            modeTime += dt * (mode == Mode.Broken ? 1f : haste);

            // Armor slowly regrows if the player stops parrying.
            if (mode != Mode.Broken && now - lastArmorHit > 6f) armor = Mathf.Min(ArmorMax, armor + 10f * dt);

            Animate(dt, now);
            var player = Game.I.Player;

            switch (mode)
            {
                case Mode.Walk:
                    attackDelay -= dt * haste;
                    if (attackDelay <= 0) NextAttack(phase, player);
                    return dist > e.Def.PreferredRange ? dir * e.Speed : Vector3.Cross(Vector3.up, dir) * e.Speed * 0.4f;

                case Mode.Barrage:
                    shotTimer -= dt * haste;
                    if (shotTimer <= 0 && volleys > 0)
                    {
                        shotTimer = 0.5f;
                        volleys--;
                        int n = phase == 1 ? 5 : 7;
                        foreach (int side in new[] { -1, 1 })
                        {
                            var origin = Cannon(side);
                            var aim = player.Pos - origin; aim.y = 0; aim.Normalize();
                            for (int i = 0; i < n; i++)
                                Game.I.Projectiles.FireEnemy(origin, Quaternion.Euler(0, (i - (n - 1) * 0.5f) * 9f, 0) * aim, 6.5f, e.Damage * 0.5f, true);
                        }
                        Sfx.Play(SfxId.EnemyShoot, 0.6f, 0.7f);
                    }
                    if (volleys <= 0 && shotTimer <= 0) EndAttack();
                    return Vector3.zero;

                case Mode.SlamWindup:
                    if (modeTime >= windup)
                    {
                        // The hammer is still overhead here: the smash animates in SlamRecover
                        // and the impact lands when it reaches the bottom (see SmashImpactAt).
                        mode = Mode.SlamRecover;
                        modeTime = 0;
                        slamImpactDone = false;
                    }
                    return Vector3.zero;

                case Mode.SlamRecover:
                    if (!slamImpactDone && modeTime >= SmashImpactAt)
                    {
                        slamImpactDone = true;
                        SlamImpact(player, phase);
                    }
                    if (modeTime >= RecoverDuration + recoverExtra)
                    {
                        if (slamsLeft > 0) StartSlam(phase, player);
                        else EndAttack();
                    }
                    return Vector3.zero;

                case Mode.SweepAim:
                    UpdateLaser(sweepFrom, 0.08f, new Color(1f, 0.2f, 0.15f));
                    if (modeTime >= 0.9f) { mode = Mode.Sweep; modeTime = 0; Sfx.Play(SfxId.Laser, 0.8f); }
                    return Vector3.zero;

                case Mode.Sweep:
                {
                    sweepAngle = sweepFrom + 200f * Mathf.Clamp01(modeTime / 2.2f) * (step % 2 == 0 ? 1 : -1);
                    var (a, b) = UpdateLaser(sweepAngle, SweepWidth, new Color(1f, 0.25f, 0.2f));
                    var pp = new Vector2(player.Pos.x, player.Pos.z);
                    if (Geo.DistanceToSegment(pp, new Vector2(a.x, a.z), new Vector2(b.x, b.z)) < SweepWidth * 0.5f + Player.Radius)
                        player.TryHit(e.Damage * 0.8f, true);   // unparryable: dash through it
                    if (modeTime >= 2.2f) { laser.enabled = false; EndAttack(); }
                    return Vector3.zero;
                }

                case Mode.GoldVolley:
                    shotTimer -= dt * haste;
                    if (shotTimer <= 0 && volleys > 0)
                    {
                        shotTimer = 0.55f;
                        volleys--;
                        var origin = e.Pos + Vector3.up * 1f;
                        var aim = player.Pos - origin; aim.y = 0; aim.Normalize();
                        for (int i = 0; i < 3; i++)
                            Game.I.Projectiles.FireEnemy(origin + aim * 3f, Quaternion.Euler(0, (i - 1) * 14f, 0) * aim, 5.5f, e.Damage * 0.7f, true, true);
                        Sfx.Play(SfxId.Perfect, 0.35f, 0.6f, 0f);
                    }
                    if (volleys <= 0 && shotTimer <= 0) EndAttack();
                    return Vector3.zero;

                case Mode.Summon:
                    if (modeTime >= 0.8f)
                    {
                        int n = phase == 3 ? 6 : 4;
                        for (int i = 0; i < n; i++)
                        {
                            var kind = i % 2 == 0 || phase == 1 ? EnemyKind.Bomber : EnemyKind.Crawler;
                            Game.I.Enemies.Spawn(kind, e.Pos + Quaternion.Euler(0, i * 360f / n + 30f, 0) * Vector3.forward * 5f);
                        }
                        Game.I.Fx.Ring(e.Pos, Orange, 2f, 12f, 0.4f);
                        EndAttack();
                    }
                    return Vector3.zero;

                case Mode.Broken:
                    if (modeTime >= BreakDuration) EndBreak();
                    return Vector3.zero;
            }
            return Vector3.zero;
        }

        void NextAttack(int phase, Player player)
        {
            modeTime = 0;
            Mode[] rotation = phase switch
            {
                1 => new[] { Mode.Barrage, Mode.SlamWindup, Mode.Summon, Mode.Barrage, Mode.SlamWindup },
                2 => new[] { Mode.SlamWindup, Mode.Barrage, Mode.SweepAim, Mode.GoldVolley, Mode.SlamWindup, Mode.Summon },
                _ => new[] { Mode.SlamWindup, Mode.GoldVolley, Mode.SweepAim, Mode.Barrage, Mode.SlamWindup, Mode.Summon },
            };
            var next = rotation[step++ % rotation.Length];
            switch (next)
            {
                case Mode.Barrage: mode = next; volleys = phase == 1 ? 3 : 4; shotTimer = 0.3f; break;
                case Mode.GoldVolley: mode = next; volleys = phase == 3 ? 4 : 3; shotTimer = 0.4f; Game.I.UI.Hud.Banner("GOLD SHOTS: PERFECT PARRY ONLY", ProjectileManager.PerfectColor, 1.4f); break;
                case Mode.SlamWindup: slamsLeft = phase == 3 ? 2 : phase == 2 ? 1 : 0; StartSlam(phase, player); break;
                case Mode.SweepAim:
                    mode = next;
                    var d = player.Pos - e.Pos;
                    sweepFrom = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg - (step % 2 == 0 ? 100f : -100f);
                    Sfx.Play(SfxId.Warning, 0.5f, 1.4f);
                    break;
                case Mode.Summon: mode = next; Sfx.Play(SfxId.Roar, 0.6f, 1.2f); break;
            }
        }

        /// <summary>Hammer reaches the bottom of its swing: shockwave, damage, and the parry counter window.</summary>
        void SlamImpact(Player player, int phase)
        {
            // Impact. A parry whose window covers this instant counters it.
            var g = Game.I;
            var d = player.Pos - SlamTarget; d.y = 0;
            bool inArea = d.magnitude <= SlamRadius + Player.Radius;
            if (inArea && player.IsParrying) CounterSlam(player.ParryAge <= 0.12f);
            else if (inArea) player.TryHit(e.Damage * 1.6f);
            g.Fx.Explosion(SlamTarget, Orange, 2.2f);
            g.Fx.Ring(SlamTarget, Orange, 0.5f, SlamRadius * 2.2f, 0.3f);
            g.Rig.Shake(0.6f);
            Sfx.Play(SfxId.Slam, 0.9f);
            if (phase >= 2 && mode != Mode.Broken) Ring(12, 5.5f, true, false, SlamTarget);
        }

        void StartSlam(int phase, Player player)
        {
            if (mode == Mode.SlamRecover) slamsLeft--;
            mode = Mode.SlamWindup;
            modeTime = 0;
            recoverExtra = 0;
            slamImpactDone = false;
            windup = phase == 3 ? 0.8f : 1.0f;
            slamArm = 1 - slamArm;
            SlamTarget = new Vector3(player.Pos.x, 0, player.Pos.z);
            Game.I.Fx.Telegraph(SlamTarget, SlamRadius, windup + SmashImpactAt);
            Sfx.Play(SfxId.Windup, 0.7f, 0.7f);
        }

        void EndAttack()
        {
            mode = Mode.Walk;
            modeTime = 0;
            attackDelay = Phase == 3 ? 0.9f : 1.6f;
        }

        Vector3 Cannon(int side) => body.TransformPoint(new Vector3(side * 2.6f, 5.2f, 1.2f));

        void Ring(int count, float speed, bool parryable, bool perfectOnly, Vector3? at = null)
        {
            var c = at ?? e.Pos;
            float offset = Random.value * 360f;
            for (int i = 0; i < count; i++)
                Game.I.Projectiles.FireEnemy(c, Quaternion.Euler(0, offset + i * 360f / count, 0) * Vector3.forward, speed, e.Damage * 0.5f, parryable, perfectOnly);
        }

        (Vector3 a, Vector3 b) UpdateLaser(float angleDeg, float width, Color color)
        {
            laser.enabled = true;
            var a = laserT.position;
            var dir = Quaternion.Euler(0, angleDeg, 0) * Vector3.forward;
            var b = new Vector3(e.Pos.x, 0.3f, e.Pos.z) + dir * SweepLength;
            laser.SetPosition(0, a);
            laser.SetPosition(1, b);
            laser.startWidth = width * 0.6f;
            laser.endWidth = width;
            var c = Art.Hdr(color, 2.2f);
            laser.startColor = laser.endColor = c;
            return (new Vector3(e.Pos.x, 0, e.Pos.z), b);
        }

        // ---------------- Procedural animation ----------------

        void Animate(float dt, float now)
        {
            float k = 1f - Mathf.Exp(-10f * dt);
            // Arms: raise during wind-up, smash on impact, settle.
            // The smash is explicitly animated (-150 overhead through 0 straight-down to +55 follow-through):
            // snapping or Slerp-smoothing the -150 -> +55 jump would teleport (or swing the wrong way around).
            // The impact itself lands at SmashImpactAt, when the hammer reaches the bottom (see SlamImpact).
            float raise = 0f, other = 0f;
            bool smashing = false;
            if (mode == Mode.SlamWindup) raise = Mathf.SmoothStep(0, -150f, Mathf.Clamp01(modeTime / windup));
            else if (mode == Mode.SlamRecover)
            {
                if (modeTime < SmashDuration)
                {
                    raise = Mathf.Lerp(-150f, 55f, Mathf.Clamp01(modeTime / SmashDuration));
                    smashing = true;
                }
                else raise = Mathf.Lerp(55f, 0f, Mathf.Clamp01((modeTime - SmashDuration) / (RecoverDuration - SmashDuration)));
            }
            if (mode == Mode.Barrage || mode == Mode.GoldVolley) other = -40f;
            var slam = slamArm == 0 ? armL : armR;
            var idle = slamArm == 0 ? armR : armL;
            var sway = Mathf.Sin(now * 1.3f) * 3f;
            if (smashing) slam.localRotation = Quaternion.Euler(raise + sway, 0, 0);
            else slam.localRotation = Quaternion.Slerp(slam.localRotation, Quaternion.Euler(raise + sway, 0, 0), k);
            idle.localRotation = Quaternion.Slerp(idle.localRotation, Quaternion.Euler(other + Mathf.Sin(now * 1.3f + 1f) * 3f, 0, 0), k);

            // Broken: kneel and tilt; otherwise hover and breathe.
            float broken = mode == Mode.Broken ? 1f : 0f;
            body.localPosition = Vector3.Lerp(body.localPosition, new Vector3(0, Mathf.Sin(now * 1.6f) * 0.15f - broken * 1.4f, 0), k);
            body.localRotation = Quaternion.Slerp(body.localRotation, Quaternion.Euler(broken * 18f, 0, broken * 6f), k * 0.6f);

            var player = Game.I.Player.Pos;
            var look = Quaternion.LookRotation(e.T.InverseTransformDirection((player - head.position).normalized));
            head.localRotation = Quaternion.Slerp(head.localRotation, look, k);

            float pulse = 0.8f + Mathf.Sin(now * (mode == Mode.Broken ? 12f : 3f)) * 0.2f;
            coreMat.SetColor("_BaseColor", Art.Hdr(mode == Mode.Broken ? ProjectileManager.PerfectColor : Orange, (mode == Mode.Broken ? 2.8f : 1.2f) * pulse));
            bool charging = mode == Mode.SweepAim || mode == Mode.Sweep;
            eyeMat.SetColor("_BaseColor", Art.Hdr(charging ? new Color(1f, 0.2f, 0.15f) : Orange, charging ? 3f : 2f));
        }

        // ---------------- Model ----------------

        /// <summary>Builds the ~8m hovering war machine: base, torso, armored plates over a core, head with eye, cannons, hammer arms.</summary>
        public static void BuildRig(Transform root, EnemyDef def)
        {
            // Readability on a dark floor: light gunmetal hull, armor plates that clearly glow orange (the thing you break).
            // Faint self-illumination: the camera mostly sees the boss's unlit side, and pure lit gray reads as black there.
            var dark = Art.Lit(new Color(0.26f, 0.26f, 0.3f), new Color(0.06f, 0.05f, 0.06f));
            var hull = Art.Lit(new Color(0.58f, 0.57f, 0.62f), new Color(0.2f, 0.17f, 0.18f));
            var plate = Art.Lit(new Color(0.55f, 0.24f, 0.1f), Art.Hdr(Orange, 1.4f));
            var glow = Art.Unlit(Art.Hdr(Orange, 1.4f));
            var box = Art.Prism(4, 0.7071f, 0.7071f, 45f);
            var hex = Art.Prism(6, 0.5f, 0.5f);
            var taper = Art.Prism(6, 0.4f, 0.5f, 30f);
            var cyl = Art.Prism(10, 0.5f, 0.5f);

            var body = new GameObject("Body").transform;
            body.SetParent(root, false);
            body.localScale = Vector3.one * 1.25f;   // ~9m tall

            var lightGo = new GameObject("BossLight");
            lightGo.transform.SetParent(body, false);
            lightGo.transform.localPosition = new Vector3(0, 6f, 3f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = Orange;
            light.range = 14f;
            light.intensity = 3f;
            light.shadows = LightShadows.None;

            // Hover base
            Art.Part("Base", body, Art.Prism(8, 0.5f, 0.42f, 22.5f), dark, new Vector3(0, 0.9f, 0), new Vector3(5.6f, 1.0f, 5.6f));
            Art.Part("BaseGlow", body, cyl, glow, new Vector3(0, 0.38f, 0), new Vector3(4.6f, 0.06f, 4.6f), false);
            Art.Part("Waist", body, hex, dark, new Vector3(0, 1.9f, 0), new Vector3(2.4f, 1.2f, 2.2f));
            // Torso
            Art.Part("Torso", body, taper, hull, new Vector3(0, 3.9f, 0), new Vector3(4.6f, 3.0f, 3.2f));
            var coreGo = Art.Part("Core", body, Art.Sphere, new Material(glow), new Vector3(0, 4.0f, 1.25f), Vector3.one * 1.3f, false);
            coreGo.name = "Core";
            var plates = new GameObject("Plates").transform;
            plates.SetParent(body, false);
            foreach (int side in new[] { -1, 1 })
            {
                var p = Art.Part("ChestPlate", plates, box, plate, new Vector3(side * 0.62f, 4.1f, 1.45f), new Vector3(1.25f, 1.9f, 0.35f));
                p.transform.localRotation = Quaternion.Euler(0, side * -12f, 0);
                Art.Part("PlateEdge", plates, box, glow, new Vector3(side * 0.62f, 3.12f, 1.55f), new Vector3(1.1f, 0.06f, 0.12f), false);
                var sp = Art.Part("ShoulderPlate", plates, taper, plate, new Vector3(side * 2.55f, 5.55f, 0), new Vector3(1.6f, 0.6f, 1.8f));
                sp.transform.localRotation = Quaternion.Euler(0, 0, side * -14f);
            }
            // Shoulder cannons
            foreach (int side in new[] { -1, 1 })
            {
                var c = Art.Part("Cannon", body, cyl, dark, new Vector3(side * 2.6f, 5.2f, 0.4f), new Vector3(0.55f, 1.4f, 0.55f));
                c.transform.localRotation = Quaternion.Euler(90, 0, 0);
                Art.Part("CannonMuzzle", body, cyl, glow, new Vector3(side * 2.6f, 5.2f, 1.12f), new Vector3(0.4f, 0.05f, 0.4f), false).transform.localRotation = Quaternion.Euler(90, 0, 0);
            }
            // Head
            var head = new GameObject("Head").transform;
            head.SetParent(body, false);
            head.localPosition = new Vector3(0, 6.0f, 0.3f);
            Art.Part("Skull", head, Art.Prism(6, 0.5f, 0.38f, 30f), hull, new Vector3(0, 0.45f, 0), new Vector3(1.5f, 0.9f, 1.5f));
            var eye = Art.Part("Eye", head, Art.Sphere, new Material(glow), new Vector3(0, 0.45f, 0.62f), new Vector3(0.85f, 0.45f, 0.35f), false);
            eye.name = "Eye";
            Art.Part("Brow", head, box, plate, new Vector3(0, 0.78f, 0.55f), new Vector3(1.3f, 0.14f, 0.3f));
            var laserGo = new GameObject("Laser");
            laserGo.transform.SetParent(head, false);
            laserGo.transform.localPosition = new Vector3(0, 0.45f, 0.75f);
            var lr = laserGo.AddComponent<LineRenderer>();
            lr.sharedMaterial = Art.Trail;
            lr.positionCount = 2;
            lr.useWorldSpace = true;
            lr.numCapVertices = 4;
            lr.alignment = LineAlignment.View;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.enabled = false;
            // Hammer arms (pivot at the shoulder; rotate around X to raise/slam).
            foreach (int side in new[] { -1, 1 })
            {
                var arm = new GameObject(side < 0 ? "ArmL" : "ArmR").transform;
                arm.SetParent(body, false);
                arm.localPosition = new Vector3(side * 3.0f, 5.0f, 0);
                Art.Part("Joint", arm, Art.Sphere, dark, Vector3.zero, Vector3.one * 1.0f);
                Art.Part("UpperArm", arm, hex, hull, new Vector3(0, -1.1f, 0.2f), new Vector3(0.8f, 2.0f, 0.8f));
                Art.Part("Forearm", arm, taper, hull, new Vector3(0, -2.6f, 0.5f), new Vector3(1.0f, 1.4f, 1.0f));
                Art.Part("Hammer", arm, box, plate, new Vector3(0, -3.6f, 0.6f), new Vector3(1.6f, 1.0f, 1.6f));
                Art.Part("HammerGlow", arm, box, glow, new Vector3(0, -4.12f, 0.6f), new Vector3(1.3f, 0.06f, 1.3f), false);
            }
        }
    }
}
