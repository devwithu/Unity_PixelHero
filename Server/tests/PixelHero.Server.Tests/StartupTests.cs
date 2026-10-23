using System.Security.Cryptography;
using System.Text;
using PixelHero.Js;
using PixelHero.Server.Info;
using PixelHero.Server.Js;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// The world right after startup against the original's (Tools/fixtures/server/gen_server_startup.mts):
    /// every entity in creation order, the random seed after startup (how many draws ran), the instance counter,
    /// and each region's tile data.
    /// </summary>
    [NonParallelizable]
    public class StartupTests
    {
        static JsObject _fixture;
        static ServerHarness _harness;

        [OneTimeSetUp]
        public void Start()
        {
            _fixture = JsData.ParseObject(File.ReadAllText(Path.Combine(TestPaths.ServerFixtures, "startup.json")));
            _harness = new ServerHarness((long)_fixture.Num("seed").Value);
        }

        [Test]
        public void StartsWithoutCrashing() => Assert.That(_harness.Crash, Is.Null);

        [Test]
        public void EntitiesMatchInCreationOrder()
        {
            var expected = _fixture.Arr("entities");
            var actual = _harness.Main.world.entities.All;

            for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
            {
                var e = (JsObject)expected[i];
                var a = actual[i];
                var description = $"entity {i}: expected {JsJson.Stringify(e)}";
                Assert.That(a.instance, Is.EqualTo(e.Str("instance")), description);
                Assert.That(a.type, Is.EqualTo(e.Num("type")), description);
                Assert.That(a.key, Is.EqualTo(e.Str("key")), description);
                Assert.That(a.x, Is.EqualTo(e.Num("x")), description);
                Assert.That(a.y, Is.EqualTo(e.Num("y")), description);
                Assert.That((double)a.region, Is.EqualTo(e.Num("region")), description);
            }

            Assert.That(actual.Count, Is.EqualTo(expected.Count));
        }

        [Test]
        public void RandomDrawsMatch()
        {
            Assert.That(_harness.Random.Seed, Is.EqualTo((long)_fixture.Num("seedAfter").Value));
            Assert.That(Common.Utils.counter, Is.EqualTo(_fixture.Num("instanceCounter")));
        }

        [Test]
        public void RegionTileDataMatches()
        {
            var expected = _fixture.Arr("regions");
            var regions = new List<string>();
            _harness.Main.world.map.regions.ForEachRegion((region, _) =>
                regions.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsJson.Stringify(region.data)))).ToLowerInvariant()));

            Assert.That(regions.Count, Is.EqualTo(expected.Count));
            for (var i = 0; i < regions.Count; i++) Assert.That(regions[i], Is.EqualTo(expected[i]), $"region {i}");
        }

        [Test]
        public void LevelTableIsFilled() => Assert.That(Formulas.LevelExp.Count, Is.EqualTo(PixelHero.Common.Constants.MAX_LEVEL));
    }
}
