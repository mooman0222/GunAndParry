using UnityEngine;

namespace GunAndParry
{
    public enum EnemyKind { Crawler, Runner, Gunner, Brute, Turret, Bomber, Boss, Colossus }
    public enum EnemyShape { Cube, Spike, Octa, Cylinder, Sphere, Boss, Colossus }

    public sealed class EnemyDef
    {
        public EnemyKind Kind;
        public string Name;
        public EnemyShape Shape;
        public float Hp, Speed, Damage, Radius;
        public int Xp;
        public Color Color;
        public float KnockbackResist;   // 0 = full knockback, 1 = immovable
        public float FireInterval;      // 0 = no ranged attack
        public float PreferredRange;    // ranged enemies stop at this distance
    }

    public enum WeaponId { Blaster, Shotgun, Drones, Railgun, Missiles, Tesla }

    public enum PassiveId
    {
        Power, Trigger, Servo, Plating, Magnet, Split, Reflex,
        CounterNova, MirrorCoat, LeechParry, Armor, Nanorepair, CritLens, Overclock
    }

    public sealed class UpgradeDef
    {
        public bool IsWeapon;
        public int Id;
        public string Name;
        public string Glyph;
        public Color Color;
        public string[] LevelDesc;    // [0] = description when acquired, [n] = description of level n+1
        public int MaxLevel => LevelDesc.Length;
    }

    /// <summary>Max-level weapon + a required passive = evolved weapon with a parry-driven effect.</summary>
    public sealed class EvolutionDef
    {
        public WeaponId Weapon;
        public PassiveId Requires;
        public UpgradeDef Def;   // name / glyph / color / description for UI
    }

    public enum MetaId { Might, Vitality, Agility, Magnetism, Reflexes, Haste, Greed, Growth, Reroll, Revival }

    public sealed class MetaDef
    {
        public MetaId Id;
        public string Name;
        public string Desc;
        public int MaxRank;
        public int BaseCost;
        public int Cost(int rank) => BaseCost * (rank + 1);
    }

    public sealed class WaveSegment
    {
        public float StartMinute;
        public float SpawnsPerSecond;
        public EnemyKind[] Kinds;
        public float[] Weights;
    }

    public enum WaveEventType { Swarm, Elite, Boss }

    public sealed class WaveEvent
    {
        public float Minute;
        public WaveEventType Type;
        public EnemyKind Kind;
        public int Count;
    }

    /// <summary>All tunable game data lives here so balancing touches a single file.</summary>
    public static class GameData
    {
        public const float RunDuration = 15f * 60f;
        public const int MaxWeapons = 5;
        public const int MaxPassives = 5;
        public const int MaxEnemies = 450;
        public const float SpawnRadius = 21f;
        public const float DespawnRadius = 34f;

        static Color Hex(string hex) { ColorUtility.TryParseHtmlString(hex, out var c); return c; }

        public static readonly EnemyDef[] Enemies =
        {
            new EnemyDef { Kind = EnemyKind.Crawler, Name = "Crawler", Shape = EnemyShape.Cube,     Hp = 10,  Speed = 2.6f, Damage = 8,  Radius = 0.45f, Xp = 1, Color = Hex("#FF3B5C") },
            new EnemyDef { Kind = EnemyKind.Runner,  Name = "Runner",  Shape = EnemyShape.Spike,    Hp = 6,   Speed = 4.7f, Damage = 6,  Radius = 0.35f, Xp = 1, Color = Hex("#FFB020") },
            new EnemyDef { Kind = EnemyKind.Gunner,  Name = "Gunner",  Shape = EnemyShape.Octa,     Hp = 18,  Speed = 2.1f, Damage = 10, Radius = 0.5f,  Xp = 2, Color = Hex("#FF4FD8"), FireInterval = 2.6f, PreferredRange = 7.5f },
            new EnemyDef { Kind = EnemyKind.Brute,   Name = "Brute",   Shape = EnemyShape.Cube,     Hp = 75,  Speed = 1.6f, Damage = 18, Radius = 0.9f,  Xp = 5, Color = Hex("#FF4D2E"), KnockbackResist = 0.75f },
            new EnemyDef { Kind = EnemyKind.Turret,  Name = "Turret",  Shape = EnemyShape.Cylinder, Hp = 45,  Speed = 1.2f, Damage = 12, Radius = 0.7f,  Xp = 4, Color = Hex("#9D5CFF"), KnockbackResist = 0.5f, FireInterval = 4f, PreferredRange = 9f },
            new EnemyDef { Kind = EnemyKind.Bomber,  Name = "Bomber",  Shape = EnemyShape.Sphere,   Hp = 14,  Speed = 3.4f, Damage = 28, Radius = 0.45f, Xp = 2, Color = Hex("#FFE14D") },
            new EnemyDef { Kind = EnemyKind.Boss,    Name = "WARDEN",  Shape = EnemyShape.Boss,     Hp = 35000, Speed = 2.2f, Damage = 25, Radius = 2.2f, Xp = 0, Color = Hex("#FF2E4D"), KnockbackResist = 1f, FireInterval = 1f, PreferredRange = 8f },
            new EnemyDef { Kind = EnemyKind.Colossus, Name = "COLOSSUS", Shape = EnemyShape.Colossus, Hp = 120000, Speed = 1.6f, Damage = 30, Radius = 3.4f, Xp = 0, Color = Hex("#FF6A2B"), KnockbackResist = 1f, PreferredRange = 7.5f },
        };

