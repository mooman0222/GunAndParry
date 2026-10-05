using System.Collections.Generic;
using UnityEngine;

namespace GunAndParry
{
    /// <summary>Particles, flat decals (rings/telegraphs), beams and screen feedback. All pooled.</summary>
    public sealed class Fx : MonoBehaviour
    {
        ParticleSystem sparks, glow, debris;

        sealed class Decal
        {
            public Transform T;
            public Material Mat;
            public float Start, Duration, From, To;
            public Color Color;
            public bool FadeIn;   // telegraphs grow in alpha, rings fade out
            public Transform Follow;
            public Vector2 Aspect;
        }

        sealed class BeamLine
        {
            public LineRenderer L;
            public float Start, Duration, Width;
            public Color Color;
        }

        readonly List<Decal> decals = new();
        readonly Stack<Decal> decalPool = new();
        readonly List<BeamLine> beams = new();
        readonly Stack<BeamLine> beamPool = new();
        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        public void Init()
        {
            sparks = CreateSystem("Sparks", ParticleSystemRenderMode.Stretch, 0.25f, 0.45f);
            glow = CreateSystem("Glow", ParticleSystemRenderMode.Billboard, 0.25f, 0.5f);
            debris = CreateSystem("Debris", ParticleSystemRenderMode.Mesh, 0.6f, 1.0f);
            var dr = debris.GetComponent<ParticleSystemRenderer>();
            dr.mesh = Art.Cube;
            dr.sharedMaterial = Art.ParticleSolid;
            var dm = debris.main;
            dm.gravityModifier = 2.5f;
        }

