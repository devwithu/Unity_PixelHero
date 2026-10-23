using PixelHero.Hub.Controllers;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// The hub's fixes that the original-comparison harness does not reach (its HTTP side only carries isOnline):
    /// ANALYSIS.md §9 #15 (no database: the leaderboards answer instead of hanging) and #17 (only real mob keys).
    /// </summary>
    [NonParallelizable]
    public class HubFixTests
    {
        EventLoop _loop;
        Express _app;

        [SetUp]
        public void CreateHub()
        {
            _loop = new EventLoop(1_700_000_000_000);
            Node.Install(_loop, new ParkMiller(1));
            Log.Sink = (_, _) => { };

            // SKIP_DATABASE keeps the cache from exiting the process when the (absent) database fails to connect.
            Config.Current = Config.Load(Path.Combine(TestPaths.Data, ".env.defaults"), null,
                new JsObject("hubEnabled", true, "skipDatabase", true));

            Express.VirtualListen = (_, app) => _app = app;

            var mobs = JsData.ParseObject(File.ReadAllText(Path.Combine(ServerHarness.DataDirectory, "mobs.json")));
            _loop.Run(() => _ = new PixelHero.Hub.Controllers.Api(new Models(), new Mailer(), new Cache(null, mobs), mobs));
        }

        [TearDown]
        public void Restore() => Express.VirtualListen = null;

        string Leaderboards(string query)
        {
            string body = null;
            var response = new ExpressResponse { Completed = text => body = text };
            _loop.Run(() => _app.Find("GET", "/leaderboards")(
                new ExpressRequest { method = "GET", path = "/leaderboards", query = Express.ParseQuery(query) }, response));
            return body;
        }

        [Test]
        public void LeaderboardsAnswerWithoutADatabase()
        {
            Assert.That(Leaderboards(""), Is.EqualTo("{\"status\":\"success\",\"list\":[],\"availableMobs\":{}}"));
            Assert.That(Leaderboards("skill=1"), Is.EqualTo("{\"status\":\"success\",\"list\":[]}"));
            Assert.That(Leaderboards("mob=rat"), Is.EqualTo("{\"status\":\"success\",\"list\":[]}"));
            Assert.That(Leaderboards("pvp=1"), Is.EqualTo("{\"status\":\"success\",\"list\":[]}"));
        }

        [Test]
        public void OnlyRealMobKeysAreAccepted()
        {
            foreach (var key in new[] { "constructor", "__proto__", "toString", "hasOwnProperty" })
                Assert.That(Leaderboards($"mob={key}"), Is.EqualTo("{\"error\":\"invalid\"}"), key);
        }
    }
}
