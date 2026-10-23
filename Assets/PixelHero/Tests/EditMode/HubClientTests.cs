using System.Collections.Generic;
using System.IO;
using PixelHero.Js;
using PixelHero.Network;
using NUnit.Framework;

namespace PixelHero.Tests
{
    /// <summary>
    /// The server the client connects to through the hub (HubRequests.ServerUrl) against the original client's
    /// network/socket.ts connect() (Tools/fixtures/gen_hub_client_cases.mts): the hub's /server answer of any JSON
    /// shape, a server picked in the world list, a body that is not JSON and a failed request.
    /// </summary>
    public class HubClientTests
    {
        const string Config = "ws://cfg.example:9011";

        [Test]
        public void ServerUrlMatchesTheOriginal()
        {
            var cases = (List<object>)JsJson.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "hub_client_cases.json")));
            Assert.Greater(cases.Count, 30);

            var failures = new List<string>();
            foreach (Dictionary<string, object> c in cases)
            {
                // A failed fetch, or a body response.json() rejects, leaves undefined (the config is used).
                object server = null;
                if (!(bool)c["fails"]) server = HubRequests.Json((string)c["body"], out _);

                var url = HubRequests.ServerUrl(server, Config);
                if (url != (string)c["url"]) failures.Add($"{c["body"]}: {c["url"]} vs {url}");
            }

            Assert.IsEmpty(failures, string.Join("\n", failures));
        }
    }
}
