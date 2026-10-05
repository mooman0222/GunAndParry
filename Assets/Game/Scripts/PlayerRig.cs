using UnityEngine;

namespace GunAndParry
{
    /// <summary>
    /// Procedural model + animation for the player android.
    /// Legs face the movement direction, the upper body faces the aim (twin-stick split),
    /// with a stride cycle, recoil, dash and parry poses, and animated emissive accents.
    /// </summary>
    public sealed class PlayerRig
    {
        static readonly Color Cyan = new(0.25f, 0.95f, 1f);
        const float HipHeight = 0.74f, ThighLength = 0.36f, ShinLength = 0.36f;
        // Larger than life so the silhouette reads from the top-down camera.
        const float ModelScale = 1.3f;

        public readonly Transform Model;
        public readonly Transform Muzzle;
        public readonly MeshRenderer[] Renderers;

        readonly Transform legs, upper, head, gun, thighL, thighR, kneeL, kneeR;
        readonly Material visorMat, coreMat, thrusterMat;
        readonly TrailRenderer scarf;
        readonly Vector3 gunRest;

        float phase, kick, dashBlend, parryT = 1f, speedSmoothed;
        Quaternion legsYaw = Quaternion.identity, upperYaw = Quaternion.identity;

        public PlayerRig(Transform root)
        {
            var dark = Art.Lit(new Color(0.07f, 0.08f, 0.1f), Color.black);
            var armor = Art.Lit(new Color(0.2f, 0.22f, 0.27f), Color.black);
            var plate = Art.Lit(new Color(0.82f, 0.86f, 0.92f), new Color(0.05f, 0.07f, 0.09f));
            var hot = Art.Unlit(Art.Hdr(Cyan, 2.6f));
            visorMat = new Material(Art.Unlit(Art.Hdr(Cyan, 3f)));
            coreMat = new Material(Art.Unlit(Art.Hdr(Cyan, 3f)));
            thrusterMat = new Material(Art.Unlit(Art.Hdr(Cyan, 2f)));

            var box = Art.Prism(4, 0.7071f, 0.7071f, 45f);       // unit cube along Y
            var hex = Art.Prism(6, 0.5f, 0.5f);
            var taper = Art.Prism(6, 0.42f, 0.5f, 30f);          // wider at the top (chest, shins)
            var helmet = Art.Prism(8, 0.5f, 0.38f, 22.5f);
            var cyl = Art.Prism(8, 0.5f, 0.5f, 22.5f);

            Model = new GameObject("Model").transform;
            Model.SetParent(root, false);
            Model.localScale = Vector3.one * ModelScale;

            // ---------------- Legs (face movement) ----------------
            legs = Node("Legs", Model, Vector3.zero);
            Art.Part("Pelvis", legs, box, armor, new Vector3(0, HipHeight + 0.04f, 0), new Vector3(0.42f, 0.18f, 0.28f));
            Art.Part("Belt", legs, box, hot, new Vector3(0, HipHeight + 0.1f, 0.125f), new Vector3(0.22f, 0.03f, 0.01f), false);
            (thighL, kneeL) = Leg(legs, -1, armor, plate, dark, hex, taper, box);
            (thighR, kneeR) = Leg(legs, 1, armor, plate, dark, hex, taper, box);

            // ---------------- Upper body (faces aim) ----------------
            upper = Node("Upper", Model, new Vector3(0, HipHeight + 0.1f, 0));
            Art.Part("Abdomen", upper, hex, dark, new Vector3(0, 0.08f, 0), new Vector3(0.32f, 0.16f, 0.26f));
            Art.Part("Chest", upper, taper, plate, new Vector3(0, 0.36f, 0.01f), new Vector3(0.66f, 0.36f, 0.44f));
            Art.Part("ChestTrim", upper, box, armor, new Vector3(0, 0.25f, 0.13f), new Vector3(0.3f, 0.06f, 0.06f));
            Art.Part("Core", upper, Art.Octa, coreMat, new Vector3(0, 0.38f, 0.19f), new Vector3(0.12f, 0.12f, 0.08f), false);
            Art.Part("Collar", upper, hex, dark, new Vector3(0, 0.55f, 0), new Vector3(0.22f, 0.06f, 0.2f));
            foreach (int side in new[] { -1, 1 })
            {
                var pad = Art.Part("Shoulder", upper, taper, plate, new Vector3(side * 0.38f, 0.48f, 0), new Vector3(0.26f, 0.17f, 0.3f));
                pad.transform.localRotation = Quaternion.Euler(0, 0, -side * 18f);
                Art.Part("ShoulderLight", upper, box, hot, new Vector3(side * 0.47f, 0.46f, 0.05f), new Vector3(0.02f, 0.035f, 0.18f), false);
            }

            // Backpack with twin thrusters
            Art.Part("Pack", upper, box, armor, new Vector3(0, 0.36f, -0.25f), new Vector3(0.42f, 0.38f, 0.16f));
            Art.Part("PackStripe", upper, box, hot, new Vector3(0, 0.44f, -0.335f), new Vector3(0.24f, 0.03f, 0.01f), false);
            foreach (int side in new[] { -1, 1 })
            {
                Art.Part("Nozzle", upper, cyl, dark, new Vector3(side * 0.12f, 0.15f, -0.29f), new Vector3(0.11f, 0.1f, 0.11f));
                Art.Part("Flame", upper, Art.Sphere, thrusterMat, new Vector3(side * 0.12f, 0.09f, -0.29f), Vector3.one * 0.09f, false);
            }

            // Head
            head = Node("Head", upper, new Vector3(0, 0.6f, 0));
            Art.Part("Neck", head, hex, dark, new Vector3(0, 0.02f, 0), new Vector3(0.1f, 0.08f, 0.1f));
            Art.Part("Helmet", head, helmet, plate, new Vector3(0, 0.17f, 0), new Vector3(0.33f, 0.27f, 0.35f));
            Art.Part("Visor", head, box, visorMat, new Vector3(0, 0.18f, 0.16f), new Vector3(0.26f, 0.065f, 0.04f), false);
            Art.Part("Crest", head, box, armor, new Vector3(0, 0.31f, -0.02f), new Vector3(0.05f, 0.05f, 0.28f));
            Art.Limb("Antenna", head, cyl, dark, new Vector3(0.11f, 0.2f, -0.06f), new Vector3(0.15f, 0.45f, -0.12f), 0.018f);
            Art.Part("AntennaTip", head, Art.Sphere, hot, new Vector3(0.15f, 0.46f, -0.12f), Vector3.one * 0.04f, false);
            var scarfAnchor = Node("ScarfAnchor", head, new Vector3(0, 0.05f, -0.12f));
            scarf = scarfAnchor.gameObject.AddComponent<TrailRenderer>();
            scarf.sharedMaterial = Art.Trail;
            scarf.time = 0.3f;
            scarf.widthMultiplier = 0.14f;
            scarf.widthCurve = AnimationCurve.Linear(0, 1, 1, 0.2f);
            scarf.minVertexDistance = 0.05f;
            scarf.startColor = Art.Hdr(Cyan, 1.4f);
            scarf.endColor = new Color(Cyan.r, Cyan.g, Cyan.b, 0f);
            scarf.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // Rifle (held two-handed); forearms ride on the gun so recoil moves them together.
            gunRest = new Vector3(0.14f, 0.3f, 0.26f);
            gun = Node("Gun", upper, gunRest);
            Art.Part("Receiver", gun, box, armor, new Vector3(0, 0, 0.12f), new Vector3(0.11f, 0.15f, 0.42f));
            Art.Part("Rail", gun, box, plate, new Vector3(0, 0.09f, 0.14f), new Vector3(0.06f, 0.04f, 0.32f));
            Art.Part("Barrel", gun, cyl, dark, new Vector3(0, 0.01f, 0.48f), new Vector3(0.06f, 0.36f, 0.06f)).transform.localRotation = Quaternion.Euler(90, 0, 0);
            Art.Part("Shroud", gun, hex, armor, new Vector3(0, 0.01f, 0.4f), new Vector3(0.09f, 0.14f, 0.09f)).transform.localRotation = Quaternion.Euler(90, 0, 0);
            Art.Part("MuzzleRing", gun, cyl, hot, new Vector3(0, 0.01f, 0.67f), new Vector3(0.075f, 0.025f, 0.075f), false).transform.localRotation = Quaternion.Euler(90, 0, 0);
            Art.Part("Cell", gun, box, hot, new Vector3(0.058f, 0.01f, 0.12f), new Vector3(0.01f, 0.05f, 0.26f), false);
            Art.Part("Cell", gun, box, hot, new Vector3(-0.058f, 0.01f, 0.12f), new Vector3(0.01f, 0.05f, 0.26f), false);
            Art.Part("Mag", gun, box, dark, new Vector3(0, -0.13f, 0.1f), new Vector3(0.07f, 0.16f, 0.1f)).transform.localRotation = Quaternion.Euler(-12, 0, 0);
            Art.Part("Stock", gun, box, armor, new Vector3(0, -0.02f, -0.14f), new Vector3(0.08f, 0.12f, 0.14f));
            Muzzle = Node("Muzzle", gun, new Vector3(0, 0.01f, 0.72f));
            // Arms: upper arms hang from the shoulders toward the gun; forearms attach to the gun.
            Arm(upper, gun, 1, new Vector3(0.36f, 0.43f, 0f), new Vector3(0.24f, 0.24f, 0.1f), new Vector3(0, -0.04f, -0.02f), armor, plate, dark, hex, box);
            Arm(upper, gun, -1, new Vector3(-0.36f, 0.43f, 0f), new Vector3(-0.2f, 0.22f, 0.22f), new Vector3(-0.02f, -0.03f, 0.36f), armor, plate, dark, hex, box);

            Renderers = Model.GetComponentsInChildren<MeshRenderer>();
        }

