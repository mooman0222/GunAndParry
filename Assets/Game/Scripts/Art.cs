using System.Collections.Generic;
using UnityEngine;

namespace GunAndParry
{
    /// <summary>
    /// Procedural art: meshes, textures and material instances.
    /// Material templates are real assets under Resources/Materials so their shader variants survive build stripping.
    /// </summary>
    public static class Art
    {
        static Material litTemplate, unlitTemplate, transparentTemplate, particleTemplate;
        static readonly Dictionary<(Color, Color), Material> litCache = new();
        static readonly Dictionary<Color, Material> unlitCache = new();

        public static Material Flash { get; private set; }
        public static Material Particle { get; private set; }
        public static Material Trail { get; private set; }
        public static Material ParticleSolid { get; private set; }

        public static Mesh Cube, Sphere, Capsule, Cylinder, Quad, Octa, Spike, Gem, Diamond;
        public static Texture2D Glow, Circle, Ring, Grid, Line;
        public static Sprite Rounded, CircleSprite, RingSprite;

        static bool initialized;

        public static void Init()
        {
            if (initialized) return;
            initialized = true;

            litTemplate = LoadTemplate("M_LitEmissive", "Universal Render Pipeline/Lit");
            unlitTemplate = LoadTemplate("M_Unlit", "Universal Render Pipeline/Unlit");
            transparentTemplate = LoadTemplate("M_UnlitTransparent", "Universal Render Pipeline/Unlit");
            particleTemplate = LoadTemplate("M_ParticleAdditive", "Universal Render Pipeline/Particles/Unlit");
            if (!litTemplate.IsKeywordEnabled("_EMISSION"))
                Debug.LogError("M_LitEmissive has no _EMISSION keyword: every emissive color is ignored. Run 'Gun & Parry/Setup Project'.");

            Glow = RadialTexture(64, d => Mathf.Pow(Mathf.Clamp01(1f - d), 2.2f));
            Circle = RadialTexture(128, d => Mathf.Clamp01((1f - d) * 24f));
            Ring = RadialTexture(128, d => Mathf.Clamp01((1f - d) * 40f) * Mathf.Clamp01((d - 0.9f) * 40f) + 0.06f * Mathf.Clamp01((1f - d) * 40f));
            Grid = GridTexture();
            Line = LineTexture();

            Cube = PrimitiveMesh(PrimitiveType.Cube);
            Sphere = PrimitiveMesh(PrimitiveType.Sphere);
            Capsule = PrimitiveMesh(PrimitiveType.Capsule);
            Cylinder = PrimitiveMesh(PrimitiveType.Cylinder);
            Quad = PrimitiveMesh(PrimitiveType.Quad);
            Octa = ConvexMesh("Octa", new[] { new Vector3(0, 0.5f, 0), new Vector3(0, -0.5f, 0), new Vector3(0.5f, 0, 0), new Vector3(-0.5f, 0, 0), new Vector3(0, 0, 0.5f), new Vector3(0, 0, -0.5f) },
                new[] { 0, 2, 4, 0, 4, 3, 0, 3, 5, 0, 5, 2, 1, 4, 2, 1, 3, 4, 1, 5, 3, 1, 2, 5 });
            Spike = ConvexMesh("Spike", new[] { new Vector3(0, 0.25f, 0.65f), new Vector3(-0.45f, 0, -0.4f), new Vector3(0.45f, 0, -0.4f), new Vector3(0, 0.6f, -0.3f) },
                new[] { 0, 2, 1, 0, 3, 2, 0, 1, 3, 1, 2, 3 });
            Gem = ConvexMesh("Gem", new[] { new Vector3(0, 0.4f, 0), new Vector3(0, -0.4f, 0), new Vector3(0.22f, 0, 0), new Vector3(-0.22f, 0, 0), new Vector3(0, 0, 0.22f), new Vector3(0, 0, -0.22f) },
                new[] { 0, 2, 4, 0, 4, 3, 0, 3, 5, 0, 5, 2, 1, 4, 2, 1, 3, 4, 1, 5, 3, 1, 2, 5 });
            Diamond = ConvexMesh("Diamond", new[] { new Vector3(0, 0, 0.6f), new Vector3(0, 0, -0.6f), new Vector3(0.35f, 0, 0), new Vector3(-0.35f, 0, 0), new Vector3(0, 0.25f, 0), new Vector3(0, -0.25f, 0) },
                new[] { 0, 2, 4, 0, 4, 3, 0, 3, 5, 0, 5, 2, 1, 4, 2, 1, 3, 4, 1, 5, 3, 1, 2, 5 });

            Flash = new Material(unlitTemplate) { name = "Flash" };
            Flash.SetColor("_BaseColor", new Color(3f, 3f, 3f, 1f));

            Particle = new Material(particleTemplate) { name = "Particle" };
            Particle.SetTexture("_BaseMap", Glow);
            Trail = new Material(particleTemplate) { name = "Trail" };
            Trail.SetTexture("_BaseMap", Line);
            ParticleSolid = new Material(particleTemplate) { name = "ParticleSolid" };

            Rounded = Sprite.Create(RoundedTexture(64, 18), new Rect(0, 0, 64, 64), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(20, 20, 20, 20));
            CircleSprite = Sprite.Create(Circle, new Rect(0, 0, 128, 128), new Vector2(0.5f, 0.5f));
            RingSprite = Sprite.Create(Ring, new Rect(0, 0, 128, 128), new Vector2(0.5f, 0.5f));
        }

