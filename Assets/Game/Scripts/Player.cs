using UnityEngine;

namespace GunAndParry
{
    public sealed class Player
    {
        public const float Radius = 0.45f;
        const float DashDistance = 4.2f, DashTime = 0.16f, DashCooldown = 1.2f;
        const float HurtInvuln = 0.6f;
        const float OverdriveMax = 100f;
        const float MuzzleHeight = 1.6f;
        /// <summary>Attacks parried while already this close (i.e. at the last moment) are PERFECT.</summary>
        public const float PerfectRadius = 1.5f;   // ~100ms before a typical enemy shot (7.5 m/s) would connect
        const float WhiffSlow = 0.4f;
        static readonly Color Gold = new(1f, 0.85f, 0.35f);
        static readonly Color Cyan = new(0.25f, 0.95f, 1f);

        public readonly Transform T;
        public Vector3 Pos => T.position;
        public Vector3 AimDir { get; private set; } = Vector3.forward;
        public Vector3 Muzzle => rig.Muzzle.position;
        public PlayerStats Stats { get; private set; }
        public float Hp { get; private set; }
        public float Overdrive { get; private set; }
        public bool InOverdrive => Game.Now < overdriveUntil;
        public float OverdriveRemaining => Mathf.Max(0, overdriveUntil - Game.Now);
        public bool IsParrying => Game.Now < parryUntil;
        /// <summary>Seconds since the current parry was pressed (bosses use it to judge PERFECT counters).</summary>
        public float ParryAge => Game.Now - parryStartedAt;
        public bool IsDashing => Game.Now < dashUntil;
        public float ParryReady01 => 1f - Mathf.Clamp01((parryReadyAt - Game.Now) / Mathf.Max(0.01f, parryCooldownLength));
        public float DashReady01 => 1f - Mathf.Clamp01((dashReadyAt - Game.Now) / DashCooldown);
        public int CounterRounds { get; private set; }
        public int MaxCounterRounds => Game.I.Loadout.Find(WeaponId.Blaster)?.Evolved == true ? 8 : 6;
        public bool God;
        /// <summary>Dev-only autopilot for soak tests (F6).</summary>
        public bool Bot;
        /// <summary>Dev: autopilot that never parries (to prove the final boss needs parrying).</summary>
        public bool BotNoParry;
        public int RevivesLeft;

        readonly PlayerRig rig;
        readonly MeshRenderer[] renderers;
        readonly Material[] baseMaterials;
        readonly MeshRenderer rangeRing, shield;
        readonly Material rangeMat, shieldMat;
        readonly TrailRenderer dashTrail;

        float parryUntil, parryReadyAt, parryCooldownLength = 1f, parryStartedAt = -10f;
        int parryWindow, parryCaughtThisWindow, parryCombo;
        bool windowPerfect, wasParrying;
        float whiffSlowUntil;
        float lastParryAt = -10f;
        static readonly int[] ParryScale = { 0, 2, 4, 7, 9, 12 };
        float dashUntil, dashReadyAt;
        Vector3 dashDir, moveDir;
        float invulnUntil, flashUntil, overdriveUntil;