        ParticleSystem CreateSystem(string name, ParticleSystemRenderMode mode, float lifeMin, float lifeMax)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(lifeMin, lifeMax);
            main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 10f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.08f, 0.18f);
            main.maxParticles = 4000;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.1f;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0, 1) });
            col.color = g;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 1, 1, 0.2f));
            var limit = ps.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.dampen = 0.12f;
            limit.limit = 0.5f;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = mode;
            r.sharedMaterial = Art.Particle;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (mode == ParticleSystemRenderMode.Stretch) { r.velocityScale = 0.06f; r.lengthScale = 1.5f; }
            ps.Play();
            return ps;
        }

        static void Emit(ParticleSystem ps, Vector3 pos, Color color, int count, float speed, float size)
        {
            var ep = new ParticleSystem.EmitParams
            {
                position = pos,
                applyShapeToPosition = true,
                startColor = color,
            };
            for (int i = 0; i < count; i++)
            {
                ep.velocity = Random.onUnitSphere * (speed * Random.Range(0.4f, 1f));
                ep.startSize = size * Random.Range(0.6f, 1.2f);
                ps.Emit(ep, 1);
            }
        }

        static Vector3 Flat(Vector3 v) { v.y = Mathf.Abs(v.y) * 0.6f; return v; }

        public void Sparks(Vector3 pos, Color color, int count = 6, float speed = 9f)
        {
            var ep = new ParticleSystem.EmitParams { position = pos, applyShapeToPosition = true, startColor = Art.Hdr(color, 2.5f) };
            for (int i = 0; i < count; i++)
            {
                ep.velocity = Flat(Random.onUnitSphere) * (speed * Random.Range(0.5f, 1f));
                ep.startSize = Random.Range(0.06f, 0.14f);
                sparks.Emit(ep, 1);
            }
        }

        public void Explosion(Vector3 pos, Color color, float size = 1f)
        {
            Emit(glow, pos, Art.Hdr(color, 2f), Mathf.RoundToInt(8 * size), 4f * size, 0.9f * size);
            Sparks(pos, color, Mathf.RoundToInt(14 * size), 12f * size);
            var ep = new ParticleSystem.EmitParams { position = pos + Vector3.up * 0.3f, applyShapeToPosition = true, startColor = Art.Hdr(color, 1.6f) };
            int n = Mathf.RoundToInt(5 * size);
            for (int i = 0; i < n; i++)
            {
                ep.velocity = (Random.insideUnitSphere + Vector3.up * 1.2f) * (6f * Mathf.Sqrt(size));
                ep.startSize = Random.Range(0.1f, 0.25f) * Mathf.Sqrt(size);
                ep.rotation3D = Random.insideUnitSphere * 180f;
                debris.Emit(ep, 1);
            }
        }

        public void Glow(Vector3 pos, Color color, float size, int count = 1)
        {
            Emit(glow, pos, Art.Hdr(color, 2f), count, 1f, size);
        }

        /// <summary>Expanding flat ring on the ground (parry, shockwave).</summary>
        public void Ring(Vector3 pos, Color color, float fromRadius, float toRadius, float duration, Transform follow = null)
        {
            SpawnDecal(Art.Ring, pos, color, fromRadius, toRadius, duration, false, follow);
        }

        /// <summary>Danger zone that fills in over its duration, then disappears.</summary>
        public void Telegraph(Vector3 pos, float radius, float duration)
        {
            SpawnDecal(Art.Circle, pos, new Color(1f, 0.15f, 0.2f, 0.45f), radius * 2f, radius * 2f, duration, true, null);
            SpawnDecal(Art.Ring, pos, new Color(1f, 0.25f, 0.3f, 1f), radius * 2f, radius * 2f, duration, true, null);
        }

        /// <summary>Rectangular danger lane (e.g. an enemy lunge path) that fills in until the attack lands.</summary>
        public void LineTelegraph(Vector3 from, Vector3 dir, float length, float width, float duration)
        {
            dir.y = 0;
            dir.Normalize();
            float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            SpawnDecal(Texture2D.whiteTexture, from + dir * (length * 0.5f), new Color(1f, 0.15f, 0.12f, 0.4f), 1f, 1f, duration, true, null, yaw, new Vector2(width, length));
        }

        void SpawnDecal(Texture tex, Vector3 pos, Color color, float from, float to, float duration, bool fadeIn, Transform follow, float yaw = 0f, Vector2? aspect = null)
        {
            var d = decalPool.Count > 0 ? decalPool.Pop() : CreateDecal();
            d.Mat.SetTexture("_BaseMap", tex);
            d.T.gameObject.SetActive(true);
            d.T.position = new Vector3(pos.x, 0.03f + decals.Count * 0.0005f, pos.z);
            d.T.rotation = Quaternion.Euler(90, yaw, 0);
            d.Aspect = aspect ?? Vector2.one;
            d.Start = Time.time; d.Duration = duration; d.From = from; d.To = to; d.Color = color; d.FadeIn = fadeIn; d.Follow = follow;
            Animate(d, 0f);
            decals.Add(d);
        }

        Decal CreateDecal()
        {
            var go = new GameObject("Decal");
            go.transform.SetParent(transform, false);
            go.transform.rotation = Quaternion.Euler(90, 0, 0);
            var mat = Art.Transparent(Art.Ring, Color.white);
            Art.AddMesh(go, Art.Quad, mat, false);
            return new Decal { T = go.transform, Mat = mat };
        }

        static void Animate(Decal d, float t)
        {
            float e = 1f - (1f - t) * (1f - t);
            float s = Mathf.Lerp(d.From, d.To, e);
            d.T.localScale = new Vector3(s * d.Aspect.x, s * d.Aspect.y, 1);
            var c = d.Color;
            c.a *= d.FadeIn ? Mathf.Lerp(0.3f, 1f, t) : 1f - t;
            d.Mat.SetColor(BaseColorId, c);
        }

        /// <summary>Straight or jagged beam (railgun, lightning).</summary>
        public void Beam(Vector3[] points, int count, Color color, float width, float duration)
        {
            var b = beamPool.Count > 0 ? beamPool.Pop() : CreateBeam();
            b.L.gameObject.SetActive(true);
            b.L.positionCount = count;
            for (int i = 0; i < count; i++) b.L.SetPosition(i, points[i]);
            b.Start = Time.time; b.Duration = duration; b.Width = width; b.Color = Art.Hdr(color, 3f);
            UpdateBeam(b, 0);
            beams.Add(b);
        }

        BeamLine CreateBeam()
        {
            var go = new GameObject("Beam");
            go.transform.SetParent(transform, false);
            var l = go.AddComponent<LineRenderer>();
            l.sharedMaterial = Art.Trail;
            l.useWorldSpace = true;
            l.numCapVertices = 2;
            l.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            l.receiveShadows = false;
            l.alignment = LineAlignment.View;
            return new BeamLine { L = l };
        }

        static void UpdateBeam(BeamLine b, float t)
        {
            float w = b.Width * (1f - t * t);
            b.L.startWidth = b.L.endWidth = w;
            var c = b.Color;
            c.a = 1f - t;
            b.L.startColor = b.L.endColor = c;
        }

        public void Clear()
        {
            foreach (var d in decals) { d.T.gameObject.SetActive(false); decalPool.Push(d); }
            decals.Clear();
            foreach (var b in beams) { b.L.gameObject.SetActive(false); beamPool.Push(b); }
            beams.Clear();
            sparks.Clear(); glow.Clear(); debris.Clear();
        }

        public void Tick()
        {
            float now = Time.time;
            for (int i = decals.Count - 1; i >= 0; i--)
            {
                var d = decals[i];
                float t = (now - d.Start) / d.Duration;
                if (t >= 1f)
                {
                    d.T.gameObject.SetActive(false);
                    decalPool.Push(d);
                    decals.RemoveAt(i);
                    continue;
                }
                if (d.Follow != null) d.T.position = new Vector3(d.Follow.position.x, d.T.position.y, d.Follow.position.z);
                Animate(d, t);
            }
            for (int i = beams.Count - 1; i >= 0; i--)
            {
                var b = beams[i];
                float t = (now - b.Start) / b.Duration;
                if (t >= 1f)
                {
                    b.L.gameObject.SetActive(false);
                    beamPool.Push(b);
                    beams.RemoveAt(i);
                    continue;
                }
                UpdateBeam(b, t);
            }
        }
    }

    /// <summary>Isometric follow camera with trauma-based shake and zoom punch.</summary>
    public sealed class CameraRig : MonoBehaviour
    {
        public static readonly Vector3 Angles = new(52f, 45f, 0f);
        const float Distance = 21f;

        public Transform Target;
        public Camera Cam;
        public float Zoom = 1f;
        float zoomSmoothed = 1f;
        float trauma, punch;
        Vector3 focus;
        public bool Orbit; // title screen

        public void Shake(float amount)
        {
            if (!SaveSystem.Data.screenShake) return;
            trauma = Mathf.Min(1f, trauma + amount);
        }

        public void Punch(float amount) => punch = Mathf.Max(punch, amount);

        public void Snap()
        {
            if (Target != null) focus = Target.position;
            LateUpdate();
        }

        void LateUpdate()
        {
            float dt = Time.unscaledDeltaTime;
            var rot = Quaternion.Euler(Angles);
            if (Orbit)
            {
                rot = Quaternion.Euler(40f, Time.unscaledTime * 6f, 0f);
                focus = Vector3.Lerp(focus, Vector3.zero, 1f - Mathf.Exp(-2f * dt));
            }
            else if (Target != null)
            {
                focus = Vector3.Lerp(focus, Target.position, 1f - Mathf.Exp(-10f * dt));
            }

            zoomSmoothed = Mathf.Lerp(zoomSmoothed, Zoom, 1f - Mathf.Exp(-1.5f * dt));
            float dist = Distance * zoomSmoothed * (1f - punch * 0.08f);
            var pos = focus - rot * Vector3.forward * dist + Vector3.up * 0.5f;

            float shake = trauma * trauma;
            float t = Time.unscaledTime * 25f;
            var offset = new Vector3(Mathf.PerlinNoise(t, 0.1f) - 0.5f, Mathf.PerlinNoise(0.2f, t) - 0.5f, 0f) * (shake * 1.6f);
            var roll = (Mathf.PerlinNoise(t, 7.7f) - 0.5f) * shake * 4f;

            transform.SetPositionAndRotation(pos + rot * offset, rot * Quaternion.Euler(0, 0, roll));
            trauma = Mathf.Max(0f, trauma - dt * 1.8f);
            punch = Mathf.Max(0f, punch - dt * 4f);
        }
    }
}