        static Material LoadTemplate(string name, string fallbackShader)
        {
            var m = Resources.Load<Material>("Materials/" + name);
            if (m != null) return m;
            Debug.LogError($"Material template '{name}' missing. Run 'Gun & Parry/Setup Project' in the editor.");
            return new Material(Shader.Find(fallbackShader));
        }

        /// <summary>Opaque lit material with HDR emission. Shared per color pair so the SRP batcher can batch.</summary>
        public static Material Lit(Color baseColor, Color emission)
        {
            if (litCache.TryGetValue((baseColor, emission), out var m)) return m;
            m = new Material(litTemplate);
            m.SetColor("_BaseColor", baseColor);
            m.SetColor("_EmissionColor", emission);
            m.SetFloat("_Smoothness", 0.55f);
            litCache[(baseColor, emission)] = m;
            return m;
        }

        /// <summary>Opaque unlit HDR color; values above 1 bloom.</summary>
        public static Material Unlit(Color hdr)
        {
            if (unlitCache.TryGetValue(hdr, out var m)) return m;
            m = new Material(unlitTemplate);
            m.SetColor("_BaseColor", hdr);
            unlitCache[hdr] = m;
            return m;
        }

        /// <summary>New transparent material instance (callers animate its color).</summary>
        public static Material Transparent(Texture tex, Color color)
        {
            var m = new Material(transparentTemplate);
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", color);
            return m;
        }

        public static Color Hdr(Color c, float intensity) => new Color(c.r * intensity, c.g * intensity, c.b * intensity, 1f);

        public static MeshRenderer AddMesh(GameObject go, Mesh mesh, Material mat, bool shadows = true)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = shadows;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return r;
        }

        public static GameObject Part(string name, Transform parent, Mesh mesh, Material mat, Vector3 localPos, Vector3 localScale, bool shadows = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            AddMesh(go, mesh, mat, shadows);
            return go;
        }

        static Mesh PrimitiveMesh(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            return mesh;
        }