        static Transform Node(string name, Transform parent, Vector3 pos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            return t;
        }

        static (Transform thigh, Transform knee) Leg(Transform legs, int side, Material armor, Material plate, Material dark, Mesh hex, Mesh taper, Mesh box)
        {
            var hip = Node(side < 0 ? "HipL" : "HipR", legs, new Vector3(side * 0.15f, HipHeight, 0));
            Art.Part("Joint", hip, Art.Sphere, dark, Vector3.zero, Vector3.one * 0.13f);
            Art.Part("Thigh", hip, hex, armor, new Vector3(0, -ThighLength * 0.5f, 0), new Vector3(0.2f, ThighLength, 0.22f));
            Art.Part("ThighPlate", hip, box, plate, new Vector3(side * 0.02f, -ThighLength * 0.4f, 0.1f), new Vector3(0.14f, 0.22f, 0.05f));
            var knee = Node("Knee", hip, new Vector3(0, -ThighLength, 0));
            Art.Part("KneeGuard", knee, box, plate, new Vector3(0, 0.01f, 0.1f), new Vector3(0.15f, 0.12f, 0.07f));
            var shin = Art.Part("Shin", knee, taper, armor, new Vector3(0, -ShinLength * 0.5f, 0), new Vector3(0.2f, ShinLength, 0.22f));
            shin.transform.localRotation = Quaternion.Euler(180, 0, 0);   // flare toward the knee
            Art.Part("Foot", knee, box, dark, new Vector3(0, -ShinLength - 0.01f, 0.06f), new Vector3(0.17f, 0.08f, 0.32f));
            Art.Part("ShinLight", knee, box, Art.Unlit(Art.Hdr(Cyan, 2.2f)), new Vector3(0, -ShinLength * 0.45f, 0.111f), new Vector3(0.03f, ShinLength * 0.5f, 0.01f), false);
            Art.Part("Toe", knee, box, plate, new Vector3(0, -ShinLength + 0.01f, 0.19f), new Vector3(0.15f, 0.05f, 0.07f));
            return (hip, knee);
        }

