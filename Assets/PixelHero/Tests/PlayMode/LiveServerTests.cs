using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using PixelHero.Common;
using PixelHero.Network;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace PixelHero.Tests
{
    /// <summary>
    /// Against a running PixelHero server (dotnet run --project Server/src/PixelHero.Server). Ignored when it is not running.
    /// One connection per test run: the server rejects a reconnect from the same IP within 5 s ("toofast").
    /// </summary>
    public class LiveServerTests
    {
        const float TimeoutSeconds = 15;

        /// <summary>PIXELHERO_SERVER_URL when set, otherwise the server at the PORT in PixelHero-Data/.env.</summary>
        static string ServerUrl()
        {
            var url = System.Environment.GetEnvironmentVariable("PIXELHERO_SERVER_URL");
            if (!string.IsNullOrEmpty(url)) return url;

            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "PixelHero-Data"));
            var port = "9001";
            foreach (var file in new[] { ".env.defaults", ".env" })
            {
                var path = Path.Combine(root, file);
                if (!File.Exists(path)) continue;
                var match = Regex.Match(File.ReadAllText(path), @"^PORT=(\d+)", RegexOptions.Multiline);
                if (match.Success) port = match.Groups[1].Value;
            }
            return $"ws://127.0.0.1:{port}";
        }

        [UnityTest]
        public IEnumerator GuestLoginReceivesWelcomeAndMap()
        {
            // The server rejects a reconnect from the same IP within 5 s; another live test may have just connected.
            yield return new WaitForSecondsRealtime(6);

            var url = ServerUrl();
            var connection = new GameConnection(WebSocketTransportFactory.Create());
            var log = new List<string>();
            string failure = null;
            Dictionary<string, object> welcome = null;
            Dictionary<string, object> regions = null;

            connection.ConnectionFailed += message => failure = message;
            connection.Rejected += (reason, message) => failure = $"rejected: {reason}";
            connection.PacketReceived += packet => log.Add(packet.Id.ToString());
            connection.On(Packets.Welcome, packet =>
            {
                welcome = (Dictionary<string, object>)packet.Arg(0);
                connection.Send(Packets.Ready, new Dictionary<string, object>
                {
                    ["regionsLoaded"] = 0,
                    ["userAgent"] = "pixelhero-unity-test"
                });
            });
            connection.On(Packets.Map, packet => regions = MapPayload.Decode((string)packet.Arg(0)));

            connection.Connect(url, LoginRequest.Guest());

            var elapsed = 0f;
            while (regions == null && failure == null && elapsed < TimeoutSeconds)
            {
                connection.Update();
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            connection.Close();
            for (var i = 0; i < 10; i++)
            {
                connection.Update();
                yield return null;
            }
            connection.Dispose();

            if (failure != null && welcome == null && failure.StartsWith("rejected") == false)
                Assert.Ignore($"Server not reachable at {url} ({failure}). Start it with dotnet run --project Server/src/PixelHero.Server.");

            Assert.IsNull(failure, failure);
            Assert.IsNotNull(welcome, $"no Welcome within {TimeoutSeconds}s; got {string.Join(",", log)}");
            Assert.IsNotNull(regions, $"no Map within {TimeoutSeconds}s; got {string.Join(",", log)}");

            StringAssert.StartsWith("Guest", (string)welcome["name"]);
            Assert.AreEqual(connection.Instance, (string)welcome["instance"]);
            Assert.IsNotEmpty(regions);
            Debug.Log($"[LiveServerTests] {welcome["name"]} at {welcome["x"]},{welcome["y"]}; regions {string.Join(",", regions.Keys)}; packets {string.Join(",", log)}");
        }
    }
}
