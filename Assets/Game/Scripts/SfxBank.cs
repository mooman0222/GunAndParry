using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GunAndParry
{
    /// <summary>
    /// Sound design for every SFX, as layered synth patches (transient + body + tail),
    /// rendered offline into normalized stereo with several variations for frequent sounds.
    /// </summary>
    public static class SfxBank
    {
        public sealed class Patch
        {
            public SfxId Id;
            public int Variants;
            public float Duration, Gain, MinInterval, Reverb, Tail;
            public Action<Synth, int> Render;
            public Func<float, float> Pan;
        }

        public sealed class Rendered
        {
            public Patch Patch;
            public float[][] Stereo;   // [variant] interleaved L/R
        }

        /// <summary>Renders every patch in parallel (pure computation, no Unity API).</summary>
        public static Rendered[] Build()
        {
            var patches = Patches();
            var result = new Rendered[patches.Count];
            Parallel.For(0, patches.Count, i =>
            {
                var p = patches[i];
                var r = new Rendered { Patch = p, Stereo = new float[p.Variants][] };
                for (int v = 0; v < p.Variants; v++)
                {
                    var s = new Synth(p.Duration, (int)p.Id * 1000 + v * 17 + 1);
                    p.Render(s, v);
                    r.Stereo[v] = Dsp.ToStereo(s.Buf, p.Reverb, p.Tail, p.Pan);
                }
                result[i] = r;
            });
            return result;
        }

        static readonly List<Patch> list = new();

        static void Def(SfxId id, int variants, float duration, float gain, float interval, float reverb, float tail, Action<Synth, int> render, Func<float, float> pan = null)
            => list.Add(new Patch { Id = id, Variants = variants, Duration = duration, Gain = gain, MinInterval = interval, Reverb = reverb, Tail = tail, Render = render, Pan = pan });

        // ---------- shared layers ----------

        /// <summary>Bright noise click: gives every hit a crisp attack.</summary>
        static Func<int, float, float> Click(Synth s, float cutoff, float decay)
        {
            var hp = new Svf();
            return (i, t) => hp.Hp(s.Noise(), cutoff) * Dsp.Env(t, 0.0002f, decay);
        }

        /// <summary>Sine thump with a downward pitch glide: the "weight" layer.</summary>
        static Func<int, float, float> Thump(float from, float to, float glide, float decay)
        {
            var o = new Osc();
            return (i, t) => o.Sine(Dsp.Sweep(from, to, t, glide)) * Dsp.Env(t, 0.001f, decay);
        }

        /// <summary>Filtered noise whose cutoff falls over time: blasts, debris, air.</summary>
        static Func<int, float, float> NoiseSweep(Synth s, float from, float to, float glide, float decay, float q = 0.8f, float attack = 0.001f)
        {
            var f = new Svf();
            return (i, t) => f.Lp(s.Noise(), Dsp.Sweep(from, to, t, glide), q) * Dsp.Env(t, attack, decay);
        }

        /// <summary>Sparse random impulses through a bandpass: crackle/sizzle tails.</summary>
        static Func<int, float, float> Crackle(Synth s, float density, float cutoff, float decay)
        {
            var bp = new Svf();
            return (i, t) =>
            {
                float env = Dsp.Env(t, 0.001f, decay);
                // Both rate and loudness fall off, so late pops never stick out of the quiet tail.
                float imp = s.Chance(density * env * 100f / Synth.Rate) ? s.Noise() * 6f * env : 0f;
                return bp.Bp(imp, cutoff * s.Rand(0.7f, 1.3f), 1.2f);
            };
        }

        /// <summary>Two-operator FM bell (inharmonic ratio = metallic).</summary>
        static Func<int, float, float> Bell(float freq, float ratio, float index, float decay, float attack = 0.001f)
        {
            var car = new Osc(); var mod = new Osc();
            return (i, t) =>
            {
                float env = Dsp.Env(t, attack, decay);
                return car.Sine(freq, mod.Sine(freq * ratio) * index * env) * env;
            };
        }

        static Func<int, float, float> Delayed(Func<int, float, float> f, float start)
        {
            int s0 = (int)(start * Synth.Rate);
            return (i, t) => i < s0 ? 0f : f(i - s0, t - start);
        }

        static void Saturate(Synth s, float drive) { for (int i = 0; i < s.N; i++) s.Buf[i] = Dsp.Drive(s.Buf[i], drive); }

        // ---------- patches ----------

        static List<Patch> Patches()
        {
            if (list.Count > 0) return list;

            // Blaster: frequent, so short and mid-heavy rather than shrill. Saw "pew" through a closing filter + thump + click.
            Def(SfxId.Shoot, 4, 0.14f, 0.32f, 0.045f, 0.1f, 0.15f, (s, v) =>
            {
                var o = new Osc(); var lp = new Svf();
                float f0 = s.Rand(1500, 1900), f1 = s.Rand(330, 400);
                s.Add((i, t) => lp.Lp(o.Saw(Dsp.Sweep(f0, f1, t, 0.016f)), Dsp.Sweep(6500, 900, t, 0.035f), 2.2f) * Dsp.Env(t, 0.001f, 0.04f));
                s.Add(Thump(180, 55, 0.018f, 0.03f), 0.7f);
                s.Add(Click(s, 4000, 0.004f), 0.35f);
                Saturate(s, 1.8f);
            });

            // Scatter gun: heavy blast, sub punch, crackling pellets.
            Def(SfxId.Shotgun, 3, 0.5f, 0.42f, 0.06f, 0.22f, 0.4f, (s, v) =>
            {
                s.Add(NoiseSweep(s, 5000, 500, 0.05f, 0.09f), 1.2f);
                s.Add(Thump(120, 38, 0.03f, 0.12f), 1.3f);
                s.Add(Click(s, 3000, 0.006f), 0.8f);
                s.Add(Delayed(Crackle(s, 6f, 3500, 0.08f), 0.02f), 0.5f);
                Saturate(s, 2.6f);
            });

            // Enemy hit: dull, short knock so hundreds per minute don't fatigue.
            Def(SfxId.Hit, 4, 0.07f, 0.26f, 0.025f, 0.05f, 0.05f, (s, v) =>
            {
                var bp = new Svf();
                float fc = s.Rand(1400, 2200);
                s.Add((i, t) => bp.Bp(s.Noise(), fc, 1.4f) * Dsp.Env(t, 0.0005f, 0.012f), 1.4f);
                s.Add(Thump(s.Rand(320, 380), 160, 0.01f, 0.02f), 0.8f);
                Saturate(s, 1.5f);
            });

            // Enemy destroyed: crunchy FM pop + debris noise.
            Def(SfxId.Kill, 4, 0.32f, 0.42f, 0.03f, 0.14f, 0.25f, (s, v) =>
            {
                var car = new Osc(); var mod = new Osc();
                float f0 = s.Rand(260, 340);
                s.Add((i, t) =>
                {
                    float env = Dsp.Env(t, 0.001f, 0.05f);
                    float f = Dsp.Sweep(f0, 70, t, 0.04f);
                    return car.Sine(f, mod.Sine(f * 1.41f) * 5f * env) * env;
                });
                s.Add(NoiseSweep(s, 6000, 700, 0.04f, 0.08f), 0.7f);
                s.Add(Click(s, 3500, 0.003f), 0.5f);
                s.Add(Delayed(Crackle(s, 3f, 2500, 0.06f), 0.03f), 0.35f);
                Saturate(s, 2.2f);
            });

            // Parry: the signature sound, built to feel rewarding rather than loud.
            // A tuned, consonant "shing" (near-harmonic partials + a fifth below) instead of a clangy inharmonic cluster,
            // a soft-edged band-limited tick, a short low "thup" for weight, no saturation and a gentle top-end roll-off.
            Def(SfxId.Parry, 1, 0.75f, 0.62f, 0.04f, 0.2f, 0.55f, (s, v) =>
            {
                float f0 = Dsp.Midi(88); // E6
                (float ratio, float amp, float decay)[] partials =
                {
                    (1f, 1f, 0.3f), (2.003f, 0.38f, 0.2f), (3.008f, 0.14f, 0.12f), (4.02f, 0.05f, 0.07f),
                    (0.6674f, 0.42f, 0.26f),   // perfect fifth below: makes it sound like a chord, not a clank
                };
                for (int k = 0; k < partials.Length; k++)
                {
                    var (ratio, amp, decay) = partials[k];
                    var o = new Osc(k * 0.21);
                    s.Add((i, t) =>
                    {
                        // tiny, fast-settling pitch flick on impact + slow shimmer
                        float bend = 1f + 0.012f * (float)Math.Exp(-t / 0.012f) + 0.0015f * (float)Math.Sin(t * 38f + k);
                        return o.Sine(f0 * ratio * bend) * Dsp.Env(t, 0.0015f, decay);
                    }, amp * 0.5f);
                }
                var tick = new Svf();
                s.Add((i, t) => tick.Bp(s.Noise(), 3200, 1.1f) * Dsp.Env(t, 0.0008f, 0.004f), 0.35f);
                s.Add(Thump(170, 95, 0.015f, 0.045f), 0.45f);
                var lp = new Svf();
                for (int i = 0; i < s.N; i++) s.Buf[i] = lp.Lp(s.Buf[i], 8000f);
            });

            // PERFECT: a sparkling high chime layered over the parry (same key, an octave up), quick and airy.
            Def(SfxId.Perfect, 1, 0.9f, 0.45f, 0.05f, 0.35f, 0.7f, (s, v) =>
            {
                float f = Dsp.Midi(100); // E7
                s.Add(Bell(f, 2f, 0.7f, 0.22f, 0.002f), 0.6f);
                s.Add(Delayed(Bell(f * 1.4983f, 2f, 0.5f, 0.2f, 0.002f), 0.045f), 0.45f);
                s.Add(Delayed(Bell(f * 2f, 2f, 0.4f, 0.18f, 0.002f), 0.09f), 0.3f);
                var lp = new Svf();
                for (int i = 0; i < s.N; i++) s.Buf[i] = lp.Lp(s.Buf[i], 9000f);
            });

            // BREAK: a heavy, satisfying crunch — low impact, armour crack, short metal clang.
            Def(SfxId.Break, 2, 0.8f, 0.6f, 0.1f, 0.25f, 0.5f, (s, v) =>
            {
                s.Add(Thump(140, 40, 0.04f, 0.16f), 1.2f);
                s.Add(NoiseSweep(s, 3500, 300, 0.06f, 0.12f), 0.8f);
                s.Add(Delayed(Crackle(s, 5f, 2200, 0.08f), 0.01f), 0.6f);
                s.Add(Bell(Dsp.Midi(64 + v * 2), 1.41f, 2.5f, 0.18f), 0.35f);
                s.Add(Click(s, 2500, 0.005f), 0.6f);
                Saturate(s, 1.8f);
            });

            // Counter round: a fat, lower "thoom" so it reads as special over the normal blaster.
            Def(SfxId.CounterShot, 2, 0.3f, 0.5f, 0.06f, 0.15f, 0.25f, (s, v) =>
            {
                var o = new Osc(); var lp = new Svf();
                s.Add((i, t) => lp.Lp(o.Saw(Dsp.Sweep(900, 160, t, 0.03f)), Dsp.Sweep(5000, 600, t, 0.05f), 1.6f) * Dsp.Env(t, 0.001f, 0.08f));
                s.Add(Thump(130, 45, 0.03f, 0.08f), 1f);
                s.Add(Click(s, 3500, 0.004f), 0.4f);
                Saturate(s, 2f);
            });

            // Enemy wind-up: a rising, growling charge that warns without being loud.
            Def(SfxId.Windup, 2, 0.7f, 0.4f, 0.15f, 0.15f, 0.2f, (s, v) =>
            {
                var o = new Osc(); var o2 = new Osc(0.4); var lp = new Svf();
                s.Add((i, t) =>
                {
                    float rise = Math.Min(1f, t / 0.62f);
                    float f = 70f + 90f * rise * rise;
                    return lp.Lp(o.Saw(f) + o2.Saw(f * 1.012f), 300 + 1500 * rise, 3f) * Math.Min(1f, t / 0.05f) * (t < 0.62f ? 0.4f + 0.6f * rise : (float)Math.Exp(-(t - 0.62f) / 0.03f));
                });
                Saturate(s, 2f);
            });

            // Colossus roar: detuned low saws with a formant-like sweeping band and a sub rumble.
            Def(SfxId.Roar, 1, 1.6f, 0.75f, 0.5f, 0.4f, 0.8f, (s, v) =>
            {
                for (int k = 0; k < 3; k++)
                {
                    var o = new Osc(k * 0.3); var bp = new Svf(); float det = 1f + (k - 1) * 0.015f;
                    s.Add((i, t) => bp.Bp(o.Saw(Dsp.Sweep(55, 42, t, 0.8f) * det * (1f + 0.02f * (float)Math.Sin(t * 23f))), 300 + 500 * (float)Math.Sin(Math.PI * Math.Min(1f, t / 1.3f)), 2.5f) * Dsp.Ahr(t, 0.15f, 0.8f, 0.35f), 0.9f);
                }
                s.Add(Thump(60, 30, 0.3f, 0.6f), 0.6f);
                s.Add(NoiseSweep(s, 1200, 300, 0.5f, 0.6f, 0.8f, 0.1f), 0.25f);
                Saturate(s, 2.4f);
            });

            // Hammer slam: huge sub impact, debris, metallic ring.
            Def(SfxId.Slam, 2, 1.2f, 0.85f, 0.15f, 0.35f, 0.7f, (s, v) =>
            {
                s.Add(Thump(80, 24, 0.06f, 0.35f), 1.6f);
                s.Add(NoiseSweep(s, 3500, 200, 0.08f, 0.25f), 1f);
                s.Add(Crackle(s, 4f, 1600, 0.3f), 0.5f);
                s.Add(Bell(Dsp.Midi(38 + v), 1.41f, 2f, 0.4f), 0.3f);
                s.Add(Click(s, 2000, 0.008f), 0.7f);
                Saturate(s, 2.6f);
            });

            // Sweeping laser: buzzing resonant hum with a rising charge.
            Def(SfxId.Laser, 1, 2.4f, 0.55f, 1f, 0.3f, 0.5f, (s, v) =>
            {
                var a = new Osc(); var b = new Osc(0.2); var lp = new Svf();
                s.Add((i, t) => lp.Lp(a.Saw(110) + b.Square(110.6f, 0.3f), 900 + 600 * (float)Math.Sin(t * 14f), 4f) * Dsp.Ahr(t, 0.05f, 2.1f, 0.15f), 0.6f);
                var hp = new Svf();
                s.Add((i, t) => hp.Hp(s.Noise(), 5000) * Dsp.Ahr(t, 0.05f, 2.1f, 0.15f), 0.06f);
                Saturate(s, 1.8f);
            });

            // Cache burst: deep punch, a rising glissando of light, then a bright major chord bloom.
            Def(SfxId.ChestBurst, 1, 1.6f, 0.75f, 0.3f, 0.45f, 0.9f, (s, v) =>
            {
                s.Add(Thump(110, 40, 0.04f, 0.18f), 1.1f);
                s.Add(Click(s, 3000, 0.006f), 0.6f);
                var g1 = new Osc(); var lp = new Svf();
                s.Add((i, t) => lp.Lp(g1.Saw(Dsp.Sweep(220, 1760, 0.5f - Math.Min(t, 0.5f), 0.25f)), 1500 + 5000 * Math.Min(1f, t / 0.5f), 1.2f) * Dsp.Ahr(t, 0.05f, 0.35f, 0.12f), 0.12f);
                foreach (var (note, delay) in new[] { (76, 0.32f), (80, 0.38f), (83, 0.44f), (88, 0.5f) })
                    s.Add(Delayed(Bell(Dsp.Midi(note), 2f, 1.1f, 0.5f), delay), 0.32f);
                var hp = new Svf();
                s.Add((i, t) => hp.Hp(s.Noise(), 7500) * Dsp.Ahr(t, 0.4f, 0.3f, 0.4f), 0.03f);
            });

            // Shard absorbed: short crystalline ping (pitched up per shard at play time).
            Def(SfxId.Shard, 1, 0.5f, 0.45f, 0.03f, 0.3f, 0.5f, (s, v) =>
            {
                s.Add(Bell(Dsp.Midi(84), 2f, 0.9f, 0.12f, 0.001f), 0.7f);
                s.Add(Bell(Dsp.Midi(91), 3f, 0.5f, 0.08f, 0.001f), 0.35f);
                s.Add(Thump(400, 200, 0.01f, 0.02f), 0.25f);
            });

            // Parry swing: airy whoosh, panned across.
            Def(SfxId.ParryWhiff, 2, 0.24f, 0.35f, 0.05f, 0.1f, 0.15f, (s, v) =>
            {
                var bp = new Svf();
                s.Add((i, t) =>
                {
                    float shape = (float)Math.Sin(Math.PI * Math.Min(1f, t / 0.22f));
                    return bp.Bp(s.Noise(), 500 + 2800 * shape, 1.3f) * Dsp.Env(t, 0.03f, 0.07f);
                }, 1.6f);
            }, t => -0.6f + t * 5f);

            // Player hurt: distorted low impact + bit-crushed glitch.
            Def(SfxId.Hurt, 2, 0.4f, 0.6f, 0.1f, 0.12f, 0.2f, (s, v) =>
            {
                var o = new Osc(); var lp = new Svf();
                s.Add((i, t) => lp.Lp(o.Square(Dsp.Sweep(240, 80, t, 0.06f)), 1800, 1.5f) * Dsp.Env(t, 0.001f, 0.12f));
                s.Add(Thump(150, 45, 0.03f, 0.1f), 1f);
                s.Add(NoiseSweep(s, 3000, 600, 0.05f, 0.06f), 0.6f);
                Saturate(s, 3f);
                float held = 0;
                for (int i = 0; i < s.N; i++) { if (i % 7 == 0) held = s.Buf[i]; s.Buf[i] = s.Buf[i] * 0.5f + held * 0.5f; }
            });

            // XP gem: soft glassy blip (pitched up per combo at play time).
            Def(SfxId.Pickup, 2, 0.14f, 0.24f, 0.02f, 0.22f, 0.25f, (s, v) =>
            {
                float f = v == 0 ? 1568f : 1661f;
                s.Add(Bell(f, 2f, 0.6f, 0.05f, 0.002f), 0.8f);
                s.Add(Bell(f * 1.5f, 3f, 0.3f, 0.035f, 0.002f), 0.4f);
            });

            // Coin: two bright metallic chimes.
            Def(SfxId.Gold, 2, 0.4f, 0.38f, 0.04f, 0.22f, 0.3f, (s, v) =>
            {
                float f = v == 0 ? 1975f : 2093f;
                s.Add(Bell(f, 3.5f, 1.1f, 0.07f), 0.7f);
                s.Add(Delayed(Bell(f * 1.335f, 3.5f, 1.1f, 0.14f), 0.055f), 0.8f);
            });

            // Level up: ascending bell arpeggio over a filtered saw swell + air.
            Def(SfxId.LevelUp, 1, 1.2f, 0.7f, 0.1f, 0.38f, 0.8f, (s, v) =>
            {
                int[] notes = { 72, 76, 79, 84 };
                for (int n = 0; n < notes.Length; n++) s.Add(Delayed(Bell(Dsp.Midi(notes[n]), 2f, 1.5f, 0.35f), n * 0.07f), 0.5f);
                foreach (int n in new[] { 60, 64, 67 })
                {
                    var o = new Osc(n * 0.1); var lp = new Svf();
                    s.Add((i, t) => lp.Lp(o.Saw(Dsp.Midi(n) * 1.003f), 400 + 3000 * Dsp.Env(t, 0.15f, 0.4f), 1.2f) * Dsp.Ahr(t, 0.12f, 0.2f, 0.35f), 0.18f);
                }
                var hp = new Svf();
                s.Add((i, t) => hp.Hp(s.Noise(), 7000) * Dsp.Ahr(t, 0.2f, 0.1f, 0.3f), 0.03f);
            });

            // Explosion: sub drop, falling noise, long crackle.
            Def(SfxId.Explosion, 3, 1.1f, 0.8f, 0.06f, 0.3f, 0.6f, (s, v) =>
            {
                s.Add(Thump(95, 28, 0.08f, 0.3f), 1.4f);
                s.Add(NoiseSweep(s, 4500, 250, 0.12f, 0.3f), 1.1f);
                s.Add(Click(s, 2500, 0.008f), 0.6f);
                s.Add(Crackle(s, 5f, 1800, 0.35f), 0.6f);
                Saturate(s, 2.8f);
            });

            // Railgun: high-voltage zap, resonant sweep, sub boom.
            Def(SfxId.Rail, 2, 0.8f, 0.75f, 0.1f, 0.32f, 0.5f, (s, v) =>
            {
                var a = new Osc(); var b = new Osc(0.3); var lp = new Svf();
                s.Add((i, t) => lp.Lp(a.Saw(110) + b.Saw(110.8f), Dsp.Sweep(9000, 300, t, 0.12f), 4f) * Dsp.Env(t, 0.002f, 0.25f), 0.6f);
                var zap = new Osc(); var zmod = new Osc();
                s.Add((i, t) =>
                {
                    float f = Dsp.Sweep(4200, 180, t, 0.05f);
                    float env = Dsp.Env(t, 0.001f, 0.09f);
                    return zap.Sine(f, zmod.Sine(f * 2.01f) * 3f * env) * env;
                }, 0.6f);
                s.Add(Thump(80, 30, 0.05f, 0.2f), 1.2f);
                s.Add(Click(s, 5000, 0.004f), 0.8f);
                s.Add(NoiseSweep(s, 8000, 1000, 0.05f, 0.12f), 0.4f);
                Saturate(s, 2.2f);
            });

            // Tesla: gated electric sizzle + mains buzz.
            Def(SfxId.Tesla, 3, 0.3f, 0.45f, 0.06f, 0.15f, 0.2f, (s, v) =>
            {
                var bp = new Svf(); var buzz = new Osc(); var lp = new Svf();
                bool open = true; int next = 0;
                s.Add((i, t) =>
                {
                    if (i >= next) { open = s.Chance(0.65f); next = i + (int)s.Rand(40, 220); }
                    float sizzle = open ? bp.Bp(s.Noise(), s.Rand(1800, 4200), 2f) : bp.Bp(0, 2500, 2f);
                    return sizzle * Dsp.Env(t, 0.001f, 0.09f);
                }, 1.6f);
                s.Add((i, t) => lp.Lp(buzz.Saw(s.Rand(118, 122)), 1200, 1f) * Dsp.Env(t, 0.002f, 0.07f), 0.35f);
                Saturate(s, 2f);
            });

            // Missile launch: rising whoosh + small pop.
            Def(SfxId.Missile, 2, 0.45f, 0.45f, 0.06f, 0.18f, 0.3f, (s, v) =>
            {
                var bp = new Svf();
                s.Add((i, t) => bp.Bp(s.Noise(), Dsp.Sweep(400, 2400, t, 0.12f), 1.5f) * Dsp.Env(t, 0.015f, 0.15f), 1.5f);
                s.Add(Thump(160, 70, 0.02f, 0.04f), 0.6f);
                s.Add(Click(s, 3000, 0.003f), 0.3f);
            }, t => (float)Math.Sin(t * 9f) * 0.3f);

            // Dash: fast air rush, swept across the stereo field.
            Def(SfxId.Dash, 2, 0.28f, 0.6f, 0.05f, 0.12f, 0.2f, (s, v) =>
            {
                var bp = new Svf();
                s.Add((i, t) => bp.Bp(s.Noise(), Dsp.Sweep(2600, 450, t, 0.08f), 0.9f) * Dsp.Env(t, 0.008f, 0.08f), 1.5f);
                s.Add(Thump(90, 50, 0.03f, 0.05f), 0.4f);
            }, t => 0.7f - t * 6f);

            // UI
            Def(SfxId.Click, 1, 0.06f, 0.35f, 0.02f, 0.08f, 0.08f, (s, v) =>
            {
                s.Add(Bell(1400, 1.5f, 0.8f, 0.012f, 0.0005f), 0.8f);
                s.Add(Click(s, 5000, 0.002f), 0.4f);
            });
            Def(SfxId.Hover, 1, 0.05f, 0.18f, 0.03f, 0.08f, 0.06f, (s, v) => s.Add(Bell(2100, 2f, 0.4f, 0.012f, 0.001f)));

            // Chest: sparkling ascending run + shimmer.
            Def(SfxId.Chest, 1, 1.5f, 0.7f, 0.2f, 0.45f, 0.9f, (s, v) =>
            {
                int[] notes = { 67, 71, 74, 79, 83, 86, 91 };
                for (int n = 0; n < notes.Length; n++) s.Add(Delayed(Bell(Dsp.Midi(notes[n]), 2f, 0.9f, 0.3f), n * 0.06f), 0.45f);
                var hp = new Svf();
                s.Add((i, t) => hp.Hp(s.Noise(), 8000) * Dsp.Ahr(t, 0.1f, 0.3f, 0.3f) * (0.6f + 0.4f * (float)Math.Sin(t * 60f)), 0.04f);
            });

            // Warning: two-tone filtered siren with vibrato.
            Def(SfxId.Warning, 1, 1.3f, 0.55f, 1f, 0.25f, 0.5f, (s, v) =>
            {
                var a = new Osc(); var b = new Osc(0.25); var lp = new Svf();
                s.Add((i, t) =>
                {
                    float f = ((int)(t / 0.32f) % 2 == 0 ? 466f : 349f) * (1f + 0.01f * (float)Math.Sin(t * 35f));
                    return lp.Lp(a.Saw(f) + b.Square(f * 0.5f, 0.3f) * 0.5f, 2200, 1.4f) * Dsp.Ahr(t, 0.02f, 1.0f, 0.12f);
                }, 0.7f);
                Saturate(s, 1.6f);
            });

            // Enemy shot: rounder "bloop" so it never masks the player's own guns.
            Def(SfxId.EnemyShoot, 3, 0.16f, 0.3f, 0.06f, 0.15f, 0.15f, (s, v) =>
            {
                var car = new Osc(); var mod = new Osc();
                float f0 = s.Rand(560, 640);
                s.Add((i, t) =>
                {
                    float env = Dsp.Env(t, 0.003f, 0.05f);
                    float f = Dsp.Sweep(f0, 220, t, 0.035f);
                    return car.Sine(f, mod.Sine(f * 0.5f) * 1.8f * env) * env;
                });
            });

            // Heal: rising chorus shimmer.
            Def(SfxId.Heal, 1, 0.7f, 0.5f, 0.1f, 0.4f, 0.6f, (s, v) =>
            {
                for (int k = 0; k < 3; k++)
                {
                    var o = new Osc(k * 0.2); float detune = 1f + (k - 1) * 0.006f;
                    s.Add((i, t) => o.Tri((550f + 550f * Math.Min(1f, t / 0.4f)) * detune * (1 + k * 0.5f)) * Dsp.Ahr(t, 0.05f, 0.15f, 0.18f), 0.3f / (1 + k));
                }
                s.Add(Bell(1760, 2f, 0.8f, 0.25f, 0.02f), 0.3f);
            });

            // Overdrive: detuned saw riser into an impact.
            Def(SfxId.Overdrive, 1, 1.3f, 0.75f, 0.5f, 0.3f, 0.6f, (s, v) =>
            {
                for (int k = 0; k < 3; k++)
                {
                    var o = new Osc(k * 0.33); var lp = new Svf(); float det = 1f + (k - 1) * 0.008f;
                    s.Add((i, t) =>
                    {
                        float rise = Math.Min(1f, t / 0.55f);
                        return lp.Lp(o.Saw(Dsp.Midi(45 + 12 * rise * rise) * det), 250 + 6000 * rise * rise, 2f) * (t < 0.55f ? rise : Dsp.Env(t - 0.55f, 0.001f, 0.25f));
                    }, 0.35f);
                }
                var hp = new Svf();
                s.Add((i, t) => hp.Hp(s.Noise(), 1500 + 6000 * Math.Min(1f, t / 0.55f)) * (t < 0.55f ? t / 0.55f : 0f), 0.25f);
                s.Add(Delayed(Thump(110, 32, 0.05f, 0.25f), 0.55f), 1.2f);
                s.Add(Delayed(Click(s, 3000, 0.008f), 0.55f), 0.7f);
                Saturate(s, 1.8f);
            });

            // Victory fanfare: bells + brass-like filtered saws.
            Def(SfxId.Victory, 1, 2.2f, 0.75f, 1f, 0.4f, 1f, (s, v) =>
            {
                int[] notes = { 67, 72, 76, 79, 84 };
                for (int n = 0; n < notes.Length; n++)
                {
                    float start = n * 0.16f, f = Dsp.Midi(notes[n]);
                    var o = new Osc(); var lp = new Svf();
                    bool last = n == notes.Length - 1;
                    s.Add(Delayed((i, t) => lp.Lp(o.Saw(f), 600 + 2500 * Dsp.Env(t, 0.02f, 0.2f), 1.5f) * Dsp.Ahr(t, 0.01f, last ? 0.8f : 0.1f, last ? 0.5f : 0.1f), start), 0.3f);
                    s.Add(Delayed(Bell(f * 2, 2f, 1.2f, 0.4f), start), 0.25f);
                }
                Saturate(s, 1.4f);
            });

            // Defeat: falling detuned saws closing down + low boom.
            Def(SfxId.Defeat, 1, 2.2f, 0.7f, 1f, 0.35f, 0.8f, (s, v) =>
            {
                for (int k = 0; k < 2; k++)
                {
                    var o = new Osc(k * 0.5); var lp = new Svf(); float det = 1f + k * 0.01f;
                    s.Add((i, t) => lp.Lp(o.Saw(Dsp.Sweep(Dsp.Midi(57), Dsp.Midi(33), t, 0.7f) * det), Dsp.Sweep(3000, 200, t, 0.6f), 1.6f) * Dsp.Ahr(t, 0.02f, 0.6f, 0.5f), 0.4f);
                }
                s.Add(Thump(70, 25, 0.1f, 0.5f), 1f);
                Saturate(s, 1.6f);
            });

            return list;
        }
    }
}