        public static EnemyDef Enemy(EnemyKind k) => Enemies[(int)k];
        public static bool IsBoss(EnemyKind k) => k == EnemyKind.Boss || k == EnemyKind.Colossus;

        /// <summary>Enemy HP multiplier by elapsed minutes. Keeps up with typical player scaling.</summary>
        public static float HpScale(float minutes) => 1f + 0.25f * minutes + 0.025f * minutes * minutes;

        public static float DamageScale(float minutes) => 1f + 0.05f * minutes;

        /// <summary>XP required to go from <paramref name="level"/> to level+1 (Vampire Survivors-like curve).</summary>
        public static int XpToNext(int level)
        {
            if (level < 20) return 5 + (level - 1) * 10;
            if (level < 40) return 200 + (level - 20) * 13;
            return 460 + (level - 40) * 16;
        }

        public static readonly WaveSegment[] Waves =
        {
            W(0,  2.2f, (EnemyKind.Crawler, 100)),
            W(1,  3.5f, (EnemyKind.Crawler, 70), (EnemyKind.Runner, 30)),
            W(2,  4.8f, (EnemyKind.Crawler, 50), (EnemyKind.Runner, 25), (EnemyKind.Gunner, 25)),
            W(4,  6.6f, (EnemyKind.Crawler, 40), (EnemyKind.Runner, 20), (EnemyKind.Gunner, 25), (EnemyKind.Bomber, 15)),
            W(6,  9.2f, (EnemyKind.Crawler, 30), (EnemyKind.Runner, 20), (EnemyKind.Gunner, 20), (EnemyKind.Bomber, 15), (EnemyKind.Brute, 15)),
            W(7,  11.0f, (EnemyKind.Crawler, 28), (EnemyKind.Runner, 18), (EnemyKind.Gunner, 18), (EnemyKind.Bomber, 14), (EnemyKind.Brute, 12), (EnemyKind.Turret, 10)),
            W(9,  13.6f, (EnemyKind.Crawler, 25), (EnemyKind.Runner, 20), (EnemyKind.Gunner, 18), (EnemyKind.Bomber, 15), (EnemyKind.Brute, 12), (EnemyKind.Turret, 10)),
            W(11, 17.2f, (EnemyKind.Crawler, 22), (EnemyKind.Runner, 20), (EnemyKind.Gunner, 18), (EnemyKind.Bomber, 16), (EnemyKind.Brute, 14), (EnemyKind.Turret, 10)),
            W(13, 20.9f, (EnemyKind.Crawler, 20), (EnemyKind.Runner, 22), (EnemyKind.Gunner, 18), (EnemyKind.Bomber, 16), (EnemyKind.Brute, 14), (EnemyKind.Turret, 10)),
            W(15, 2.0f, (EnemyKind.Crawler, 50), (EnemyKind.Runner, 50)),
        };

        public static readonly WaveEvent[] Events =
        {
            new WaveEvent { Minute = 3,  Type = WaveEventType.Swarm, Kind = EnemyKind.Runner,  Count = 40 },
            new WaveEvent { Minute = 5,  Type = WaveEventType.Elite, Kind = EnemyKind.Brute,   Count = 1 },
            new WaveEvent { Minute = 6.5f, Type = WaveEventType.Elite, Kind = EnemyKind.Gunner, Count = 1 },
            new WaveEvent { Minute = 8,  Type = WaveEventType.Swarm, Kind = EnemyKind.Crawler, Count = 60 },
            new WaveEvent { Minute = 9.5f, Type = WaveEventType.Elite, Kind = EnemyKind.Turret, Count = 2 },
            new WaveEvent { Minute = 10, Type = WaveEventType.Boss,  Kind = EnemyKind.Boss,    Count = 1 },
            new WaveEvent { Minute = 11.5f, Type = WaveEventType.Swarm, Kind = EnemyKind.Bomber, Count = 36 },
            new WaveEvent { Minute = 12.5f, Type = WaveEventType.Elite, Kind = EnemyKind.Brute, Count = 3 },
            new WaveEvent { Minute = 14, Type = WaveEventType.Swarm, Kind = EnemyKind.Runner,  Count = 80 },
            new WaveEvent { Minute = 15, Type = WaveEventType.Boss,  Kind = EnemyKind.Colossus, Count = 1 },
        };

