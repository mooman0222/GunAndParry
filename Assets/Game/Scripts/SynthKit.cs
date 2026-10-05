using System;

namespace GunAndParry
{
    /// <summary>
    /// Offline sound-design toolkit: band-limited oscillators, a TPT state-variable filter,
    /// envelopes, saturation and a stereo Freeverb-style reverb. Thread-safe per instance.
    /// </summary>
    public sealed class Synth
    {
        public const int Rate = 44100;
        public const float Dt = 1f / Rate;
        public readonly float[] Buf;
        public int N => Buf.Length;
        readonly Random rng;

        public Synth(float duration, int seed)
        {
            Buf = new float[(int)(duration * Rate)];
            rng = new Random(seed);
        }

        public float Rand(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        public float Noise() => (float)rng.NextDouble() * 2f - 1f;
        public bool Chance(float p) => rng.NextDouble() < p;

        /// <summary>Runs f(sampleIndex, timeSeconds) and adds its result into the buffer.</summary>
        public void Add(Func<int, float, float> f, float gain = 1f, float start = 0f)
        {
            for (int i = (int)(start * Rate); i < Buf.Length; i++) Buf[i] += f(i, i * Dt) * gain;
        }
    }

    /// <summary>Phase-accumulating oscillator with PolyBLEP anti-aliasing for saw/square.</summary>
    public sealed class Osc
    {
        double phase;

        public Osc(double startPhase = 0) { phase = startPhase; }

        double Step(float freq)
        {
            double dt = Math.Abs(freq) * Synth.Dt;
            phase += dt;
            phase -= Math.Floor(phase);
            return dt;
        }

        static float Blep(double t, double dt)
        {
            if (dt <= 0) return 0;
            if (t < dt) { t /= dt; return (float)(t + t - t * t - 1); }
            if (t > 1 - dt) { t = (t - 1) / dt; return (float)(t * t + t + t + 1); }
            return 0;
        }

        public float Sine(float freq, float phaseMod = 0f)
        {
            Step(freq);
            return (float)Math.Sin(2 * Math.PI * phase + phaseMod);
        }

        public float Saw(float freq)
        {
            double dt = Step(freq);
            return (float)(2 * phase - 1) - Blep(phase, dt);
        }

        public float Square(float freq, float width = 0.5f)
        {
            double dt = Step(freq);
            float v = phase < width ? 1f : -1f;
            v += Blep(phase, dt);
            double p2 = phase - width; p2 -= Math.Floor(p2);
            v -= Blep(p2, dt);
            return v;
        }

        public float Tri(float freq)
        {
            Step(freq);
            return (float)(1 - 4 * Math.Abs(phase - 0.5));
        }
    }

    /// <summary>Topology-preserving-transform state-variable filter (stable under fast cutoff modulation).</summary>
    public sealed class Svf
    {
        float ic1, ic2;
        public float Low, Band, High;

        public float Run(float x, float cutoff, float q)
        {
            cutoff = Math.Clamp(cutoff, 20f, Synth.Rate * 0.45f);
            float g = (float)Math.Tan(Math.PI * cutoff / Synth.Rate);
            float k = 1f / Math.Max(q, 0.05f);
            float a1 = 1f / (1f + g * (g + k)), a2 = g * a1, a3 = g * a2;
            float v3 = x - ic2;
            float v1 = a1 * ic1 + a2 * v3;
            float v2 = ic2 + a2 * ic1 + a3 * v3;
            ic1 = 2f * v1 - ic1;
            ic2 = 2f * v2 - ic2;
            Low = v2; Band = v1; High = x - k * v1 - v2;
            return Low;
        }

        public float Lp(float x, float cutoff, float q = 0.707f) { Run(x, cutoff, q); return Low; }
        public float Bp(float x, float cutoff, float q = 1f) { Run(x, cutoff, q); return Band; }
        public float Hp(float x, float cutoff, float q = 0.707f) { Run(x, cutoff, q); return High; }
    }

    public static class Dsp
    {
        /// <summary>Linear attack then exponential decay (decay = time constant in seconds).</summary>
        public static float Env(float t, float attack, float decay)
        {
            if (t < attack) return t / attack;
            return (float)Math.Exp(-(t - attack) / decay);
        }

        /// <summary>Attack, hold at 1, then exponential release.</summary>
        public static float Ahr(float t, float attack, float hold, float release)
        {
            if (t < attack) return t / attack;
            if (t < attack + hold) return 1f;
            return (float)Math.Exp(-(t - attack - hold) / release);
        }

        /// <summary>Exponential glide from a to b with time constant tau.</summary>
        public static float Sweep(float a, float b, float t, float tau) => b + (a - b) * (float)Math.Exp(-t / tau);

