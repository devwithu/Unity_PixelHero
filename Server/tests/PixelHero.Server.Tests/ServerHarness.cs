using System.IO.Compression;
using System.Text;
using PixelHero.Common;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// Runs the C# server under the rules of Tools/fixtures/server/harness.mjs (the harness that records the
    /// original): Park–Miller random, virtual clock (Date.now() = epoch + t, performance.now() = t + 1000, UTC),
    /// timers then inputs at each time, uWS socket semantics, and a crash stops the run.
    /// </summary>
    public sealed class ServerHarness
    {
        public sealed class Output
        {
            public double t;
            public int? conn;
            public string kind; // frame | close | crash
            public string text;
            public double code;
            public string reason;
            public string error;
        }

        public readonly EventLoop Loop;
        public readonly ParkMiller Random;
        public readonly ServerMain Main;
        public readonly List<Output> Outputs = new();
        public readonly List<string> Logs = new();
        public readonly JsObject Database;
        public string Crash;

        readonly Dictionary<int, FakeSocket> _sockets = new();

        sealed class FakeSocket : ISocket
        {
            readonly ServerHarness _harness;
            readonly int _conn;

            public FakeSocket(ServerHarness harness, int conn, string address)
            {
                _harness = harness;
                _conn = conn;
                RemoteAddressText = address;
            }

            public string RemoteAddressText { get; }
            public string ForwardedAddress => "";
            public bool Closed { get; set; }
            public string Instance { get; set; }

            void Check()
            {
                if (Closed) throw new InvalidOperationException("Invalid access of closed uWS.WebSocket/SSLWebSocket.");
            }

            public void Send(string message)
            {
                Check();
                _harness.Outputs.Add(new Output { t = _harness.Loop.Now, conn = _conn, kind = "frame", text = message });
            }

            public void End(double code, string reason)
            {
                Check();
                Closed = true;
                _harness.Outputs.Add(new Output { t = _harness.Loop.Now, conn = _conn, kind = "close", code = code, reason = reason ?? "" });
                _harness.Main.socketHandler.sockets.HandleClose(Instance);
            }
        }

        public static string DataDirectory => Path.Combine(TestPaths.Data, "server", "data");

        /// <param name="database">The memory database's collections, or null to run without a database.</param>
        public ServerHarness(long seed = 1, double epoch = 1_700_000_000_000, JsObject config = null, JsObject database = null)
        {
            Loop = new EventLoop(epoch) { UncaughtException = error => Crash ??= Describe(error) };
            Random = new ParkMiller(seed);
            Node.Install(Loop, Random);
            Node.TimeZone = TimeZoneInfo.Utc;

            Log.Sink = (level, message) =>
            {
                if (level >= Log.Level.Warning) Logs.Add($"{Loop.Now} {level}: {message}");
            };

            // The harness config: .env.defaults and SKIP_DATABASE off when there is a database.
            var overrides = new JsObject("skipDatabase", database == null);
            if (config != null) foreach (var pair in config) overrides.Set(pair.Key, pair.Value);

            ServerMain.ResetState(DataDirectory, Config.Load(Path.Combine(TestPaths.Data, ".env.defaults"), null, overrides));
            Filter.Load(Path.Combine(TestPaths.Data, "common", "text", "profanity.json"));
            I18n.Load(Path.Combine(TestPaths.Root, "Assets", "PixelHero", "Localization", "server.en.json"));

            Database = database == null ? null : (JsObject)JsData.Clone(database);
            IDocumentStore store = new MemoryDocumentStore(Database ?? new JsObject(), Database != null);

            ServerMain main = null;
            Loop.Run(() => main = new ServerMain(store));
            Main = main;

            // Let the database connection settle and the world start (all at t = 0).
            RunUntil(0);
        }

        static string Describe(Exception error) => error switch
        {
            JsTypeError => "TypeError: " + error.Message,
            _ => $"{error.GetType().Name}: {error.Message}"
        };

        public void RunUntil(double t)
        {
            if (Crash != null) return;
            Loop.RunUntil(t);
        }

        bool Macrotask(Action action)
        {
            if (Crash != null) return false;
            Loop.Run(action);
            return true;
        }

        public void Open(int conn, string address = "127.0.0.1")
        {
            var socket = new FakeSocket(this, conn, address);
            _sockets[conn] = socket;
            Macrotask(() => Main.socketHandler.sockets.HandleConnection(socket));
        }

        public void Message(int conn, string text)
        {
            var socket = _sockets[conn];
            if (socket.Closed) return;
            Macrotask(() => Main.socketHandler.sockets.HandleMessage(socket.Instance, text));
        }

        public void Close(int conn, double code = 1000)
        {
            var socket = _sockets[conn];
            if (socket.Closed) return;
            socket.Closed = true;
            Macrotask(() => Main.socketHandler.sockets.HandleClose(socket.Instance));
        }

        /// <summary>Runs a recorded scenario's inputs, then its end time; returns the outputs (with a crash entry).</summary>
        public List<Output> Replay(List<object> inputs, double end)
        {
            foreach (JsObject input in inputs)
            {
                if (Crash != null) break;
                RunUntil(input.Num("t").Value);
                if (Crash != null) break;

                var conn = (int)input.Num("conn").Value;
                switch (input.Str("kind"))
                {
                    case "open":
                        Open(conn, input.Str("address"));
                        break;
                    case "message":
                        Message(conn, input.Str("text"));
                        break;
                    case "close":
                        Close(conn, input.Num("code") ?? 1000);
                        break;
                }
            }

            RunUntil(end);

            if (Crash != null) Outputs.Add(new Output { t = Loop.Now, kind = "crash", error = Crash });

            return Outputs;
        }

        /// <summary>
        /// A frame in comparable form: Map packets (gzip bytes differ between Node and .NET) have their data replaced
        /// by the decompressed JSON.
        /// </summary>
        public static string Normalize(string frame)
        {
            if (!frame.StartsWith('[')) return frame;
            if (JsJson.Parse(frame) is not List<object> packets) return frame;

            var changed = false;
            foreach (var entry in packets)
                if (entry is List<object> packet && packet.Count > 1 && packet[0] is double id && id == (double)Packets.Map &&
                    packet[1] is string data)
                {
                    packet[1] = "gunzip:" + Gunzip(data);
                    changed = true;
                }

            return changed ? JsJson.Stringify(packets) : frame;
        }

        static string Gunzip(string base64)
        {
            if (base64.Length == 0) return "";
            using var input = new MemoryStream(Convert.FromBase64String(base64));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