        static WaveSegment W(float minute, float rate, params (EnemyKind kind, float weight)[] mix)
        {
            var s = new WaveSegment { StartMinute = minute, SpawnsPerSecond = rate, Kinds = new EnemyKind[mix.Length], Weights = new float[mix.Length] };
            for (int i = 0; i < mix.Length; i++) { s.Kinds[i] = mix[i].kind; s.Weights[i] = mix[i].weight; }
            return s;
        }

        public static WaveSegment WaveAt(float minutes)
        {
            var current = Waves[0];
            foreach (var w in Waves) if (minutes >= w.StartMinute) current = w;
            return current;
        }

        public static readonly UpgradeDef[] Weapons =
        {
            new UpgradeDef { IsWeapon = true, Id = (int)WeaponId.Blaster, Name = "Blaster", Glyph = "BL", Color = Hex("#38E8FF"), LevelDesc = new[] {
                "Fires toward your aim.", "+1 projectile.", "Damage +30%.", "Fire rate +20%.", "+1 pierce.", "+1 projectile.", "Damage +40%, +1 pierce." } },
            new UpgradeDef { IsWeapon = true, Id = (int)WeaponId.Shotgun, Name = "Scatter Gun", Glyph = "SG", Color = Hex("#FF9A3C"), LevelDesc = new[] {
                "Short-range blast of pellets. Heavy knockback.", "+2 pellets.", "Damage +30%.", "Cooldown -20%.", "Range +30%.", "+3 pellets.", "Damage +50%." } },
            new UpgradeDef { IsWeapon = true, Id = (int)WeaponId.Drones, Name = "Gun Drones", Glyph = "DR", Color = Hex("#7CFF6B"), LevelDesc = new[] {
                "An orbiting drone shoots the nearest enemy.", "+1 drone.", "Damage +30%.", "Fire rate +25%.", "+1 drone.", "Damage +40%.", "+1 drone." } },
            new UpgradeDef { IsWeapon = true, Id = (int)WeaponId.Railgun, Name = "Railgun", Glyph = "RG", Color = Hex("#B98CFF"), LevelDesc = new[] {
                "Charges a piercing beam toward your aim.", "Damage +40%.", "Cooldown -15%.", "Beam width +50%.", "Fires 2 extra side beams.", "Damage +50%.", "Cooldown -20%." } },
            new UpgradeDef { IsWeapon = true, Id = (int)WeaponId.Missiles, Name = "Swarm Missiles", Glyph = "MS", Color = Hex("#FF5D7A"), LevelDesc = new[] {
                "Homing missiles that explode on impact.", "+1 missile.", "Damage +30%.", "Blast radius +30%.", "+1 missile.", "Cooldown -20%.", "+2 missiles." } },
            new UpgradeDef { IsWeapon = true, Id = (int)WeaponId.Tesla, Name = "Tesla Coil", Glyph = "TC", Color = Hex("#5CA8FF"), LevelDesc = new[] {
                "Lightning that chains between enemies.", "+1 chain.", "Damage +30%.", "Cooldown -20%.", "+2 chains.", "Damage +40%.", "Fires a second bolt." } },
        };

        public static readonly UpgradeDef[] Passives =
        {
            P(PassiveId.Power,       "Power Core",   "PW", "#FF5D5D", 5, "Damage +10%."),
            P(PassiveId.Trigger,     "Hair Trigger", "HT", "#FFD24D", 5, "Weapon cooldowns -8%."),
            P(PassiveId.Servo,       "Servo Legs",   "SV", "#7CFFB2", 5, "Move speed +8%."),
            P(PassiveId.Plating,     "Plating",      "PL", "#FF8A8A", 5, "Max HP +20."),
            P(PassiveId.Magnet,      "Magnet Coil",  "MG", "#6BD5FF", 5, "Pickup radius +30%."),
            P(PassiveId.Split,       "Split Chamber","SC", "#FFA6F2", 2, "All guns fire +1 projectile."),
            P(PassiveId.Reflex,      "Reflex Chip",  "RX", "#38E8FF", 5, "Parry window +0.04s, parry radius +10%."),
            P(PassiveId.CounterNova, "Counter Nova", "CN", "#A0F0FF", 5, "Successful parries release a damaging shockwave."),
            P(PassiveId.MirrorCoat,  "Mirror Coat",  "MC", "#E0E8FF", 5, "Reflected bullets deal +60% damage and pierce +1."),
            P(PassiveId.LeechParry,  "Leech Parry",  "LP", "#FF4D88", 5, "Heal 2 HP per parried attack."),
            P(PassiveId.Armor,       "Armor",        "AR", "#B0B8C8", 5, "Incoming damage -1."),
            P(PassiveId.Nanorepair,  "Nanorepair",   "NR", "#6BFF8A", 5, "Regenerate 0.3 HP/s."),
            P(PassiveId.CritLens,    "Crit Lens",    "CL", "#FFE680", 5, "Critical chance +6%."),
            P(PassiveId.Overclock,   "Overclock",    "OC", "#FF7AF0", 5, "Overdrive charges 25% faster and lasts +1s."),
        };

