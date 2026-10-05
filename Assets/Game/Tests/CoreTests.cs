using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace GunAndParry.Tests
{
    public class CoreTests
    {
        [Test]
        public void SpatialGrid_ReturnsEachItemOnceWithinRadius()
        {
            var grid = new SpatialGrid(2f);
            var points = new List<Vector2>();
            var rng = new System.Random(1);
            for (int i = 0; i < 2000; i++)
            {
                var p = new Vector2((float)rng.NextDouble() * 400 - 200, (float)rng.NextDouble() * 400 - 200);
                points.Add(p);
                grid.Add(p);
            }
            var results = new List<int>();
            foreach (var (center, radius) in new[] { (Vector2.zero, 5f), (new Vector2(37, -81), 12f), (new Vector2(-150, 150), 60f) })
            {
                results.Clear();
                grid.Query(center, radius, results);
                var expected = new HashSet<int>();
                for (int i = 0; i < points.Count; i++) if ((points[i] - center).magnitude <= radius) expected.Add(i);
                Assert.AreEqual(expected.Count, results.Count, "count / duplicates");
                CollectionAssert.AreEquivalent(expected, results);
            }
        }

        [Test]
        public void XpCurve_IsMonotonicAndStartsAtFive()
        {
            Assert.AreEqual(5, GameData.XpToNext(1));
            for (int lv = 1; lv < 80; lv++) Assert.Greater(GameData.XpToNext(lv + 1), GameData.XpToNext(lv) - 1);
        }

        [Test]
        public void UpgradePicker_RespectsSlotsAndMaxLevels()
        {
            var rng = new System.Random(7);
            var l = new Loadout();
            for (int i = 0; i < 500; i++)
            {
                var picks = UpgradePicker.Roll(l, 3, rng);
                var seen = new HashSet<UpgradeDef>();
                foreach (var c in picks)
                {
                    if (c.Fallback != FallbackReward.None) continue;
                    if (c.Evolution != null) { Assert.IsTrue(l.CanEvolve(c.Evolution)); continue; }
                    Assert.IsTrue(seen.Add(c.Def), "duplicate choice");
                    Assert.AreEqual(l.Level(c.Def) + 1, c.NextLevel);
                }
                var first = picks[0];
                if (first.Evolution != null) l.Evolve(first.Evolution);
                else if (first.Fallback == FallbackReward.None) l.Apply(first.Def);
                Assert.LessOrEqual(l.Weapons.Count, GameData.MaxWeapons);
                Assert.LessOrEqual(l.PassiveCount, GameData.MaxPassives);
            }
            // After 500 picks everything offerable is maxed: only fallbacks remain.
            var final = UpgradePicker.Roll(l, 3, rng);
            Assert.IsTrue(final.TrueForAll(c => c.Fallback != FallbackReward.None));
        }

        [Test]
        public void Evolution_RequiresMaxWeaponAndPassive_AndIsOfferedOnce()
        {
            var l = new Loadout();
            var evo = GameData.Evolution(WeaponId.Blaster);
            l.Apply(GameData.Weapon(WeaponId.Blaster));
            Assert.IsFalse(l.CanEvolve(evo), "level 1");
            for (int i = 1; i < GameData.Weapon(WeaponId.Blaster).MaxLevel; i++) l.Apply(GameData.Weapon(WeaponId.Blaster));
            Assert.IsFalse(l.CanEvolve(evo), "max level but no passive");
            l.Apply(GameData.Passive(evo.Requires));
            Assert.IsTrue(l.CanEvolve(evo));

            var offers = UpgradePicker.Roll(l, 3, new System.Random(3));
            Assert.IsTrue(offers.Exists(c => c.Evolution == evo), "ready evolution is always offered");

            l.Evolve(evo);
            Assert.IsTrue(l.Find(WeaponId.Blaster).Evolved);
            Assert.IsFalse(l.CanEvolve(evo));
            for (int seed = 0; seed < 50; seed++)
                Assert.IsFalse(UpgradePicker.Roll(l, 3, new System.Random(seed)).Exists(c => c.Evolution == evo));
        }

        [Test]
        public void SpawnRate_RampsUpThenDropsForBoss()
        {
            Assert.AreEqual(GameData.Waves[0].SpawnsPerSecond, Spawner.SpawnRate(0), 1e-4);
            Assert.Greater(Spawner.SpawnRate(12), Spawner.SpawnRate(6));
            Assert.Less(Spawner.SpawnRate(15.5f), Spawner.SpawnRate(14.9f));
        }

        [Test]
        public void Save_RoundTripsAndSurvivesCorruption()
        {
            string dir = Path.Combine(Path.GetTempPath(), "gap_test_" + System.Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "save.json");
            try
            {
                var d = new SaveData { gold = 1234, bestTime = 321f };
                d.meta[(int)MetaId.Might] = 3;
                SaveSystem.Save(d, path);
                SaveSystem.Save(d, path); // exercises the replace path
                var loaded = SaveSystem.Load(path);
                Assert.AreEqual(1234, loaded.gold);
                Assert.AreEqual(3, loaded.Meta(MetaId.Might));

                File.WriteAllText(path, "{ not json");
                var recovered = SaveSystem.Load(path);
                Assert.AreEqual(0, recovered.gold);
                Assert.IsTrue(File.Exists(path + ".corrupt"));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void MetaShop_ChargesAndCapsRanks()
        {
            var d = new SaveData { gold = 100000 };
            var def = GameData.Meta[(int)MetaId.Revival];
            Assert.IsTrue(SaveSystem.TryBuy(d, def));
            Assert.IsFalse(SaveSystem.TryBuy(d, def), "max rank reached");
            Assert.AreEqual(100000 - def.Cost(0), d.gold);
        }

        [Test]
        public void Stats_ApplyMetaAndPassives()
        {
            var save = new SaveData();
            save.meta[(int)MetaId.Vitality] = 2;
            var l = new Loadout();
            l.PassiveLevels[(int)PassiveId.Plating] = 3;
            l.PassiveLevels[(int)PassiveId.Trigger] = 2;
            var s = PlayerStats.Compute(l, save);
            Assert.AreEqual(100f + 20f + 60f, s.MaxHp, 1e-3);
            Assert.AreEqual(0.92f * 0.92f, s.CooldownMult, 1e-4);
        }

        [Test]
        public void SegmentDistance()
        {
            Assert.AreEqual(1f, Geo.DistanceToSegment(new Vector2(5, 1), Vector2.zero, new Vector2(10, 0)), 1e-4);
            Assert.AreEqual(5f, Geo.DistanceToSegment(new Vector2(-3, 4), Vector2.zero, new Vector2(10, 0)), 1e-4);
            Assert.AreEqual(2f, Geo.DistanceToSegment(new Vector2(3, 2), new Vector2(3, 0), new Vector2(3, 0)), 1e-4, "degenerate segment");
        }
    }
}
