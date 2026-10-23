using PixelHero.Game;
using NUnit.Framework;

namespace PixelHero.Tests
{
    public sealed class WebServerUrlTests
    {
        [TestCase("http://localhost:9050/", "ws://localhost:59011")]
        [TestCase("http://192.168.0.7:9050/index.html", "ws://192.168.0.7:59011")]
        [TestCase("https://example.org/play/", "wss://example.org:59011")]
        [TestCase("http://localhost:9050/?server=ws%3A%2F%2F10.0.0.2%3A9001", "ws://10.0.0.2:9001")]
        [TestCase("http://localhost:9050/?lang=ko&server=ws://h:1", "ws://h:1")]
        [TestCase("", "ws://127.0.0.1:59011")]
        public void ThePageHostOrTheServerParameter(string page, string expected) =>
            Assert.AreEqual(expected, GameClient.WebServerUrl(page, "ws://127.0.0.1:59011"));

        // With the secure server: an https page uses wss at its port, an http page keeps ws.
        [TestCase("https://localhost:9051/", "wss://localhost:59443")]
        [TestCase("https://game.example.org/", "wss://game.example.org:59443")]
        [TestCase("http://localhost:9050/", "ws://localhost:59011")]
        [TestCase("http://localhost:9050/?server=wss://localhost:59443", "wss://localhost:59443")]
        [TestCase("https://localhost:9051/?server=ws%3A%2F%2Fh%3A1", "ws://h:1")]
        public void HttpsPagesUseTheSecureServer(string page, string expected) =>
            Assert.AreEqual(expected, GameClient.WebServerUrl(page, "ws://127.0.0.1:59011", "wss://localhost:59443"));

        [Test]
        public void DesktopUsesTheCommandLineThenTheEnvironment()
        {
            const string fallback = "ws://127.0.0.1:59011";
            Assert.AreEqual("wss://localhost:59443",
                GameClient.DesktopServerUrl(new[] { "PixelHero.exe", "-server", "wss://localhost:59443" }, "ws://env:1", fallback));
            Assert.AreEqual("ws://env:1", GameClient.DesktopServerUrl(new[] { "PixelHero.exe" }, "ws://env:1", fallback));
            Assert.AreEqual(fallback, GameClient.DesktopServerUrl(new[] { "PixelHero.exe", "-server" }, null, fallback));
            Assert.AreEqual(fallback, GameClient.DesktopServerUrl(null, "", fallback));
        }
    }
}
