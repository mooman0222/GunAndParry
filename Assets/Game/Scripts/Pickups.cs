using System.Collections.Generic;
using UnityEngine;

namespace GunAndParry
{
    public enum PickupType { XpSmall, XpMedium, XpLarge, Gold, Heal, Magnet, Bomb, Chest }

    public sealed class Pickup
    {
        public PickupType Type;
        public Transform T;
        public Vector3 Pos;
        public int Value;
        public bool Attracted;
        public float Speed, Phase;
    }

    public sealed class PickupManager
    {
        const int GemCap = 300;
        readonly Transform root;
        readonly List<Pickup> active = new();
        readonly Dictionary<PickupType, Stack<Pickup>> pools = new();
        int gemCount;
        float pickupCombo, lastPickup;

        public PickupManager(Transform parent)
        {
            root = new GameObject("Pickups").transform;
            root.SetParent(parent, false);
        }

        public void DropFor(Enemy e)
        {
            var pos = e.Pos;
            if (GameData.IsBoss(e.Kind)) return;
            if (e.Elite)
            {
                Spawn(PickupType.Chest, pos, 1);
                Spawn(PickupType.XpLarge, pos + Jitter(1f), 25);
                for (int i = 0; i < 5; i++) Spawn(PickupType.Gold, pos + Jitter(1.5f), 3);
                return;
            }
            int xp = e.Def.Xp;
            Spawn(xp >= 25 ? PickupType.XpLarge : xp >= 5 ? PickupType.XpMedium : PickupType.XpSmall, pos, xp);

            float r = Random.value;
            if (r < 0.06f) Spawn(PickupType.Gold, pos + Jitter(0.6f), Random.Range(1, 4));
            else if (r < 0.068f) Spawn(PickupType.Heal, pos, 0);
            else if (r < 0.0705f) Spawn(PickupType.Magnet, pos, 0);
            else if (r < 0.0725f) Spawn(PickupType.Bomb, pos, 0);
        }

        static Vector3 Jitter(float r) { var v = Random.insideUnitCircle * r; return new Vector3(v.x, 0, v.y); }

        public void Spawn(PickupType type, Vector3 pos, int value)
        {
            bool isGem = type <= PickupType.XpLarge;
            if (isGem && gemCount >= GemCap && MergeIntoNearestGem(pos, value)) return;

            if (!pools.TryGetValue(type, out var pool)) pools[type] = pool = new Stack<Pickup>();
            var p = pool.Count > 0 ? pool.Pop() : Create(type);
            p.Pos = new Vector3(pos.x, 0, pos.z);
            p.Value = value;
            p.Attracted = false;
            p.Speed = 0;
            p.Phase = Random.value * 10f;
            p.T.gameObject.SetActive(true);
            p.T.position = p.Pos;
            active.Add(p);
            if (isGem) gemCount++;
        }

        bool MergeIntoNearestGem(Vector3 pos, int value)
        {
            Pickup best = null;
            float bestSq = float.MaxValue;
            foreach (var p in active)
            {
                if (p.Type > PickupType.XpLarge) continue;
                float sq = (p.Pos - pos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = p; }
            }
            if (best == null) return false;
            best.Value += value;
            return true;
        }

        Pickup Create(PickupType type)
        {
            var go = new GameObject(type.ToString());
            go.transform.SetParent(root, false);
            var t = go.transform;
            switch (type)
            {
                case PickupType.XpSmall:
                    Art.Part("Gem", t, Art.Gem, Art.Unlit(new Color(0.3f, 0.75f, 3f)), new Vector3(0, 0.45f, 0), Vector3.one * 0.6f, false);
                    break;
                case PickupType.XpMedium:
                    Art.Part("Gem", t, Art.Gem, Art.Unlit(new Color(0.4f, 3f, 0.8f)), new Vector3(0, 0.5f, 0), Vector3.one * 0.85f, false);
                    break;
                case PickupType.XpLarge:
                    Art.Part("Gem", t, Art.Gem, Art.Unlit(new Color(3.5f, 0.4f, 0.6f)), new Vector3(0, 0.6f, 0), Vector3.one * 1.2f, false);
                    break;
                case PickupType.Gold:
                    Art.Part("Coin", t, Art.Cylinder, Art.Lit(new Color(0.6f, 0.45f, 0.1f), new Color(2.2f, 1.6f, 0.3f)), new Vector3(0, 0.45f, 0), new Vector3(0.45f, 0.05f, 0.45f), false)
                        .transform.localRotation = Quaternion.Euler(90, 0, 0);
                    break;
                case PickupType.Heal:
                    var heal = Art.Unlit(new Color(0.5f, 3f, 0.8f));
                    Art.Part("CrossA", t, Art.Cube, heal, new Vector3(0, 0.6f, 0), new Vector3(0.7f, 0.22f, 0.22f), false);
                    Art.Part("CrossB", t, Art.Cube, heal, new Vector3(0, 0.6f, 0), new Vector3(0.22f, 0.7f, 0.22f), false);
                    break;
                case PickupType.Magnet:
                    Art.Part("Core", t, Art.Octa, Art.Lit(new Color(0.1f, 0.2f, 0.3f), new Color(0.6f, 1.8f, 3f)), new Vector3(0, 0.65f, 0), Vector3.one * 0.8f, false);
                    break;
                case PickupType.Bomb:
                    Art.Part("Core", t, Art.Sphere, Art.Lit(new Color(0.08f, 0.08f, 0.1f), new Color(0.3f, 0f, 0f)), new Vector3(0, 0.55f, 0), Vector3.one * 0.75f, false);
                    Art.Part("Fuse", t, Art.Sphere, Art.Unlit(new Color(4f, 0.6f, 0.2f)), new Vector3(0, 0.98f, 0), Vector3.one * 0.18f, false);
                    break;
                case PickupType.Chest:
                    // Lit gold with only a little self-glow so it keeps its shading; a small bright lock is the eye-catcher.
                    var gold = Art.Lit(new Color(0.72f, 0.52f, 0.14f), new Color(0.4f, 0.28f, 0.06f));
                    var band = Art.Lit(new Color(0.16f, 0.12f, 0.1f), Color.black);
                    Art.Part("Box", t, Art.Cube, gold, new Vector3(0, 0.33f, 0), new Vector3(1.1f, 0.66f, 0.8f));
                    Art.Part("Lid", t, Art.Cube, gold, new Vector3(0, 0.76f, 0), new Vector3(1.16f, 0.22f, 0.86f));
                    foreach (int side in new[] { -1, 1 })
                        Art.Part("Band", t, Art.Cube, band, new Vector3(side * 0.32f, 0.47f, 0), new Vector3(0.12f, 0.92f, 0.88f));
                    Art.Part("Lock", t, Art.Cube, Art.Unlit(new Color(2f, 1.5f, 0.45f)), new Vector3(0, 0.6f, 0.43f), new Vector3(0.16f, 0.2f, 0.06f), false);
                    break;
            }
            go.SetActive(false);
            return new Pickup { Type = type, T = t };
        }