        public Player(Transform parent)
        {
            T = new GameObject("Player").transform;
            T.SetParent(parent, false);

            rig = new PlayerRig(T);
            renderers = rig.Renderers;
            baseMaterials = new Material[renderers.Length];
            for (int i = 0; i < renderers.Length; i++) baseMaterials[i] = renderers[i].sharedMaterial;

            // Faint ring shows parry reach; brightens when parry is ready.
            rangeMat = Art.Transparent(Art.Ring, new Color(Cyan.r, Cyan.g, Cyan.b, 0.25f));
            var ring = Art.Part("ParryRange", T, Art.Quad, rangeMat, new Vector3(0, 0.02f, 0), Vector3.one, false);
            ring.transform.localRotation = Quaternion.Euler(90, 0, 0);
            rangeRing = ring.GetComponent<MeshRenderer>();

            var perfectRing = Art.Part("PerfectZone", T, Art.Quad, Art.Transparent(Art.Ring, new Color(Gold.r, Gold.g, Gold.b, 0.22f)), new Vector3(0, 0.025f, 0), Vector3.one * PerfectRadius * 2f, false);
            perfectRing.transform.localRotation = Quaternion.Euler(90, 0, 0);

            shieldMat = Art.Transparent(Texture2D.whiteTexture, new Color(0.4f, 1f, 1f, 0f));
            shield = Art.Part("Shield", T, Art.Sphere, shieldMat, new Vector3(0, 1.1f, 0), Vector3.one, false).GetComponent<MeshRenderer>();
            shield.enabled = false;

            dashTrail = T.gameObject.AddComponent<TrailRenderer>();
            dashTrail.sharedMaterial = Art.Trail;
            dashTrail.time = 0.25f;
            dashTrail.widthMultiplier = 0.9f;
            dashTrail.startColor = Art.Hdr(Cyan, 2f);
            dashTrail.endColor = new Color(Cyan.r, Cyan.g, Cyan.b, 0f);
            dashTrail.emitting = false;
            dashTrail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        public void ResetForRun(PlayerStats stats)
        {
            Stats = stats;
            Hp = stats.MaxHp;
            RevivesLeft = stats.Revives;
            Overdrive = 0;
            overdriveUntil = parryUntil = parryReadyAt = dashUntil = dashReadyAt = invulnUntil = 0;
            // A lethal hit flashes the model and ends the run before the flash can expire: always restore materials here.
            SetFlash(false);
            parryCombo = 0;
            lastParryAt = -10f;
            CounterRounds = 0;
            whiffSlowUntil = 0;
            wasParrying = false;
            T.position = Vector3.zero;
            T.rotation = Quaternion.identity;
            AimDir = Vector3.forward;
            rig.Snap(AimDir);
            dashTrail.Clear();
            God = false;
            UpdateRangeRing();
        }

        public void SetStats(PlayerStats stats)
        {
            float missing = Stats.MaxHp - Hp;
            Stats = stats;
            Hp = Mathf.Clamp(stats.MaxHp - missing, 1f, stats.MaxHp);
            UpdateRangeRing();
        }

        void UpdateRangeRing() => rangeRing.transform.localScale = Vector3.one * Stats.ParryRadius * 2f;

        /// <summary>Cooldown multiplier including Overdrive.</summary>
        public float CooldownMult => Stats.CooldownMult * (InOverdrive ? 0.5f : 1f);

        public void Tick(float dt, GameInput input, Camera cam, bool acceptInput)
        {
            float now = Game.Now;

            // ---- Movement (camera-relative) ----
            var mv = acceptInput ? input.Move : Vector2.zero;
            var fwd = cam.transform.forward; fwd.y = 0; fwd.Normalize();
            var right = cam.transform.right; right.y = 0; right.Normalize();
            moveDir = right * mv.x + fwd * mv.y;
            bool wantDash = acceptInput && input.DashPressed, wantParry = acceptInput && input.ParryPressed;
            if (Bot) BotThink(ref wantParry, ref wantDash);

            if (wantDash && now >= dashReadyAt)
            {
                dashDir = moveDir.sqrMagnitude > 0.01f ? moveDir.normalized : AimDir;
                dashUntil = now + DashTime;
                dashReadyAt = now + DashCooldown;
                dashTrail.emitting = true;
                Sfx.Play(SfxId.Dash, 0.6f);
            }

            float speedMult = now < whiffSlowUntil ? 0.45f : 1f;
            Vector3 velocity = IsDashing ? dashDir * (DashDistance / DashTime) : moveDir * (Stats.MoveSpeed * speedMult);
            if (!IsDashing && dashTrail.emitting) dashTrail.emitting = false;
            T.position += velocity * dt;

            // ---- Aim ----
            UpdateAim(input, cam, fwd, right);
            rig.Animate(dt, velocity, AimDir, IsDashing, InOverdrive, now);

            // ---- Parry ----
            if (wantParry && now >= parryReadyAt) StartParry(now);
            if (IsParrying) ResolveParry(now);
            else if (wasParrying && parryCaughtThisWindow == 0) Whiff(now);
            wasParrying = IsParrying;
            UpdateShield(now);

            // ---- Overdrive & regen ----
            if (Stats.Regen > 0) Heal(Stats.Regen * dt, false);

            // ---- Weapons ----
            foreach (var w in Game.I.Loadout.Weapons) w.Tick(this, dt);

            // ---- Feedback ----
            if (flashUntil > 0 && now >= flashUntil) SetFlash(false);
            bool blink = now < invulnUntil && !IsParrying && (int)(now * 20f) % 2 == 0;
            rig.Model.gameObject.SetActive(!blink);
            var rc = rangeMat.color;
            rc.a = Mathf.Lerp(rc.a, now >= parryReadyAt ? 0.5f : 0.1f, 1f - Mathf.Exp(-12f * dt));
            rangeMat.color = InOverdrive ? new Color(1f, 0.4f, 1f, rc.a + 0.1f) : new Color(Cyan.r, Cyan.g, Cyan.b, rc.a);
            if (InOverdrive && Random.value < 0.5f) Game.I.Fx.Glow(Pos + Random.insideUnitSphere * 0.6f + Vector3.up * 0.8f, new Color(1f, 0.4f, 1f), 0.35f);
        }

        void UpdateAim(GameInput input, Camera cam, Vector3 fwd, Vector3 right)
        {
            Vector3 aim = Vector3.zero;
            if (Bot)
            {
                var target = Game.I.Enemies.Nearest(Pos, 16f);
                if (target != null) aim = target.Pos - Pos;
            }
            else if (!input.UsingGamepad)
            {
                var ray = cam.ScreenPointToRay(input.MouseScreen);
                var plane = new Plane(Vector3.up, new Vector3(0, MuzzleHeight, 0));
                if (plane.Raycast(ray, out float enter)) aim = ray.GetPoint(enter) - (Pos + Vector3.up * MuzzleHeight);
            }
            else
            {
                var stick = input.AimStick;
                if (stick.sqrMagnitude > 0.09f) aim = right * stick.x + fwd * stick.y;
                else
                {
                    var target = Game.I.Enemies.Nearest(Pos, 14f);
                    if (target != null) aim = target.Pos - Pos;
                    else if (moveDir.sqrMagnitude > 0.01f) aim = moveDir;
                }
            }
            aim.y = 0;
            if (aim.sqrMagnitude > 0.04f) AimDir = aim.normalized;
        }

        void BotThink(ref bool parry, ref bool dash)
        {
            var g = Game.I;
            var flee = Vector3.zero;
            foreach (var e in g.Enemies.Active)
            {
                if (!e.Alive) continue;
                var d = Pos - e.Pos; d.y = 0;
                float sq = d.sqrMagnitude;
                if (sq < 64f) flee += d / Mathf.Max(sq, 0.25f);
            }
            var tangent = Vector3.Cross(Vector3.up, flee.normalized);
            var gem = g.Pickups.Nearest(Pos, 40f);
            var toGem = gem.HasValue ? gem.Value - Pos : Vector3.zero;
            toGem.y = 0;
            moveDir = flee.sqrMagnitude > 0.15f || toGem == Vector3.zero
                ? (flee.normalized * 0.7f + tangent * 0.7f).normalized
                : (toGem.normalized + flee * 2f).normalized;
            // Waits for the last moment (PERFECT) and for lunges to strike (BREAK), like a skilled player would.
            parry = g.Projectiles.AnyHostileWithin(Pos, PerfectRadius * 0.9f) || g.Enemies.Nearest(Pos, 1.4f) != null;
            foreach (var e in g.Enemies.Active)
                if (e.Alive && e.HasLunge && e.InStrike && (e.Pos - Pos).sqrMagnitude < 9f) { parry = true; break; }
            if (g.Enemies.Boss?.Boss is ColossusBrain colossus)
            {
                float impact = colossus.SlamImpactIn;
                var d = Pos - colossus.SlamTarget; d.y = 0;
                if (impact >= 0 && impact < 0.08f && d.magnitude < 3.6f) parry = true;
            }
            if (BotNoParry) parry = false;
            dash = Hp < Stats.MaxHp * 0.4f && g.Enemies.Nearest(Pos, 2f) != null;
        }

        void StartParry(float now)
        {
            parryWindow++;
            parryCaughtThisWindow = 0;
            windowPerfect = false;
            parryUntil = now + Stats.ParryWindow;
            parryStartedAt = now;
            rig.ParryStart();
            parryCooldownLength = Stats.ParryCooldown;
            parryReadyAt = now + parryCooldownLength;
            Game.I.Fx.Ring(Pos, Cyan, 0.5f, Stats.ParryRadius * 2f, Stats.ParryWindow + 0.05f, T);
            Sfx.Play(SfxId.ParryWhiff, 0.6f);
        }

        void ResolveParry(float now)
        {
            var g = Game.I;
            int bullets = g.Projectiles.Reflect(Pos, Stats.ParryRadius, PerfectRadius, Stats, out int perfectBullets);
            int enemies = g.Enemies.Parry(Pos, Stats.ParryRadius, PerfectRadius, parryWindow, Stats, out int perfectEnemies, out int breaks);
            RegisterParry(bullets, enemies, perfectBullets + perfectEnemies, breaks);
        }

        /// <summary>Rewards and feedback for parried attacks (also called by bosses whose attacks are countered).</summary>
        public void RegisterParry(int bullets, int enemies, int perfects, int breaks)
        {
            var g = Game.I;
            float now = Game.Now;
            int caught = bullets + enemies;
            if (caught == 0) return;

            bool first = parryCaughtThisWindow == 0;
            bool firstPerfect = perfects > 0 && !windowPerfect;
            parryCaughtThisWindow += caught;
            windowPerfect |= perfects > 0;
            g.Run.Parries += caught;
            g.Run.PerfectParries += perfects;
            g.Run.Breaks += breaks;

            AddOverdrive((bullets * 10f + enemies * 4f + perfects * 8f + breaks * 16f) * Stats.OverdriveGain);
            if (Stats.LeechHeal > 0) Heal(Stats.LeechHeal * caught);

            if (first)
            {
                // Deflecting a bullet rewards timing with a fast reset; body-counters keep the full cooldown so parry can't be spammed in a crowd.
                if (bullets > 0) SetParryCooldown(now, 0.35f);
                LoadCounterRounds(2);
                g.HitStop(bullets > 0 ? 0.09f : 0.05f);
                g.Rig.Shake(0.25f);
                g.Rig.Punch(1f);
                g.PulseChromatic(0.8f);
                g.Input.Rumble(0.4f, 0.8f, 0.12f);
                g.Fx.Ring(Pos, Color.white, Stats.ParryRadius * 1.6f, Stats.ParryRadius * 2.6f, 0.25f);
                g.Fx.Sparks(Pos + Vector3.up * 0.8f, Cyan, 24, 14f);
                // Chained parries climb a pentatonic scale: a musical reward for keeping the rhythm.
                parryCombo = now - lastParryAt < 2.5f ? Mathf.Min(parryCombo + 1, ParryScale.Length - 1) : 0;
                lastParryAt = now;
                Sfx.Play(SfxId.Parry, bullets > 0 ? 0.7f : 0.55f, Mathf.Pow(2f, ParryScale[parryCombo] / 12f) * (bullets > 0 ? 1f : 0.89f), 0f);
                if (perfects == 0 && breaks == 0) g.UI.Hud.ParryPopup(Pos, bullets > 0 ? "PARRY!" : "COUNTER!", UIKit.Accent);

                if (Stats.NovaDamage > 0)
                {
                    g.Enemies.DamageInRadius(Pos, Stats.NovaRadius, Stats.NovaDamage * Stats.DamageMult, ProjectileManager.SourceNova);
                    g.Fx.Ring(Pos, new Color(0.6f, 0.95f, 1f), 1f, Stats.NovaRadius * 2f, 0.3f);
                }
                foreach (var w in g.Loadout.Weapons) w.OnParry(this, perfects > 0);
            }

            if (firstPerfect)
            {
                // PERFECT: the attack was parried at the last moment.
                SetParryCooldown(now, 0.2f);
                LoadCounterRounds(2);
                g.SlowMo(0.35f);
                g.Fx.Ring(Pos, Gold, 0.5f, Stats.ParryRadius * 3f, 0.35f);
                g.Fx.Sparks(Pos + Vector3.up * 1f, Gold, 30, 16f);
                g.UI.Hud.ParryPopup(Pos + Vector3.up * 0.6f, "PERFECT!", UIKit.Gold);
                Sfx.Play(SfxId.Perfect, 0.6f, Mathf.Pow(2f, ParryScale[parryCombo] / 12f), 0f);
            }

            if (breaks > 0)
            {
                LoadCounterRounds(2);
                g.HitStop(0.14f);
                g.Rig.Shake(0.5f);
                g.Input.Rumble(0.8f, 0.6f, 0.2f);
                g.UI.Hud.ParryPopup(Pos + Vector3.up * 1.2f, "BREAK!", new Color(1f, 0.55f, 0.2f));
                Sfx.Play(SfxId.Break, 0.75f);
            }
        }

        void SetParryCooldown(float now, float length)
        {
            if (now + length >= parryReadyAt) return;
            parryCooldownLength = length;
            parryReadyAt = now + length;
        }

        /// <summary>Pressing parry with nothing to catch leaves you briefly sluggish, so it can't be mashed.</summary>
        void Whiff(float now)
        {
            if (Game.I.ChestOpening) return;   // parrying for the cache bonus is never punished
            whiffSlowUntil = now + WhiffSlow;
            Game.I.Fx.Ring(Pos, new Color(0.5f, 0.55f, 0.65f), 0.4f, 1.6f, 0.25f);
            Sfx.Play(SfxId.Hit, 0.25f, 0.6f);
        }

        void LoadCounterRounds(int n) => CounterRounds = Mathf.Min(MaxCounterRounds, CounterRounds + n);

        /// <summary>Called by the Blaster: spends one parry-loaded counter round if available.</summary>
        public bool TryConsumeCounterRound()
        {
            if (CounterRounds <= 0) return false;
            CounterRounds--;
            return true;
        }

        void UpdateShield(float now)
        {
            bool on = IsParrying;
            if (shield.enabled != on) shield.enabled = on;
            if (!on) return;
            float t = 1f - (parryUntil - now) / Mathf.Max(0.01f, Stats.ParryWindow);
            shield.transform.localScale = Vector3.one * Mathf.Lerp(1.6f, 2.2f, t);
            shieldMat.color = new Color(0.4f, 1f, 1f, 0.35f * (1f - t));
        }

        public void AddOverdrive(float amount)
        {
            if (InOverdrive) return;
            Overdrive = Mathf.Min(OverdriveMax, Overdrive + amount);
            if (Overdrive < OverdriveMax) return;
            Overdrive = 0;
            overdriveUntil = Game.Now + Stats.OverdriveDuration;
            Game.I.Fx.Ring(Pos, new Color(1f, 0.4f, 1f), 1f, 10f, 0.5f);
            Game.I.UI.Hud.Banner("OVERDRIVE", new Color(1f, 0.45f, 1f), 1.2f);
            Game.I.Rig.Shake(0.3f);
            Sfx.Play(SfxId.Overdrive, 0.8f);
        }

        /// <summary>Weapon recoil feedback on the model.</summary>
        public void Kick(float amount) => rig.Kick(amount);

        public float Overdrive01 => InOverdrive ? OverdriveRemaining / Stats.OverdriveDuration : Overdrive / OverdriveMax;

        /// <summary>Applies damage unless invulnerable. Returns true if the hit landed (the attack is consumed).</summary>
        public bool TryHit(float amount, bool unparryable = false)
        {
            float now = Game.Now;
            if (Hp <= 0 || IsDashing || (IsParrying && !unparryable) || Game.I.ChestOpening) return false;
            if (now < invulnUntil) return false;
            var g = Game.I;
            invulnUntil = now + HurtInvuln;
            float dmg = Mathf.Max(1f, amount - Stats.Armor);
            if (!God) Hp -= dmg;
            SetFlash(true);
            g.Rig.Shake(0.4f);
            g.PulseHurt();
            g.Input.Rumble(0.8f, 0.3f, 0.2f);
            g.Fx.Sparks(Pos + Vector3.up * 0.8f, new Color(1f, 0.3f, 0.3f), 12, 10f);
            Sfx.Play(SfxId.Hurt, 0.8f);
            if (Hp <= 0) Die();
            return true;
        }

        void Die()
        {
            var g = Game.I;
            if (RevivesLeft > 0)
            {
                RevivesLeft--;
                Hp = Stats.MaxHp * 0.5f;
                invulnUntil = Game.Now + 2.5f;
                g.Projectiles.ClearHostile();
                g.Enemies.DamageInRadius(Pos, 8f, 200f, ProjectileManager.SourceNova);
                g.Fx.Ring(Pos, Color.white, 1f, 16f, 0.6f);
                g.UI.Hud.Banner("REVIVED", Color.white, 1.5f);
                Sfx.Play(SfxId.Heal, 1f, 0.8f);
                return;
            }
            Hp = 0;
            g.Fx.Explosion(Pos + Vector3.up * 0.8f, Cyan, 2.5f);
            g.EndRun(false);
        }

        public void Heal(float amount, bool feedback = true)
        {
            if (Hp >= Stats.MaxHp) return;
            Hp = Mathf.Min(Stats.MaxHp, Hp + amount);
            if (!feedback) return;
            Game.I.Fx.Glow(Pos + Vector3.up, new Color(0.3f, 1f, 0.5f), 1.2f, 3);
            Sfx.Play(SfxId.Heal, 0.5f);
        }

        void SetFlash(bool on)
        {
            flashUntil = on ? Game.Now + 0.1f : 0;
            for (int i = 0; i < renderers.Length; i++) renderers[i].sharedMaterial = on ? Art.Flash : baseMaterials[i];
        }
    }
}
