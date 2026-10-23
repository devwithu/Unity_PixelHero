using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// Config.Load against the original's pipeline (dotenv-extended with dotenv 8.6.0, dotenv-parse-variables, the
    /// camelCase keys of common/config.ts) on a generated .env (Tools/fixtures/server/gen_config_cases.mjs): quoting,
    /// backticks (DISCORD_CHANNEL_ID must stay a string of 19 digits), inline #, numbers, lists, odd lines.
    /// </summary>
    public class ConfigTests
    {
        [Test]
        public void ParsesLikeTheOriginal()
        {
            var env = Path.Combine(TestPaths.ServerFixtures, "config_cases.env");
            var expected = JsData.ParseObject(File.ReadAllText(Path.Combine(TestPaths.ServerFixtures, "config_cases.json")));

            var config = Config.Load(env, null);

            Assert.That(config.Values.Keys, Is.EquivalentTo(expected.Keys));

            var failures = expected.Keys
                .Where(key => JsJson.Stringify(config.Values[key]) != JsJson.Stringify(expected[key]))
                .Select(key => $"{key}: {JsJson.Stringify(expected[key])} vs {JsJson.Stringify(config.Values[key])}")
                .ToList();

            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        [Test]
        public void BacktickedChannelIdKeepsItsDigits()
        {
            var env = Path.Combine(Path.GetTempPath(), $"pixelhero_config_{Guid.NewGuid():N}.env");
            File.WriteAllText(env, "DISCORD_CHANNEL_ID=`1234567890123456789`\n");
            try
            {
                Assert.That(Config.Load(env, null).discordChannelId, Is.EqualTo("1234567890123456789"));
            }
            finally
            {
                File.Delete(env);
            }
        }
    }
}
