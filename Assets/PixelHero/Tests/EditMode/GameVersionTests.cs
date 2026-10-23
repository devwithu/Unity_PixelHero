using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace PixelHero.Tests
{
    public class GameVersionTests
    {
        [Test]
        public void GverMatchesOriginalEnvDefaults()
        {
            var env = File.ReadAllText(Path.Combine(TestPaths.Root, ".env.defaults"));
            var match = Regex.Match(env, @"^GVER='([^']*)'", RegexOptions.Multiline);

            Assert.IsTrue(match.Success, "GVER not found in .env.defaults");
            Assert.AreEqual(match.Groups[1].Value, GameVersion.Gver);
        }

        [Test]
        public void MapVersionMatchesOriginalMaps()
        {
            var server = JObject.Parse(File.ReadAllText(TestPaths.Data("server/data/map/world.json")));
            var client = JObject.Parse(File.ReadAllText(TestPaths.Data("client/maps/map.json")));

            Assert.AreEqual(GameVersion.MapVersion, (long)server["version"]);
            Assert.AreEqual(GameVersion.MapVersion, (long)client["version"]);
        }
    }
}