        /// <summary>Flat-shaded mesh from a convex hull; triangle winding is fixed up to face outward.</summary>
        public static Mesh ConvexMesh(string name, Vector3[] v, int[] tris, Vector3? interior = null)
        {
            var center = Vector3.zero;
            foreach (var p in v) center += p;
            center = interior ?? center / v.Length;
            var verts = new Vector3[tris.Length];
            var idx = new int[tris.Length];
            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 a = v[tris[i]], b = v[tris[i + 1]], c = v[tris[i + 2]];
                var n = Vector3.Cross(b - a, c - a);
                // Unity front faces are clockwise when viewed from outside: normal = cross(b-a, c-a) must point outward.
                if (Vector3.Dot(n, (a + b + c) / 3f - center) < 0) (b, c) = (c, b);
                verts[i] = a; verts[i + 1] = b; verts[i + 2] = c;
                idx[i] = i; idx[i + 1] = i + 1; idx[i + 2] = i + 2;
            }
            var mesh = new Mesh { name = name, vertices = verts, triangles = idx };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static readonly Dictionary<(int, float, float, float), Mesh> prismCache = new();

        /// <summary>
        /// Flat-shaded faceted prism along Y (height 1, centered): a regular polygon with separate bottom/top radii.
        /// top = 0 gives a pyramid. Scale the transform for boxy or elongated parts.
        /// </summary>
        public static Mesh Prism(int sides, float bottom, float top, float rotationDeg = 0f)
        {
            var key = (sides, bottom, top, rotationDeg);
            if (prismCache.TryGetValue(key, out var cached)) return cached;
            var v = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i < sides; i++)
            {
                float a = (i * 360f / sides + rotationDeg) * Mathf.Deg2Rad;
                v.Add(new Vector3(Mathf.Cos(a) * bottom, -0.5f, Mathf.Sin(a) * bottom));
                v.Add(new Vector3(Mathf.Cos(a) * Mathf.Max(top, 1e-4f), 0.5f, Mathf.Sin(a) * Mathf.Max(top, 1e-4f)));
            }
            int bc = v.Count; v.Add(new Vector3(0, -0.5f, 0));
            int tc = v.Count; v.Add(new Vector3(0, 0.5f, 0));
            for (int i = 0; i < sides; i++)
            {
                int b0 = i * 2, t0 = i * 2 + 1, b1 = (i + 1) % sides * 2, t1 = b1 + 1;
                tris.AddRange(new[] { b0, t0, b1, b1, t0, t1, bc, b0, b1, tc, t1, t0 });
            }
            var mesh = ConvexMesh($"Prism{sides}", v.ToArray(), tris.ToArray(), Vector3.zero);
            prismCache[key] = mesh;
            return mesh;
        }

        /// <summary>Places a part so its local Y axis spans from a to b (in parent space).</summary>
        public static GameObject Limb(string name, Transform parent, Mesh mesh, Material mat, Vector3 a, Vector3 b, float thickness)
        {
            var go = Part(name, parent, mesh, mat, (a + b) * 0.5f, new Vector3(thickness, (b - a).magnitude, thickness));
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, b - a);
            return go;
        }

        static Texture2D RadialTexture(int size, System.Func<float, float> alpha)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                float a = Mathf.Clamp01(alpha(Mathf.Sqrt(dx * dx + dy * dy)));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        static Texture2D RoundedTexture(int size, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        /// <summary>One tile = 8m: minor lines every 2m, bright major line on the border.</summary>
        static Texture2D GridTexture()
        {
            const int size = 256;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 8, filterMode = FilterMode.Trilinear };
            var px = new Color[size * size];
            var baseCol = new Color(0.035f, 0.04f, 0.075f);
            var minor = new Color(0.08f, 0.22f, 0.32f);
            var major = new Color(0.2f, 0.75f, 1f);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int mx = x % 64, my = y % 64;
                bool isMajor = x < 2 || y < 2;
                bool isMinor = mx < 1 || my < 1;
                px[y * size + x] = isMajor ? major : isMinor ? minor : baseCol;
            }
            tex.SetPixels(px);
            tex.Apply(true, true);
            return tex;
        }

        static Texture2D LineTexture()
        {
            var tex = new Texture2D(4, 32, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[4 * 32];
            for (int y = 0; y < 32; y++)
            {
                float v = Mathf.Abs((y + 0.5f) / 32f * 2f - 1f);
                byte a = (byte)(Mathf.Pow(1f - v, 1.5f) * 255);
                for (int x = 0; x < 4; x++) px[y * 4 + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
