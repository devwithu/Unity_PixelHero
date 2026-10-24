using PixelHero.Hub.Network;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Js;
using PixelHero.Server.Network;
using HubMain = PixelHero.Hub.Main;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// Runs a C# hub and C# servers together under the cluster rules of Tools/fixtures/server/harness.mjs: one virtual
    /// clock, one Park–Miller random, one in-memory database (each node with its own store and ObjectId counter, as
    /// each original node has its own copy of the mongodb stand-in), nodes started in order, and the same virtual
    /// network between the hub and the servers (delivery one immediate later, recorded at the same points).
    ///
    /// Each original node has its own module state; the C# port keeps the per-server part of it in statics
    /// (Config.Current, Utils' counter and event flags). A <see cref="Realm"/> holds one node's copy, and the event
    /// loop restores the realm a callback was scheduled from before it runs (EventLoop.CaptureContext).
    /// </summary>
    public sealed class ClusterHarness : IDisposable
    {
        public sealed class Output
        {
            public double t;
            public string node;
            public int? conn;
            public string kind; // frame | close | crash | link | linkclose | http | httpResponse
            public string text;
            public double code;
            public string reason;
            public string error;
            public string from, to, method, path;
        }

        /// <summary>One node's static state.</summary>
        public sealed class Realm
        {
            public static Realm Current;

            Config _config;
            double _counter = -1;
            bool _doubleLumberjacking, _doubleMining;

            public Realm(Config config) => _config = config;

            public void Enter()
            {
                if (Current == this) return;
                Current?.Save();

                Config.Current = _config;
                Utils.counter = _counter;
                Utils.doubleLumberjacking = _doubleLumberjacking;
                Utils.doubleMining = _doubleMining;

                Current = this;
            }

            void Save()
            {
                _config = Config.Current;
                _counter = Utils.counter;
                _doubleLumberjacking = Utils.doubleLumberjacking;
                _doubleMining = Utils.doubleMining;
            }
        }

        public sealed class Node
        {
            public string name;
            public string kind;
            public Realm realm;
            public ServerMain server;
            public HubMain hub;
            public Handler handler;
        }

        public readonly EventLoop Loop;
        public readonly ParkMiller Random;
        public readonly List<Node> Nodes = new();
        public readonly List<Output> Outputs = new();
        public readonly List<string> Logs = new();
        public readonly JsObject Database;
        public string Crash;

        readonly Dictionary<string, PlayerSocket> _sockets = new();
        readonly Dictionary<double, Node> _ws = new();
        readonly Dictionary<double, (Node node, Express app)> _http = new();

        /// <param name="nodes">[{name, kind: hub | server, config}] in start order.</param>
        /// <param name="database">The shared in-memory database's collections, or null to run without one.</param>
        public ClusterHarness(List<object> nodes, long seed = 1, double epoch = 1_700_000_000_000, JsObject database = null)
        {
            Loop = new EventLoop(epoch)
            {
                UncaughtException = error => Crash ??= Describe(error),
                CaptureContext = () =>
                {
                    var realm = Realm.Current;
                    return realm == null ? null : realm.Enter;
                }
            };
            Random = new ParkMiller(seed);
            PixelHero.Server.Js.Node.Install(Loop, Random);
            PixelHero.Server.Js.Node.TimeZone = TimeZoneInfo.Utc;

            Log.Sink = (level, message) =>
            {
                if (level >= Log.Level.Warning) Logs.Add($"{Loop.Now} {Current?.name} {level}: {message}");
            };

            Filter.Load(Path.Combine(TestPaths.Data, "common", "text", "profanity.json"));
            I18n.Load(Path.Combine(TestPaths.Root, "Assets", "PixelHero", "Localization", "server.en.json"));

            // The virtual network.
            HubClient.Transport = new VirtualHubTransport(this);
            Api.Http = new VirtualHttp(this);
            Express.VirtualListen = (port, app) => _http[port] = (Current, app);
            Handler.VirtualListen = (port, handler) =>
            {
                Current.handler = handler;
                _ws[port] = Current;
            };

            Database = database == null ? null : (JsObject)JsData.Clone(database);
            var collections = Database ?? new JsObject();
            var mobs = JsData.ParseObject(File.ReadAllText(Path.Combine(ServerHarness.DataDirectory, "mobs.json")));
            var dataLoaded = false;

            foreach (JsObject entry in nodes)
            {
                // The harness config: .env.defaults and SKIP_DATABASE off when there is a database. STARTER_ITEMS is
                // empty: the original has no starter items, and the scenarios turn the tutorial off.
                var overrides = new JsObject("skipDatabase", database == null, "starterItems", "");
                if (entry.Obj("config") is { } config) foreach (var pair in config) overrides.Set(pair.Key, pair.Value);

                var node = new Node
                {
                    name = entry.Str("name"),
                    kind = entry.Str("kind"),
                    realm = new Realm(Config.Load(Path.Combine(TestPaths.Data, ".env.defaults"), null, overrides))
                };
                Nodes.Add(node);

                if (Crash != null) break;

                var store = new MemoryDocumentStore(collections, Database != null);

                Loop.Run(() =>
                {
                    node.realm.Enter();

                    if (node.kind == "hub") node.hub = new HubMain(store, mobs);
                    else
                    {
                        // The data tables are the same for every server; load them once.
                        if (!dataLoaded) ServerMain.ResetState(ServerHarness.DataDirectory, Config.Current);
                        else Utils.Reset();
                        dataLoaded = true;

                        node.server = new ServerMain(store);
                    }
                });
            }

            // Let the database connections settle and the worlds start (all at t = 0).
            RunUntil(0);
        }

        /// <summary>The node whose realm is current.</summary>
        Node Current => Nodes.FirstOrDefault(node => node.realm == Realm.Current);

        Node Get(string name) => Nodes.First(node => node.name == name);

        public void Dispose()
        {
            HubClient.Transport = new WebSocketHubTransport();
            Api.Http = new HttpTransport();
            Express.VirtualListen = null;
            Handler.VirtualListen = null;
            Realm.Current = null;
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

        /// <summary>harness.mjs `later`: one immediate later.</summary>
        void Later(Action action) => Loop.SetImmediate(action);

        void Record(Output output)
        {
            output.t = Loop.Now;
            Outputs.Add(output);
        }

        // ---- players' sockets ----

        sealed class PlayerSocket : ISocket
        {
            readonly ClusterHarness _harness;
            readonly Node _node;
            readonly int _conn;

            public PlayerSocket(ClusterHarness harness, Node node, int conn, string address)
            {
                _harness = harness;
                _node = node;
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
                _harness.Record(new Output { node = _node.name, conn = _conn, kind = "frame", text = message });
            }

            public void End(double code, string reason)
            {
                Check();
                Closed = true;
                _harness.Record(new Output { node = _node.name, conn = _conn, kind = "close", code = code, reason = reason ?? "" });
                _node.server.socketHandler.sockets.HandleClose(Instance);
            }
        }

        void Macrotask(Node node, Action action)
        {
            if (Crash != null) return;
            Loop.Run(() =>
            {
                node.realm.Enter();
                action();
            });
        }

        /// <summary>Runs a recorded cluster scenario's inputs, then its end time; returns the outputs.</summary>
        public List<Output> Replay(List<object> inputs, double end)
        {
            foreach (JsObject input in inputs)
            {
                if (Crash != null) break;
                RunUntil(input.Num("t").Value);
                if (Crash != null) break;

                var node = Get(input.Str("node"));
                var conn = (int)input.Num("conn").Value;
                var key = $"{node.name}:{conn}";
                var sockets = node.server.socketHandler.sockets;

                switch (input.Str("kind"))
                {
                    case "open":
                    {
                        var socket = new PlayerSocket(this, node, conn, input.Str("address") ?? "127.0.0.1");
                        _sockets[key] = socket;
                        Macrotask(node, () => sockets.HandleConnection(socket));
                        break;
                    }
                    case "message":
                    {
                        var socket = _sockets[key];
                        if (socket.Closed) break;
                        var text = input.Str("text");
                        Macrotask(node, () => sockets.HandleMessage(socket.Instance, text));
                        break;
                    }
                    case "close":
                    {
                        var socket = _sockets[key];
                        if (socket.Closed) break;
                        socket.Closed = true;
                        Macrotask(node, () => sockets.HandleClose(socket.Instance));
                        break;
                    }
                }
            }

            RunUntil(end);

            if (Crash != null) Outputs.Add(new Output { t = Loop.Now, kind = "crash", error = Crash });

            return Outputs;
        }

        // ---- the virtual network ----

        static double PortOf(string address) => new Uri(address).Port;

        sealed class VirtualHubTransport : IHubTransport
        {
            readonly ClusterHarness _harness;

            public VirtualHubTransport(ClusterHarness harness) => _harness = harness;

            public void Connect(string address, Action<IHubLink> connect, Action connectFailed)
            {
                var from = _harness.Current;

                _harness.Later(() =>
                {
                    if (!_harness._ws.TryGetValue(PortOf(address), out var target))
                    {
                        connectFailed();
                        return;
                    }

                    var connection = new LinkConnection(_harness, from, target);
                    var socket = new LinkSocket(_harness, target, from, connection);
                    connection.socket = socket;

                    target.realm.Enter();
                    target.handler.HandleConnection(socket);

                    from.realm.Enter();
                    connect(connection);
                });
            }
        }

        /// <summary>The client end of a link (websocket's connection).</summary>
        sealed class LinkConnection : IHubLink
        {
            readonly ClusterHarness _harness;
            readonly Node _from, _target;
            Action<string> _onMessage;
            Action _onClose;

            public LinkSocket socket;
            public bool connected = true;

            public LinkConnection(ClusterHarness harness, Node from, Node target)
            {
                _harness = harness;
                _from = from;
                _target = target;
            }

            public bool Connected => connected;

            public void OnMessage(Action<string> handler) => _onMessage = handler;

            public void OnClose(Action handler) => _onClose = handler;

            public void Send(string text)
            {
                _harness.Record(new Output { kind = "link", from = _from.name, to = _target.name, text = text });
                _harness.Later(() =>
                {
                    if (socket.Closed) return;
                    _target.realm.Enter();
                    _target.handler.HandleMessage(socket, text);
                });
            }

            public void Message(string text)
            {
                _from.realm.Enter();
                _onMessage?.Invoke(text);
            }

            public void Closed()
            {
                connected = false;
                _from.realm.Enter();
                _onClose?.Invoke();
            }
        }

        /// <summary>The listening end of a link (a uWS socket on the hub).</summary>
        sealed class LinkSocket : IHubSocket
        {
            readonly ClusterHarness _harness;
            readonly Node _node, _peer;
            readonly LinkConnection _connection;

            public LinkSocket(ClusterHarness harness, Node node, Node peer, LinkConnection connection)
            {
                _harness = harness;
                _node = node;
                _peer = peer;
                _connection = connection;
            }

            public string RemoteAddressText => "127.0.0.1";
            public string Instance { get; set; }
            public bool Closed { get; private set; }

            void Check()
            {
                if (Closed) throw new InvalidOperationException("Invalid access of closed uWS.WebSocket/SSLWebSocket.");
            }

            public void Send(string message)
            {
                Check();
                _harness.Record(new Output { kind = "link", from = _node.name, to = _peer.name, text = message });
                _harness.Later(() =>
                {
                    if (!_connection.connected) return;
                    _connection.Message(message);
                });
            }

            public void End()
            {
                Check();
                Closed = true;
                _harness.Record(new Output { kind = "linkclose", from = _node.name, to = _peer.name });
                _node.handler.HandleClose(this);
                _harness.Later(_connection.Closed);
            }
        }

        sealed class VirtualHttp : IHttpTransport
        {
            readonly ClusterHarness _harness;

            public VirtualHttp(ClusterHarness harness) => _harness = harness;

            public void Post(string url, JsObject data, Action<object> resolve, Action reject)
            {
                var from = _harness.Current;
                var uri = new Uri(url);
                var text = JsJson.Stringify(data);
                _harness._http.TryGetValue(uri.Port, out var target);

                _harness.Record(new Output
                {
                    kind = "http", from = from.name, to = target.node?.name, method = "POST", path = uri.AbsolutePath, text = text
                });

                _harness.Later(() =>
                {
                    var handler = target.app?.Find("POST", uri.AbsolutePath);

                    if (handler == null)
                    {
                        _harness.Later(() =>
                        {
                            from.realm.Enter();
                            reject();
                        });
                        return;
                    }

                    var response = new ExpressResponse
                    {
                        Completed = body =>
                        {
                            _harness.Record(new Output { kind = "httpResponse", from = target.node.name, to = from.name, text = body });
                            _harness.Later(() =>
                            {
                                from.realm.Enter();
                                resolve(JsData.Parse(body));
                            });
                        }
                    };

                    target.node.realm.Enter();
                    handler(new ExpressRequest { method = "POST", path = uri.AbsolutePath, body = JsData.Parse(text) }, response);
                });
            }
        }
    }
}