        public static float Drive(float x, float amount) => (float)(Math.Tanh(x * amount) / Math.Tanh(amount));

        public static float Midi(float note) => 440f * (float)Math.Pow(2, (note - 69) / 12f);

        /// <summary>
        /// Mono dry signal to normalized interleaved stereo: optional constant-power pan automation,
        /// stereo reverb send with a tail, DC blocking, edge fades and peak normalization.
        /// </summary>
        public static float[] ToStereo(float[] dry, float reverbSend, float tail, Func<float, float> pan = null, float peak = 0.89f)
        {
            // Patches may still be ringing when their buffer ends; fade the dry signal so it never truncates into a click.
            int dryFade = Math.Min(dry.Length / 3, (int)(0.012f * Synth.Rate));
            for (int i = 0; i < dryFade; i++) dry[dry.Length - 1 - i] *= i / (float)dryFade;

            int n = dry.Length + (int)(tail * Synth.Rate);
            var outL = new float[n];
            var outR = new float[n];
            var verbL = new Reverb(0);
            var verbR = new Reverb(23);
            float dcInL = 0, dcOutL = 0, dcInR = 0, dcOutR = 0;
            for (int i = 0; i < n; i++)
            {
                float x = i < dry.Length ? dry[i] : 0f;
                if (float.IsNaN(x) || float.IsInfinity(x)) x = 0f;
                float p = pan != null ? Math.Clamp(pan(i * Synth.Dt), -1f, 1f) : 0f;
                float angle = (p + 1f) * 0.25f * (float)Math.PI;
                float l = x * (float)Math.Cos(angle) * 1.4142f;
                float r = x * (float)Math.Sin(angle) * 1.4142f;
                float send = x * reverbSend;
                l += verbL.Process(send);
                r += verbR.Process(send);
                // DC blocker (~20 Hz)
                dcOutL = l - dcInL + 0.997f * dcOutL; dcInL = l;
                dcOutR = r - dcInR + 0.997f * dcOutR; dcInR = r;
                outL[i] = dcOutL;
                outR[i] = dcOutR;
            }

            // Trim the silent part of the tail, then fade edges so clips never click.
            int last = n - 1;
            while (last > dry.Length && Math.Abs(outL[last]) < 1e-4f && Math.Abs(outR[last]) < 1e-4f) last--;
            n = Math.Min(n, last + (int)(0.02f * Synth.Rate));
            float max = 1e-6f;
            for (int i = 0; i < n; i++) max = Math.Max(max, Math.Max(Math.Abs(outL[i]), Math.Abs(outR[i])));
            float norm = peak / max;
            int fadeIn = 32, fadeOut = Math.Min(n / 4, (int)(0.015f * Synth.Rate));
            var stereo = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                float g = norm;
                if (i < fadeIn) g *= i / (float)fadeIn;
                if (i > n - fadeOut) g *= (n - i) / (float)fadeOut;
                stereo[i * 2] = outL[i] * g;
                stereo[i * 2 + 1] = outR[i] * g;
            }
            return stereo;
        }

        /// <summary>Compact Freeverb: 4 damped combs + 2 allpasses. Offset decorrelates L/R.</summary>
        sealed class Reverb
        {
            readonly float[][] combs;
            readonly int[] combIdx;
            readonly float[] combStore;
            readonly float[][] aps;
            readonly int[] apIdx;

            public Reverb(int offset)
            {
                int[] c = { 1116, 1188, 1277, 1356 };
                int[] a = { 556, 441 };
                combs = new float[c.Length][];
                for (int i = 0; i < c.Length; i++) combs[i] = new float[c[i] + offset];
                combIdx = new int[c.Length];
                combStore = new float[c.Length];
                aps = new float[a.Length][];
                for (int i = 0; i < a.Length; i++) aps[i] = new float[a[i] + offset];
                apIdx = new int[a.Length];
            }

            public float Process(float input)
            {
                const float feedback = 0.8f, damp = 0.28f;
                float o = 0;
                for (int i = 0; i < combs.Length; i++)
                {
                    var b = combs[i];
                    float y = b[combIdx[i]];
                    combStore[i] = y * (1 - damp) + combStore[i] * damp;
                    b[combIdx[i]] = input + combStore[i] * feedback;
                    combIdx[i] = (combIdx[i] + 1) % b.Length;
                    o += y;
                }
                for (int i = 0; i < aps.Length; i++)
                {
                    var b = aps[i];
                    float buf = b[apIdx[i]];
                    b[apIdx[i]] = o + buf * 0.5f;
                    o = buf - o;
                    apIdx[i] = (apIdx[i] + 1) % b.Length;
                }
                return o * 0.25f;
            }
        }
    }
}