        static UpgradeDef P(PassiveId id, string name, string glyph, string color, int maxLevel, string desc)
        {
            var d = new string[maxLevel];
            for (int i = 0; i < maxLevel; i++) d[i] = desc;
            return new UpgradeDef { IsWeapon = false, Id = (int)id, Name = name, Glyph = glyph, Color = Hex(color), LevelDesc = d };
        }

        public static readonly EvolutionDef[] Evolutions =
        {
            Evo(WeaponId.Blaster,  PassiveId.MirrorCoat,  "Reflector",       "RF", "#9FF6FF", "Bullets ricochet to 2 more enemies. Counter rounds hold +2."),
            Evo(WeaponId.Shotgun,  PassiveId.CounterNova, "Nova Burst",      "NB", "#FFC27A", "Every parry fires a 360° pellet ring (two on PERFECT)."),
            Evo(WeaponId.Drones,   PassiveId.LeechParry,  "Guardian Drones", "GD", "#B4FF9E", "Drones parry enemy bullets near them and heal you 1 HP each."),
            Evo(WeaponId.Railgun,  PassiveId.Reflex,      "Counter Rail",    "CR", "#D7BCFF", "Every parry fires a railgun beam at your aim (three on PERFECT)."),
            Evo(WeaponId.Missiles, PassiveId.Overclock,   "Overdrive Swarm", "OS", "#FF9AAE", "Every parry launches 3 missiles. Double fire rate in Overdrive."),
            Evo(WeaponId.Tesla,    PassiveId.Armor,       "Arc Aegis",       "AA", "#9CCBFF", "Every parry arcs lightning to 6 nearby enemies; PERFECT also stuns them."),
        };

        static EvolutionDef Evo(WeaponId w, PassiveId req, string name, string glyph, string color, string desc) => new EvolutionDef
        {
            Weapon = w, Requires = req,
            Def = new UpgradeDef { IsWeapon = true, Id = (int)w, Name = name, Glyph = glyph, Color = Hex(color), LevelDesc = new[] { desc } },
        };

        public static EvolutionDef Evolution(WeaponId w)
        {
            foreach (var e in Evolutions) if (e.Weapon == w) return e;
            return null;
        }

        public static UpgradeDef Weapon(WeaponId id) => Weapons[(int)id];
        public static UpgradeDef Passive(PassiveId id) => Passives[(int)id];

        public static readonly MetaDef[] Meta =
        {
            new MetaDef { Id = MetaId.Might,     Name = "Might",     Desc = "+5% damage per rank.",              MaxRank = 5, BaseCost = 120 },
            new MetaDef { Id = MetaId.Vitality,  Name = "Vitality",  Desc = "+10 max HP per rank.",              MaxRank = 5, BaseCost = 100 },
            new MetaDef { Id = MetaId.Agility,   Name = "Agility",   Desc = "+4% move speed per rank.",          MaxRank = 3, BaseCost = 150 },
            new MetaDef { Id = MetaId.Magnetism, Name = "Magnetism", Desc = "+15% pickup radius per rank.",      MaxRank = 3, BaseCost = 90 },
            new MetaDef { Id = MetaId.Reflexes,  Name = "Reflexes",  Desc = "+0.02s parry window per rank.",     MaxRank = 3, BaseCost = 200 },
            new MetaDef { Id = MetaId.Haste,     Name = "Haste",     Desc = "-3% weapon cooldowns per rank.",    MaxRank = 3, BaseCost = 180 },
            new MetaDef { Id = MetaId.Greed,     Name = "Greed",     Desc = "+10% gold per rank.",               MaxRank = 5, BaseCost = 100 },
            new MetaDef { Id = MetaId.Growth,    Name = "Growth",    Desc = "+6% experience per rank.",          MaxRank = 5, BaseCost = 110 },
            new MetaDef { Id = MetaId.Reroll,    Name = "Reroll",    Desc = "+1 level-up reroll per run.",       MaxRank = 3, BaseCost = 250 },
            new MetaDef { Id = MetaId.Revival,   Name = "Revival",   Desc = "Revive once per run at 50% HP.",    MaxRank = 1, BaseCost = 1200 },
        };
    }
}
