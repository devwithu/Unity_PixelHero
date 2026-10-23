using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PixelHero.Common;
using PixelHero.Js;
using PixelHero.Network;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace PixelHero.Tests
{
    /// <summary>Packet frames, the Map payload and the session start, against recorded browser sessions and Node.</summary>
    public class NetworkTests
    {
        static IEnumerable<JObject> RecordedFrames(string direction) =>
            Directory.GetFiles(TestPaths.Recordings, "*.jsonl")
                .SelectMany(File.ReadLines)
                .Where(line => line.Trim().Length > 0)
                .Select(JObject.Parse)
                .Where(entry => (string)entry["dir"] == direction);

        [Test]
        public void ClientFramesAreSerializedExactlyLikeTheBrowser()
        {
            var frames = RecordedFrames("c2s").Select(entry => (string)entry["data"]).ToList();
            Assert.Greater(frames.Count, 100);

            foreach (var frame in frames)
            {
                var message = (List<object>)JsJson.Parse(frame);
                var id = (Packets)(int)(double)message[0];
                var data = message.Count > 1 ? message[1] : null;

                Assert.AreEqual(frame, PacketFrames.Serialize(id, data));
            }
        }

        [Test]
        public void ServerFramesParseIntoKnownPackets()
        {
            var count = 0;
            foreach (var entry in RecordedFrames("s2c"))
            {
                var frame = (string)entry["data"];
                var packets = PacketFrames.Parse(frame);
                var expected = JArray.Parse(frame);

                Assert.AreEqual(expected.Count, packets.Count);
                for (var i = 0; i < packets.Count; i++)
                {
                    Assert.IsTrue(Enum.IsDefined(typeof(Packets), packets[i].Id), $"unknown id {packets[i].Id}");
                    Assert.AreEqual((int)expected[i][0], (int)packets[i].Id);
                    Assert.AreEqual(((JArray)expected[i]).Count - 1, packets[i].Args.Count);
                }

                count += packets.Count;
            }

            Assert.Greater(count, 300);
        }

        [Test]
        public void NonArrayFramesAreIgnoredLikeTheOriginal()
        {
            Assert.IsEmpty(PacketFrames.Parse("This is server, why are you here?"));
        }

        [Test]
        public void MapPayloadsMatchNode()
        {
            var cases = JArray.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "map_payload_cases.json")));
            Assert.IsNotEmpty(cases);

            foreach (var c in cases)
            {
                var line = File.ReadLines(Path.Combine(TestPaths.Recordings, (string)c["recording"])).ElementAt((int)c["line"]);
                var packet = PacketFrames.Parse((string)JObject.Parse(line)["data"])[(int)c["position"]];
                Assert.AreEqual(Packets.Map, packet.Id);

                var text = MapPayload.Inflate((string)packet.Arg(0));
                Assert.AreEqual((int)c["length"], text.Length);
                Assert.AreEqual((string)c["sha256"], Sha256(text));
                Assert.AreEqual((double)c["bufferSize"], (double)packet.Arg(1));

                var regions = MapPayload.Decode((string)packet.Arg(0));
                CollectionAssert.AreEqual(c["regions"].Select(r => (string)r).ToList(), regions.Keys.ToList());
                Assert.AreEqual((int)c["tiles"], regions.Values.Sum(list => ((List<object>)list).Count));
                Assert.AreEqual(text, JsJson.Stringify(regions), "region JSON round trip");
            }
        }

        [Test]
        public void GuestSessionStartSendsWhatTheBrowserSent()
        {
            var (connection, transport, clock) = Open(LoginRequest.Guest());

            transport.Receive("[[0,null]]");
            transport.Receive("[[1,{\"type\":\"client\",\"instance\":\"0-826814194\",\"serverId\":1,\"serverTime\":312568}]]");

            // Frames 1-2 of Tools/fixtures/recordings/2026-10-09_browser_tutorial.jsonl.
            CollectionAssert.AreEqual(new[] { "[1,{\"gVer\":\"0.1.1\"}]", "[2,{\"opcode\":2}]" }, transport.Sent);
            Assert.AreEqual("0-826814194", connection.Instance);
            Assert.AreEqual(1, connection.ServerId);
            Assert.AreEqual(1000 - 312568, connection.TimeOffset);
        }

        [Test]
        public void LoginAndRegisterMessagesKeepTheOriginalFieldOrder()
        {
            var (_, login, _) = Open(LoginRequest.Login("Tester", "secret"));
            login.Receive("[[0,null]]");
            login.Receive("[[1,{\"type\":\"client\",\"instance\":\"0-1\",\"serverId\":1,\"serverTime\":5}]]");
            Assert.AreEqual("[2,{\"opcode\":0,\"username\":\"Tester\",\"password\":\"secret\"}]", login.Sent[1]);

            var (_, register, _) = Open(LoginRequest.Register("Tester", "secret", "a@b.c"));
            register.Receive("[[0,null]]");
            register.Receive("[[1,{\"type\":\"client\",\"instance\":\"0-1\",\"serverId\":1,\"serverTime\":5}]]");
            Assert.AreEqual("[2,{\"opcode\":1,\"username\":\"Tester\",\"password\":\"secret\",\"email\":\"a@b.c\"}]", register.Sent[1]);
        }

        [Test]
        public void NonClientHandshakeStopsTheLogin()
        {
            var (connection, transport, _) = Open(LoginRequest.Guest());
            var statuses = new List<string>();
            connection.StatusChanged += statuses.Add;

            transport.Receive("[[1,{\"type\":\"hub\"}]]");

            Assert.IsEmpty(transport.Sent);
            CollectionAssert.AreEqual(new[] { "Invalid client" }, statuses);
        }

        [Test]
        public void RejectionReasonIsReportedBeforeDisconnection()
        {
            var (connection, transport, _) = Open(LoginRequest.Guest());
            var events = new List<string>();
            connection.Rejected += (reason, message) => events.Add($"rejected {reason}: {message}");
            connection.Disconnected += (code, reason) => events.Add($"disconnected {code}");

            transport.CloseFromServer(1010, "invalidlogin");

            CollectionAssert.AreEqual(new[]
            {
                "rejected invalidlogin: You have entered the wrong username or password.",
                "disconnected 1010"
            }, events);
        }

        [Test]
        public void PlainCloseIsNotARejection()
        {
            var (connection, transport, _) = Open(LoginRequest.Guest());
            var rejected = false;
            connection.Rejected += (_, __) => rejected = true;

            transport.CloseFromServer(1010, "");
            transport.CloseFromServer(1000, "invalidlogin");

            Assert.IsFalse(rejected);
            Assert.AreEqual(CloseReasons.Unknown, CloseReasons.Describe("something-new"));
        }

        [Test]
        public void PingIsAnsweredAndSyncMovesTheTimeOffset()
        {
            var (connection, transport, clock) = Open(LoginRequest.Guest());

            transport.Receive("[[18,0]]");
            Assert.AreEqual("[18,[1]]", transport.Sent.Last());

            clock.Now = 5000.75;
            transport.Receive("[[18,2,{\"timestamp\":1234.5}]]");
            Assert.AreEqual(5000 - 1234.5, connection.TimeOffset);

            transport.Receive("[[18,2,{\"timestamp\":0}]]"); // ignored like `if (!info?.timestamp) return`
            Assert.AreEqual(5000 - 1234.5, connection.TimeOffset);

            clock.Now = 9000.9;
            Assert.AreEqual(5235, connection.ServerTimestamp()); // ~~(9000.9 - 3765.5)
        }

        [Test]
        public void PacketsBeforeOpenAreIgnored()
        {
            var transport = new FakeTransport();
            var connection = new GameConnection(transport, new FakeClock());
            var received = 0;
            connection.PacketReceived += _ => received++;

            connection.Connect("ws://test", LoginRequest.Guest());
            transport.Receive("[[0,null]]"); // socket.ts: `if (!this.listening) return;`

            Assert.AreEqual(0, received);
        }

        static (GameConnection, FakeTransport, FakeClock) Open(LoginRequest login)
        {
            var transport = new FakeTransport();
            var clock = new FakeClock { Now = 1000.6 };
            var connection = new GameConnection(transport, clock);
            connection.Connect("ws://test", login);
            transport.OpenFromServer();
            return (connection, transport, clock);
        }

        static string Sha256(string text)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }

        sealed class FakeClock : IClientClock
        {
            public double Now { get; set; }
        }

        /// <summary>Raises events immediately; frames sent by the client are collected in <see cref="Sent"/>.</summary>
        sealed class FakeTransport : IWebSocketTransport
        {
            public readonly List<string> Sent = new List<string>();

            public TransportState State { get; private set; }

            public event Action Opened;
            public event Action<string> MessageReceived;
            public event Action<string> Error;
            public event Action<int, string> Closed;

            public void Connect(string url) => State = TransportState.Connecting;

            public void OpenFromServer()
            {
                State = TransportState.Open;
                Opened?.Invoke();
            }

            public void Receive(string frame) => MessageReceived?.Invoke(frame);

            public void CloseFromServer(int code, string reason)
            {
                State = TransportState.Closed;
                Closed?.Invoke(code, reason);
            }

            public void Fail(string message) => Error?.Invoke(message);

            public void Send(string text)
            {
                if (State == TransportState.Open) Sent.Add(text);
            }

            public void Close() => State = TransportState.Closed;

            public void DispatchEvents() { }

            public void Dispose() { }
        }
    }
}
