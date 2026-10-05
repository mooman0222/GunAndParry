using System.Collections.Generic;
using UnityEngine;

namespace GunAndParry
{
    /// <summary>Final player numbers derived from meta progression + in-run passives.</summary>
    public sealed class PlayerStats
    {
        public float MaxHp = 100f;
        public float MoveSpeed = 5.6f;
        public float DamageMult = 1f;
        public float CooldownMult = 1f;
        public float PickupRadius = 2.6f;
        public float Armor;
        public float Regen;
        public float Crit = 0.05f;
        public float CritMult = 2f;
        public float XpMult = 1f;
        public float GoldMult = 1f;
        public int Amount;

        public float ParryWindow = 0.2f;
        public float ParryRadius = 2.6f;
        public float ParryCooldown = 0.75f;
        public float ReflectMult = 3f;
        public int ReflectPierce;
        public float NovaDamage, NovaRadius;
        public float LeechHeal;
        public float OverdriveGain = 1f;
        public float OverdriveDuration = 6f;

        public int Rerolls;
        public int Revives;

        public static PlayerStats Compute(Loadout l, SaveData save)
        {
            var s = new PlayerStats();
            int M(MetaId id) => save.Meta(id);
            int P(PassiveId id) => l.PassiveLevels[(int)id];

            s.DamageMult = 1f + 0.05f * M(MetaId.Might) + 0.10f * P(PassiveId.Power);
            s.MaxHp = 100f + 10f * M(MetaId.Vitality) + 20f * P(PassiveId.Plating);
            s.MoveSpeed = 5.6f * (1f + 0.04f * M(MetaId.Agility) + 0.08f * P(PassiveId.Servo));
            s.PickupRadius = 2.6f * (1f + 0.15f * M(MetaId.Magnetism) + 0.30f * P(PassiveId.Magnet));
            s.ParryWindow = 0.2f + 0.02f * M(MetaId.Reflexes) + 0.04f * P(PassiveId.Reflex);
            s.ParryRadius = 2.6f * (1f + 0.10f * P(PassiveId.Reflex));
            s.CooldownMult = (1f - 0.03f * M(MetaId.Haste)) * Mathf.Pow(0.92f, P(PassiveId.Trigger));
            s.GoldMult = 1f + 0.10f * M(MetaId.Greed);
            s.XpMult = 1f + 0.06f * M(MetaId.Growth);
            s.Rerolls = 1 + M(MetaId.Reroll);
            s.Revives = M(MetaId.Revival);

            s.Amount = P(PassiveId.Split);
            s.Armor = P(PassiveId.Armor);
            s.Regen = 0.3f * P(PassiveId.Nanorepair);
            s.Crit = 0.05f + 0.06f * P(PassiveId.CritLens);
            s.ReflectMult = 3f * (1f + 0.6f * P(PassiveId.MirrorCoat));
            s.ReflectPierce = P(PassiveId.MirrorCoat);
            int nova = P(PassiveId.CounterNova);
            s.NovaDamage = nova > 0 ? 15f + 12f * nova : 0f;
            s.NovaRadius = 3f + 0.5f * nova;
            s.LeechHeal = 2f * P(PassiveId.LeechParry);
            s.OverdriveGain = 1f + 0.25f * P(PassiveId.Overclock);
            s.OverdriveDuration = 6f + P(PassiveId.Overclock);
            return s;
        }
    }

    /// <summary>What the player owns in the current run.</summary>
    public sealed class Loadout
    {
        public readonly List<Weapon> Weapons = new();
        public readonly int[] PassiveLevels = new int[GameData.Passives.Length];

        public int PassiveCount
        {
            get { int c = 0; foreach (var l in PassiveLevels) if (l > 0) c++; return c; }
        }

        public int Level(UpgradeDef d)
        {
            if (!d.IsWeapon) return PassiveLevels[d.Id];
            foreach (var w in Weapons) if ((int)w.Id == d.Id) return w.Level;
            return 0;
        }