        public Vector3? Nearest(Vector3 pos, float maxDist)
        {
            Vector3? best = null;
            float bestSq = maxDist * maxDist;
            foreach (var p in active)
            {
                float sq = (p.Pos - pos).sqrMagnitude;
                if (sq < bestSq) { bestSq = sq; best = p.Pos; }
            }
            return best;
        }

        public void AttractAllGems()
        {
            foreach (var p in active) if (p.Type <= PickupType.XpLarge || p.Type == PickupType.Gold) p.Attracted = true;
        }

        public void Tick(float dt)
        {
            var player = Game.I.Player;
            var ppos = player.Pos;
            float radius = player.Stats.PickupRadius;
            float r2 = radius * radius;
            float time = Game.Now;

            for (int i = active.Count - 1; i >= 0; i--)
            {
                var p = active[i];
                var d = ppos - p.Pos; d.y = 0;
                float sq = d.sqrMagnitude;
                bool magnetic = p.Type != PickupType.Chest;
                if (!p.Attracted && magnetic && sq < r2) p.Attracted = true;

                if (p.Attracted)
                {
                    p.Speed = Mathf.Min(p.Speed + 40f * dt, 30f);
                    p.Pos += d.normalized * Mathf.Min(p.Speed * dt, Mathf.Sqrt(sq));
                }
                if (sq < 0.8f * 0.8f)
                {
                    Collect(p);
                    p.T.gameObject.SetActive(false);
                    pools[p.Type].Push(p);
                    active[i] = active[^1];
                    active.RemoveAt(active.Count - 1);
                    continue;
                }
                p.T.position = p.Pos + Vector3.up * (Mathf.Sin(time * 3f + p.Phase) * 0.12f);
                p.T.rotation = Quaternion.Euler(0, (time * 90f + p.Phase * 40f) % 360f, 0);
            }
        }

        void Collect(Pickup p)
        {
            var g = Game.I;
            switch (p.Type)
            {
                case PickupType.XpSmall:
                case PickupType.XpMedium:
                case PickupType.XpLarge:
                    gemCount--;
                    g.AddXp(p.Value);
                    float now = Time.unscaledTime;
                    pickupCombo = now - lastPickup < 0.4f ? Mathf.Min(pickupCombo + 1, 24) : 0;
                    lastPickup = now;
                    Sfx.Play(SfxId.Pickup, 0.35f, 1f + pickupCombo * 0.03f, 0f);
                    break;
                case PickupType.Gold:
                    g.Run.Gold += p.Value * g.Player.Stats.GoldMult;
                    Sfx.Play(SfxId.Gold, 0.4f);
                    break;
                case PickupType.Heal:
                    g.Player.Heal(g.Player.Stats.MaxHp * 0.3f);
                    break;
                case PickupType.Magnet:
                    AttractAllGems();
                    g.Fx.Ring(g.Player.Pos, new Color(0.4f, 0.8f, 1f), 1f, 30f, 0.6f);
                    Sfx.Play(SfxId.Overdrive, 0.4f, 1.6f);
                    break;
                case PickupType.Bomb:
                    g.Enemies.KillAllInRadius(g.Player.Pos, 20f, true, 400f);
                    g.Projectiles.ClearHostile();
                    g.Fx.Ring(g.Player.Pos, new Color(1f, 0.6f, 0.3f), 1f, 40f, 0.6f);
                    g.Rig.Shake(0.6f);
                    g.FlashScreen(new Color(1f, 0.8f, 0.6f, 0.6f));
                    Sfx.Play(SfxId.Explosion, 1f, 0.7f);
                    break;
                case PickupType.Chest:
                    g.OpenChest(p.Pos);
                    break;
            }
        }

        public void Clear()
        {
            foreach (var p in active) { p.T.gameObject.SetActive(false); pools[p.Type].Push(p); }
            active.Clear();
            gemCount = 0;
        }
    }
}