        static void Arm(Transform upper, Transform gun, int side, Vector3 shoulder, Vector3 elbow, Vector3 handOnGun,
            Material armor, Material plate, Material dark, Mesh hex, Mesh box)
        {
            Art.Part("ShoulderJoint", upper, Art.Sphere, dark, shoulder, Vector3.one * 0.15f);
            Art.Limb("UpperArm", upper, hex, armor, shoulder, elbow, 0.14f);
            Art.Part("Elbow", upper, Art.Sphere, dark, elbow, Vector3.one * 0.1f);
            // Forearm lives under the gun: compute the elbow in gun space at rest.
            var elbowInGun = elbow - new Vector3(0.14f, 0.3f, 0.26f);
            Art.Limb("Forearm", gun, hex, plate, elbowInGun, handOnGun, 0.13f);
            Art.Part("Hand", gun, box, dark, handOnGun, new Vector3(0.08f, 0.08f, 0.1f));
        }

        public void Kick(float amount) => kick = Mathf.Min(1.5f, kick + amount);

        public void ParryStart() => parryT = 0f;

        public void Animate(float dt, Vector3 velocity, Vector3 aim, bool dashing, bool overdrive, float time)
        {
            velocity.y = 0;
            float speed = velocity.magnitude;
            speedSmoothed = Mathf.Lerp(speedSmoothed, dashing ? 0 : speed, 1f - Mathf.Exp(-12f * dt));
            float amt = Mathf.Clamp01(speedSmoothed / 5.6f);
            dashBlend = Mathf.MoveTowards(dashBlend, dashing ? 1f : 0f, dt * (dashing ? 14f : 5f));
            parryT = Mathf.Min(1f, parryT + dt / 0.22f);
            kick *= Mathf.Exp(-16f * dt);

            // Legs follow movement; walking backward plays the cycle in reverse instead of turning around.
            var legDir = aim;
            float dirSign = 1f;
            if (speed > 0.3f)
            {
                legDir = velocity / speed;
                if (Vector3.Dot(legDir, aim) < -0.25f) { legDir = -legDir; dirSign = -1f; }
            }
            legsYaw = Quaternion.Slerp(legsYaw, Quaternion.LookRotation(legDir), 1f - Mathf.Exp(-12f * dt));
            upperYaw = Quaternion.Slerp(upperYaw, Quaternion.LookRotation(aim), 1f - Mathf.Exp(-25f * dt));
            phase += dt * speedSmoothed * 4.4f * dirSign;

            float swing = 38f * amt;
            float sL = Mathf.Sin(phase), sR = Mathf.Sin(phase + Mathf.PI);
            float tuck = dashBlend * 45f;
            thighL.localRotation = Quaternion.Euler(-sL * swing - tuck, 0, 0);
            thighR.localRotation = Quaternion.Euler(-sR * swing + tuck * 0.4f, 0, 0);
            kneeL.localRotation = Quaternion.Euler(Mathf.Max(0, Mathf.Cos(phase)) * 55f * amt + 6f + dashBlend * 70f, 0, 0);
            kneeR.localRotation = Quaternion.Euler(Mathf.Max(0, Mathf.Cos(phase + Mathf.PI)) * 55f * amt + 6f + dashBlend * 30f, 0, 0);

            float bob = Mathf.Abs(Mathf.Cos(phase)) * 0.045f * amt + Mathf.Sin(time * 2.2f) * 0.008f;
            Model.localPosition = new Vector3(0, (bob - dashBlend * 0.08f) * ModelScale, 0);
            legs.localRotation = legsYaw;

            // Upper body: aim, lean into movement/dash, recoil, and a quick twist when parrying.
            float lean = 6f * amt + dashBlend * 22f - kick * 5f;
            float sway = Mathf.Sin(phase) * 3f * amt;
            float parrySwing = Mathf.Sin(parryT * Mathf.PI) * (1f - parryT * 0.3f);
            upper.localRotation = upperYaw * Quaternion.Euler(lean, -parrySwing * 30f, sway);
            head.localRotation = Quaternion.Euler(-lean * 0.5f, parrySwing * 15f, -sway * 0.5f);
            gun.localPosition = gunRest + new Vector3(0, parrySwing * 0.06f, -kick * 0.08f);
            gun.localRotation = Quaternion.Euler(-kick * 8f - parrySwing * 25f, parrySwing * 20f, 0);

            // Emissive accents
            var accent = overdrive ? new Color(1f, 0.4f, 1f) : Cyan;
            float pulse = 0.85f + Mathf.Sin(time * 4f) * 0.15f;
            visorMat.SetColor("_BaseColor", Art.Hdr(Color.Lerp(accent, Color.white, parrySwing), 2.6f + parrySwing * 3f));
            coreMat.SetColor("_BaseColor", Art.Hdr(accent, (2.2f + kick * 2f) * pulse));
            thrusterMat.SetColor("_BaseColor", Art.Hdr(accent, 1.2f + amt * 1.5f + dashBlend * 6f));
            scarf.startColor = Art.Hdr(accent, 1.4f);
            scarf.endColor = new Color(accent.r, accent.g, accent.b, 0f);
        }

        public void Snap(Vector3 aim)
        {
            legsYaw = upperYaw = Quaternion.LookRotation(aim);
            scarf.Clear();
        }
    }
}
