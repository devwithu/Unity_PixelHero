using PixelHero.Js;
using PixelHero.Server.Js;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// Replays each scenario recorded from the original (Tools/fixtures/out/server/*.json, made by
    /// gen_server_cases.mts) against the C# server and requires the same frames and closes, at the same virtual
    /// times, byte for byte (Map packets compared after decompression). Scenarios with a database also compare the
    /// documents left in it (the bcrypt hash aside: its salt is random).
    /// </summary>
    [NonParallelizable]
    public class ScenarioTests
    {
        static IEnumerable<string> Scenarios() =>
            Directory.GetFiles(Path.Combine(TestPaths.Root, "Tools", "fixtures", "server", "scenarios"), "*.json")
                .Where(file => !ClusterScenarioTests.IsCluster(file))
                .Select(Path.GetFileNameWithoutExtension).OrderBy(name => name, StringComparer.Ordinal);

        [TestCaseSource(nameof(Scenarios))]
        public void MatchesTheOriginal(string name)
        {
            var fixture = JsData.ParseObject(File.ReadAllText(Path.Combine(TestPaths.ServerFixtures, $"{name}.json")));

            var harness = new ServerHarness(
                (long)fixture.Num("seed").Value,
                fixture.Num("epoch").Value,
                fixture.Obj("config"),
                fixture.Obj("database"));

            var actual = harness.Replay(fixture.Arr("inputs"), fixture.Num("end") ?? 0);
            var expected = fixture.Arr("outputs").Cast<JsObject>().ToList();

            for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
            {
                var e = expected[i];
                var a = actual[i];
                var where = $"output {i} (expected t={JsValue.NumberToString(e.Num("t") ?? double.NaN)} {e.Str("kind")})";

                Assert.That(a.kind, Is.EqualTo(e.Str("kind")), where + Context(harness, e, a));
                Assert.That(a.t, Is.EqualTo(e.Num("t")), where);

                switch (a.kind)
                {
                    case "frame":
                        Assert.That(a.conn, Is.EqualTo((int?)e.Num("conn")), where);
                        Assert.That(ServerHarness.Normalize(a.text), Is.EqualTo(ServerHarness.Normalize(e.Str("text"))), where);
                        break;
                    case "close":
                        Assert.That(a.conn, Is.EqualTo((int?)e.Num("conn")), where);
                        Assert.That(a.code, Is.EqualTo(e.Num("code")), where);
                        Assert.That(a.reason, Is.EqualTo(e.Str("reason")), where);
                        break;
                    case "crash":
                        Assert.That(a.error, Is.EqualTo(e.Str("error")), where);
                        break;
                }
            }

            Assert.That(actual.Count, Is.EqualTo(expected.Count), "output count" + Context(harness, null, null));

            if (fixture.Obj("finalDatabase") is { } expectedDatabase)
                Assert.That(DatabaseText(harness.Database), Is.EqualTo(DatabaseText(expectedDatabase)), "final database");
        }

        static string Context(ServerHarness harness, JsObject expected, ServerHarness.Output actual)
        {
            var text = $"\ncrash: {harness.Crash ?? "none"}\nlogs:\n  {string.Join("\n  ", harness.Logs.Take(20))}";
            if (actual != null) text += $"\nactual: {actual.kind} {actual.text ?? actual.reason ?? actual.error}";
            return text;
        }

        /// <summary>The documents per collection, ignoring _id and the password hash.</summary>
        public static string DatabaseText(JsObject database)
        {
            var copy = new JsObject();
            foreach (var pair in database)
            {
                var documents = ((List<object>)pair.Value).ConvertAll(document =>
                {
                    var clone = (JsObject)JsData.Clone(document);
                    clone.Delete("_id");
                    if (clone.Has("password")) clone.Set("password", "<hash>");
                    return (object)clone;
                });
                copy.Set(pair.Key, documents);
            }
            return JsJson.Stringify(copy);
        }
    }
}