        public Weapon Find(WeaponId id)
        {
            foreach (var w in Weapons) if (w.Id == id) return w;
            return null;
        }

        public bool CanEvolve(EvolutionDef e)
        {
            var w = Find(e.Weapon);
            return w != null && !w.Evolved && w.Level >= w.Def.MaxLevel && PassiveLevels[(int)e.Requires] > 0;
        }

        public void Evolve(EvolutionDef e) => Find(e.Weapon).Evolved = true;

        public bool CanOffer(UpgradeDef d)
        {
            int lv = Level(d);
            if (lv >= d.MaxLevel) return false;
            if (lv > 0) return true;
            return d.IsWeapon ? Weapons.Count < GameData.MaxWeapons : PassiveCount < GameData.MaxPassives;
        }

        public void Apply(UpgradeDef d)
        {
            if (!d.IsWeapon) { PassiveLevels[d.Id]++; return; }
            foreach (var w in Weapons)
                if ((int)w.Id == d.Id) { w.Level++; return; }
            Weapons.Add(Weapon.Create((WeaponId)d.Id));
        }
    }

    public enum FallbackReward { None, Heal, Gold }

    public struct UpgradeChoice
    {
        public UpgradeDef Def;
        public int NextLevel;
        public FallbackReward Fallback;
        public EvolutionDef Evolution;

        /// <summary>What to show on cards and icons (the evolved form for evolutions).</summary>
        public UpgradeDef Display => Evolution != null ? Evolution.Def : Def;

        public string Title => Fallback switch
        {
            FallbackReward.Heal => "Repair Kit",
            FallbackReward.Gold => "Gold Cache",
            _ => Display.Name,
        };

        public string Description => Fallback switch
        {
            FallbackReward.Heal => "Restore 30 HP.",
            FallbackReward.Gold => "Gain 25 gold.",
            _ => Evolution != null ? Evolution.Def.LevelDesc[0] : Def.LevelDesc[NextLevel - 1],
        };
    }

    public static class UpgradePicker
    {
        /// <summary>
        /// Picks up to <paramref name="count"/> distinct offers. Ready evolutions are always offered first;
        /// the remaining slots are weighted random. Falls back to heal/gold when nothing is left.
        /// </summary>
        public static List<UpgradeChoice> Roll(Loadout loadout, int count, System.Random rng)
        {
            var result = new List<UpgradeChoice>();
            foreach (var e in GameData.Evolutions)
                if (loadout.CanEvolve(e) && result.Count < count) result.Add(new UpgradeChoice { Def = GameData.Weapon(e.Weapon), Evolution = e });

            var pool = new List<(UpgradeChoice choice, float weight)>();
            foreach (var d in GameData.Weapons)
                if (loadout.CanOffer(d)) pool.Add((new UpgradeChoice { Def = d, NextLevel = loadout.Level(d) + 1 }, loadout.Level(d) > 0 ? 1.3f : 1f));
            foreach (var d in GameData.Passives)
                if (loadout.CanOffer(d)) pool.Add((new UpgradeChoice { Def = d, NextLevel = loadout.Level(d) + 1 }, loadout.Level(d) > 0 ? 1.1f : 0.9f));

            while (result.Count < count && pool.Count > 0)
            {
                float total = 0;
                foreach (var p in pool) total += p.weight;
                float r = (float)rng.NextDouble() * total;
                int pick = pool.Count - 1;
                for (int i = 0; i < pool.Count; i++)
                {
                    r -= pool[i].weight;
                    if (r <= 0) { pick = i; break; }
                }
                result.Add(pool[pick].choice);
                pool.RemoveAt(pick);
            }
            if (result.Count == 0)
            {
                result.Add(new UpgradeChoice { Fallback = FallbackReward.Heal });
                result.Add(new UpgradeChoice { Fallback = FallbackReward.Gold });
            }
            return result;
        }
    }
}
