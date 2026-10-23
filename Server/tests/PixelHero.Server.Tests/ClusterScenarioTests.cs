using PixelHero.Js;
using PixelHero.Server.Js;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// Replays each cluster scenario recorded from the original hub and servers (Tools/fixtures/out/server/*.json with
    /// `nodes`) against the C# hub and servers (<see cref="ClusterHarness"/>) and requires the same outputs in the same
    /// order: the players' frames and closes, and every frame, close, request and response between the hub and the
    /// servers, at the same virtual times. Scenarios with a database also compare the documents left in it.
    /// </summary>
    [NonParallelizable]
    public class ClusterScenarioTests
    {
        public static IEnumerable<string> Scenarios() =>
            Directory.GetFiles(Path.Combine(TestPaths.Root, "Tools", "fixtures", "server", "scenarios"), "*.json")
                .Where(IsCluster)
                .Select(Path.GetFileNameWithoutExtension).OrderBy(name => name, StringComparer.Ordinal);

        public static bool IsCluster(string scenarioFile) => JsData.ParseObject(File.ReadAllText(scenarioFile)).Has("nodes");

        [TestCaseSource(nameof(Scenarios))]
        public void MatchesTheOriginal(string name)
        {
            var fixture = JsData.ParseObject(File.ReadAllText(Path.Combine(TestPaths.ServerFixtures, $"{name}.json")));

            using var harness = new ClusterHarness(
                fixture.Arr("nodes"),
                (long)fixture.Num("seed").Value,
                fixture.Num("epoch").Value,
                fixture.Obj("database"));

            var actual = harness.Replay(fixture.Arr("inputs"), fixture.Num("end") ?? 0);
            var expected = fixture.Arr("outputs").Cast<JsObject>().ToList();

            for (var i = 0; i < Math.Min(expected.Count, actual.Count); i++)
            {
                var e = expected[i];
                var a = actual[i];
                var where = $"output {i} (expected t={JsValue.NumberToString(e.Num("t") ?? double.NaN)} {e.Str("kind")} " +
                            $"{e.Str("node") ?? e.Str("from")}){Context(harness, a)}";

                Assert.That(a.kind, Is.EqualTo(e.Str("kind")), where);
                Assert.That(a.t, Is.EqualTo(e.Num("t")), where);

                switch (a.kind)
                {
                    case "frame":
                        Assert.That(a.node, Is.EqualTo(e.Str("node")), where);
                        Assert.That(a.conn, Is.EqualTo((int?)e.Num("conn")), where);
                        Assert.That(ServerHarness.Normalize(a.text), Is.EqualTo(ServerHarness.Normalize(e.Str("text"))), where);
                        break;
                    case "close":
                        Assert.That(a.node, Is.EqualTo(e.Str("node")), where);
                        Assert.That(a.conn, Is.EqualTo((int?)e.Num("conn")), where);
                        Assert.That(a.code, Is.EqualTo(e.Num("code")), where);
                        Assert.That(a.reason, Is.EqualTo(e.Str("reason")), where);
                        break;
                    case "link":
                    case "httpResponse":
                        Assert.That((a.from, a.to), Is.EqualTo((e.Str("from"), e.Str("to"))), where);
                        Assert.That(a.text, Is.EqualTo(e.Str("text")), where);
                        break;
                    case "linkclose":
                        Assert.That((a.from, a.to), Is.EqualTo((e.Str("from"), e.Str("to"))), where);
                        break;
                    case "http":
                        Assert.That((a.from, a.to, a.method, a.path), Is.EqualTo((e.Str("from"), e.Str("to"), e.Str("method"), e.Str("path"))), where);
                        Assert.That(a.text, Is.EqualTo(e.Str("text")), where);
                        break;
                    case "crash":
                        Assert.That(a.error, Is.EqualTo(e.Str("error")), where);
                        break;
                }
            }

            Assert.That(actual.Count, Is.EqualTo(expected.Count), "output count" + Context(harness, null));

            if (fixture.Obj("finalDatabase") is { } expectedDatabase)
                Assert.That(ScenarioTests.DatabaseText(harness.Database), Is.EqualTo(ScenarioTests.DatabaseText(expectedDatabase)),
                    "final database");
        }

        static string Context(ClusterHarness harness, ClusterHarness.Output actual)
        {
            var text = $"\ncrash: {harness.Crash ?? "none"}\nlogs:\n  {string.Join("\n  ", harness.Logs.Take(20))}";
            if (actual != null) text += $"\nactual: {actual.kind} {actual.node ?? actual.from}->{actual.to} {actual.text ?? actual.reason ?? actual.error}";
            return text;
        }
    }
}
