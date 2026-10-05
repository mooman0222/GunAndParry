using System;
using UnityEngine;

namespace GunAndParry
{
    public enum SfxId
    {
        Shoot, Shotgun, Hit, Kill, Parry, ParryWhiff, Hurt, Pickup, Gold, LevelUp, Explosion,
        Rail, Tesla, Missile, Dash, Click, Hover, Chest, Warning, EnemyShoot, Heal, Overdrive, Victory, Defeat,
        Perfect, Break, CounterShot, Windup, Roar, Slam, Laser, ChestBurst, Shard
    }

    /// <summary>
    /// Playback for procedurally synthesized SFX (designed in <see cref="SfxBank"/>) and music (no audio assets required).
    /// Replace individual clips with authored audio later by assigning to <see cref="Override"/>.
    /// </summary>
    public static class Sfx
    {
        const int Rate = 44100;
        static AudioClip[][] clips;
        static float[] gains;
        static float[] minInterval;
        static float[] lastPlayed;
        static AudioSource[] voices;
        static int nextVoice;
        static AudioSource music;
        static AudioClip[] tracks;
        static float sfxVolume = 0.8f, musicVolume = 0.55f;
        static int currentTrack = -1;

        public static void Init(GameObject host)
        {
            int n = Enum.GetValues(typeof(SfxId)).Length;
            clips = new AudioClip[n][];
            gains = new float[n];
            minInterval = new float[n];
            lastPlayed = new float[n];
            for (int i = 0; i < n; i++) lastPlayed[i] = -1;

            voices = new AudioSource[24];
            for (int i = 0; i < voices.Length; i++)
            {
                voices[i] = host.AddComponent<AudioSource>();
                voices[i].playOnAwake = false;
                voices[i].spatialBlend = 0f;
            }
            music = host.AddComponent<AudioSource>();
            music.loop = true;
            music.playOnAwake = false;
            music.ignoreListenerPause = true;

            foreach (var r in SfxBank.Build())
            {
                int id = (int)r.Patch.Id;
                gains[id] = r.Patch.Gain;
                minInterval[id] = r.Patch.MinInterval;
                clips[id] = new AudioClip[r.Stereo.Length];
                for (int v = 0; v < r.Stereo.Length; v++)
                {
                    var data = r.Stereo[v];
                    var clip = AudioClip.Create($"{r.Patch.Id}_{v}", data.Length / 2, 2, Synth.Rate, false);
                    clip.SetData(data, 0);
                    clips[id][v] = clip;
                }
            }

            tracks = new[]
            {
                Music.Render("Title", 96, new[] { 57, 53, 48, 55 }, new[] { false, false, false, false }, 0),
                Music.Render("Battle", 128, new[] { 57, 53, 48, 55 }, new[] { true, false, false, false }, 1),
                Music.Render("Boss", 144, new[] { 50, 46, 43, 45 }, new[] { true, false, true, false }, 2),
            };
        }

        static float Square(float t, float f) => Mathf.Repeat(t * f, 1f) < 0.5f ? 1f : -1f;
        static float Saw(float t, float f) => Mathf.Repeat(t * f, 1f) * 2f - 1f;

        /// <summary>Assign an authored clip to replace the synthesized one.</summary>
        public static void Override(SfxId id, AudioClip clip) => clips[(int)id] = new[] { clip };

        public static void Play(SfxId id, float volume = 1f, float pitch = 1f, float pitchJitter = 0.06f)
        {
            if (clips == null) return;
            int i = (int)id;
            float now = Time.unscaledTime;
            if (now - lastPlayed[i] < minInterval[i]) return;
            lastPlayed[i] = now;
            var v = voices[nextVoice];
            nextVoice = (nextVoice + 1) % voices.Length;
            v.pitch = pitch * (1f + UnityEngine.Random.Range(-pitchJitter, pitchJitter));
            var variants = clips[i];
            v.PlayOneShot(variants[UnityEngine.Random.Range(0, variants.Length)], volume * gains[i] * sfxVolume);
        }

        public static void PlayMusic(int track)
        {
            if (tracks == null || track == currentTrack) return;
            currentTrack = track;
            music.clip = tracks[track];
            music.volume = musicVolume;
            music.Play();
        }

        public static void SetVolumes(float musicVol, float sfxVol)
        {
            musicVolume = musicVol;
            sfxVolume = sfxVol;
            if (music != null) music.volume = musicVolume;
        }

        /// <summary>Tiny offline sequencer: renders an 8-bar loop from a chord progression.</summary>
        static class Music
        {
            public static AudioClip Render(string name, int bpm, int[] roots, bool[] minorOverride, int intensity)
            {
                float beat = 60f / bpm;
                int bars = 8;
                int n = Mathf.RoundToInt(bars * 4 * beat * Rate);
                var buf = new float[n];
                var rng = new System.Random(name.GetHashCode());

                for (int bar = 0; bar < bars; bar++)
                {
                    int chord = bar / 2 % roots.Length;
                    int root = roots[chord];
                    bool minor = chord == 0 || minorOverride[chord];
                    int[] tones = { 0, minor ? 3 : 4, 7, 12 };
                    float barStart = bar * 4 * beat;

                    // Pad: sustained detuned saws, quiet.
                    for (int k = 0; k < 3; k++)
                        Note(buf, barStart, 4 * beat, Midi(root + tones[k] + 12), 0.035f, 0.6f, Wave.SoftSaw, 1.2f);

                    if (intensity >= 1)
                    {
                        for (int b = 0; b < 4; b++)
                        {
                            float t = barStart + b * beat;
                            Kick(buf, t);
                            if (b % 2 == 1) Snare(buf, t, rng);
                            Hat(buf, t + beat * 0.5f, rng);
                            if (intensity >= 2) { Hat(buf, t + beat * 0.25f, rng, 0.5f); Hat(buf, t + beat * 0.75f, rng, 0.5f); }
                        }
                        // Bass: octave-bouncing eighths.
                        for (int e = 0; e < 8; e++)
                            Note(buf, barStart + e * beat * 0.5f, beat * 0.45f, Midi(root - 12 + (e % 2 == 1 ? 12 : 0)), 0.22f, 12f, Wave.Saw, 0f);
                    }

                    // Arpeggio sixteenths.
                    int[] pattern = { 0, 1, 2, 3, 2, 1, 2, 3 };
                    for (int s = 0; s < 16; s++)
                    {
                        float t = barStart + s * beat * 0.25f;
                        int tone = tones[pattern[s % pattern.Length]];
                        float vol = intensity == 0 ? 0.06f : 0.07f;
                        Note(buf, t, beat * 0.22f, Midi(root + tone + 12 + (intensity >= 2 && s % 4 == 3 ? 12 : 0)), vol, 18f, Wave.Square, 0f);
                        // Echo
                        Note(buf, t + beat * 0.75f, beat * 0.22f, Midi(root + tone + 12), vol * 0.35f, 18f, Wave.Square, 0f);
                    }
                }

                // Soft clip
                for (int i = 0; i < n; i++) buf[i] = (float)Math.Tanh(buf[i] * 1.4f) * 0.8f;
                var clip = AudioClip.Create(name, n, 1, Rate, false);
                clip.SetData(buf, 0);
                return clip;
            }

            enum Wave { Square, Saw, SoftSaw }

            static float Midi(int note) => 440f * Mathf.Pow(2f, (note - 69) / 12f);

            static void Note(float[] buf, float start, float duration, float freq, float vol, float decay, Wave wave, float attack)
            {
                int s0 = (int)(start * Rate);
                int len = (int)((duration + 0.05f) * Rate);
                float lp = 0;
                for (int i = 0; i < len && s0 + i < buf.Length; i++)
                {
                    float t = i / (float)Rate;
                    float raw = wave switch
                    {
                        Wave.Square => Square(t, freq) * 0.6f,
                        Wave.Saw => Saw(t, freq),
                        _ => (Saw(t, freq) + Saw(t, freq * 1.006f)) * 0.5f,
                    };
                    lp += (raw - lp) * (wave == Wave.Saw ? 0.12f : 0.25f);
                    float env = (attack > 0 ? Mathf.Clamp01(t / (attack * duration)) : Mathf.Clamp01(t * 400f)) *
                                Mathf.Exp(-t * decay / Mathf.Max(duration, 0.01f) * 0.25f) *
                                Mathf.Clamp01((duration + 0.05f - t) * 20f);
                    buf[s0 + i] += lp * env * vol;
                }
            }

            static void Kick(float[] buf, float start)
            {
                int s0 = (int)(start * Rate);
                float phase = 0;
                for (int i = 0; i < Rate * 0.3f && s0 + i < buf.Length; i++)
                {
                    float t = i / (float)Rate;
                    phase += 2 * Mathf.PI * Mathf.Lerp(45f, 140f, Mathf.Exp(-t * 30f)) / Rate;
                    buf[s0 + i] += Mathf.Sin(phase) * Mathf.Exp(-t * 9f) * 0.55f;
                }
            }

            static void Snare(float[] buf, float start, System.Random rng)
            {
                int s0 = (int)(start * Rate);
                for (int i = 0; i < Rate * 0.2f && s0 + i < buf.Length; i++)
                {
                    float t = i / (float)Rate;
                    float noise = (float)(rng.NextDouble() * 2 - 1);
                    buf[s0 + i] += (noise * 0.8f + Mathf.Sin(2 * Mathf.PI * 190 * t) * 0.4f) * Mathf.Exp(-t * 18f) * 0.22f;
                }
            }

            static void Hat(float[] buf, float start, System.Random rng, float vol = 1f)
            {
                int s0 = (int)(start * Rate);
                float prev = 0;
                for (int i = 0; i < Rate * 0.05f && s0 + i < buf.Length; i++)
                {
                    float t = i / (float)Rate;
                    float noise = (float)(rng.NextDouble() * 2 - 1);
                    float hp = noise - prev;
                    prev = noise;
                    buf[s0 + i] += hp * Mathf.Exp(-t * 60f) * 0.06f * vol;
                }
            }
        }
    }
}
