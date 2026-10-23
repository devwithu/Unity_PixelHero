using System.Net.WebSockets;
using System.Text;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Js;
using DiscordNet = Discord;
using DiscordSocket = Discord.WebSocket;

namespace PixelHero.Server.Network
{
    // The hub connection, the HTTP API and Discord. Each one is off unless its setting is on (HUB_ENABLED, API_ENABLED,
    // DISCORD_ENABLED), and then does nothing at all, as in the original: no timer, no socket, no log line. Work from
    // their sockets and HTTP calls reaches game code through Node.Post, one macrotask at a time like Node's I/O.

    /// <summary>
    /// network/api.ts: the server's own HTTP API (GET / with the server's information), and the hub's isOnline query.
    /// Sentry is not ported.
    /// </summary>
    public sealed class Api
    {
        /// <summary>How requests to the hub travel (axios): HTTP, or the test harness's virtual network.</summary>
        public static IHttpTransport Http = new HttpTransport();

        readonly World world;

        public Api(World world)
        {
            this.world = world;

            var apiEnabled = Config.Current.apiEnabled || Config.Current.hubEnabled;

            // API must be initialized if the hub is enabled.
            if (apiEnabled)
            {
                var app = new Express();

                HandleRouter(app);

                app.Listen(Config.Current.apiPort, () => Log.Notice($"{Config.Current.name} API has successfully initialized."));
            }
        }

        /// <summary>
        /// Default routing for the server API. We just display some basic infomration about the server, such as the
        /// name, port, game version, and the amount of players currently online.
        /// </summary>
        void HandleRouter(Express router) =>
            router.Get("/", (_, response) => response.Json(new JsObject(
                "name", Config.Current.name,
                "port", Config.Current.port, // Sends the server port.
                "gameVersion", Config.Current.gver,
                "maxPlayers", Config.Current.maxPlayers,
                "playerCount", world.GetPopulation())));

        /// <summary>Checks whether the player is online on another server (asks the hub).</summary>
        public void IsPlayerOnline(string username, Action<bool> callback)
        {
            if (!Config.Current.hubEnabled)
            {
                callback(false);
                return;
            }

            var url = Utils.GetUrl(Config.Current.hubHost, Config.Current.hubPort, "isOnline");
            var data = new JsObject(
                "hubAccessToken", Config.Current.hubAccessToken,
                "serverId", Config.Current.serverId,
                "username", username);

            // axios.post(url, data).then(({ data }) => callback(data.online)).catch(log): an error in the callback
            // also ends in the catch.
            Http.Post(url, data, result =>
            {
                try
                {
                    callback(JsData.Truthy(result is JsObject json ? json["online"] : null));
                }
                catch
                {
                    Log.Error("Could not send `isOnline` to hub.");
                }
            }, () => Log.Error("Could not send `isOnline` to hub."));
        }
    }

    /// <summary>axios.post: the response body (parsed JSON) or a failure, each delivered on the event loop.</summary>
    public interface IHttpTransport
    {
        void Post(string url, JsObject data, Action<object> resolve, Action reject);
    }

    /// <summary><see cref="IHttpTransport"/> over HttpClient; the result reaches the event loop through Node.Post.</summary>
    public sealed class HttpTransport : IHttpTransport
    {
        static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

        public void Post(string url, JsObject data, Action<object> resolve, Action reject)
        {
            var body = JsJson.Stringify(data);
            var post = Node.Post;

            Task.Run(async () =>
            {
                object result;

                try
                {
                    using var response = await Client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
                    response.EnsureSuccessStatusCode();
                    result = JsData.Parse(await response.Content.ReadAsStringAsync());
                }
                catch
                {
                    post(reject);
                    return;
                }

                post(() => resolve(result));
            });
        }
    }

    /// <summary>
    /// network/client.ts: the server's WebSocket connection to the hub. The handshake goes out as soon as it opens;
    /// messages from the hub go to the global incoming controller; a failed or lost connection is retried after 5 s.
    /// </summary>
    public sealed class HubClient
    {
        /// <summary>How the connection to the hub travels (websocket): a WebSocket, or the test harness's virtual network.</summary>
        public static IHubTransport Transport = new WebSocketHubTransport();

        readonly string address = $"ws://{Config.Current.hubWsHost}:{JsNumber.ToJsString(Config.Current.hubWsPort)}";

        readonly World world;

        Controllers.Incoming incoming;

        /// <summary>The open connection (websocket's `connection`), or null.</summary>
        IHubLink connection;

        public HubClient(World world)
        {
            this.world = world;

            if (!Config.Current.hubEnabled) return;

            // Initialize the connection to the hub.
            Connect();

            incoming = new Controllers.Incoming(world);
        }

        /// <summary>webSocket.connect(address): 'connect' or 'connectFailed' arrives later on the event loop.</summary>
        void Connect() => Transport.Connect(address, HandleOpen, HandleError);

        /// <summary>
        /// Used to notify us when a connection to the hub has been successfully established. Once we open the
        /// connection we can begin the handshake.
        /// </summary>
        void HandleOpen(IHubLink connection)
        {
            Log.Debug("Established a web socket connection to the hub!");

            // Store the connection for later use.
            this.connection = connection;

            connection.OnMessage(HandleMessage);
            connection.OnClose(HandleClose);

            // Relay the handshake packet to the connection immediately.
            connection.Send(JsJson.Stringify(new HandshakePacket(new JsObject(
                "type", "hub",
                "gVer", Config.Current.gver,
                "name", Config.Current.name,
                "serverId", Config.Current.serverId,
                // Port fix (ANALYSIS.md §9 #13): the token the hub checks (the original sent ACCESS_TOKEN, unchecked).
                "accessToken", Config.Current.hubAccessToken,
                "remoteHost", Config.Current.remoteServerHost,
                "port", Config.Current.port,
                "players", world.entities.GetPlayerUsernames().ConvertAll(name => (object)name),
                "maxPlayers", Config.Current.maxPlayers)).Serialize()));
        }

        /// <summary>Contains the packet information from the hub (text messages only).</summary>
        void HandleMessage(string info)
        {
            // We received a raw UTF8 message from the hub, we don't really handle these.
            if (!info.StartsWith("[")) return;

            var data = JsData.Parse(info);

            // Handle incoming data from the hub.
            incoming.Handle(data);
        }

        /// <summary>We will continuously attempt to reconnect to the hub until we are able to connect.</summary>
        void HandleError()
        {
            Log.Error("Could not connect to hub WebSocket server, attempting again in 5 seconds...");

            // Retry connection.
            Node.SetTimeout(Connect, 5000);
        }

        /// <summary>If the hub goes under then we will attempt to reconnect to the hub.</summary>
        void HandleClose()
        {
            Log.Error("Lost connection to hub WebSocket server, attempting again in 5 seconds...");

            // Retry connection.
            Node.SetTimeout(Connect, 5000);
        }

        /// <summary>Serializes a packet and stringifies then sends it to websocket connection.</summary>
        public void Send(Packet packet)
        {
            if (connection is not { Connected: true }) return;

            connection.Send(JsJson.Stringify(packet.Serialize()));
        }

        /// <summary>
        /// Relays a message to a player on another server (if they're online). The hub routes the relay packet
        /// ([username, packet]) to the server the player is on.
        /// </summary>
        public void Relay(string username, Packet packet) => Send(new RelayPacket(username, packet));
    }

    /// <summary>websocket's client: connect(address) ends in 'connect' with the connection or in 'connectFailed'.</summary>
    public interface IHubTransport
    {
        void Connect(string address, Action<IHubLink> connect, Action connectFailed);
    }

    /// <summary>websocket's connection: connected, send(), and its 'message' (text) and 'close' events.</summary>
    public interface IHubLink
    {
        bool Connected { get; }

        void Send(string text);

        void OnMessage(Action<string> handler);

        void OnClose(Action handler);
    }

    /// <summary><see cref="IHubTransport"/> over ClientWebSocket; every event reaches the event loop through Node.Post.</summary>
    public sealed class WebSocketHubTransport : IHubTransport
    {
        public void Connect(string address, Action<IHubLink> connect, Action connectFailed)
        {
            var post = Node.Post;

            Task.Run(async () =>
            {
                var socket = new ClientWebSocket();

                try
                {
                    await socket.ConnectAsync(new Uri(address), CancellationToken.None);
                }
                catch
                {
                    socket.Dispose();
                    post(connectFailed);
                    return;
                }

                var link = new HubConnection(socket);

                post(() => connect(link));

                // Receive until the connection closes; every text message is one macrotask.
                await link.Receive(text => post(() => link.Message(text)));

                post(() =>
                {
                    link.connected = false;
                    link.Closed();
                });
            });
        }
    }

    /// <summary>One WebSocket connection (a server's to the hub): ordered sends, a receive loop.</summary>
    public sealed class HubConnection : IHubLink
    {
        Action<string> _onMessage;
        Action _onClose;

        public bool Connected => connected;

        public void OnMessage(Action<string> handler) => _onMessage = handler;

        public void OnClose(Action handler) => _onClose = handler;

        internal void Message(string text) => _onMessage?.Invoke(text);

        internal void Closed() => _onClose?.Invoke();

        readonly WebSocket _socket;
        Task _sending = Task.CompletedTask;
        readonly object _lock = new();

        public bool connected = true;

        public HubConnection(WebSocket socket) => _socket = socket;

        /// <summary>Queues a text message; messages leave in the order they were sent.</summary>
        public void Send(string message)
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            lock (_lock)
                _sending = _sending.ContinueWith(async _ =>
                {
                    try
                    {
                        if (_socket.State == WebSocketState.Open)
                            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch
                    {
                        // A failed send ends with the receive loop's close.
                    }
                }).Unwrap();
        }

        /// <summary>Reads messages until the connection closes or fails.</summary>
        public async Task Receive(Action<string> onText)
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();

            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    var result = await _socket.ReceiveAsync(buffer, CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (_socket.State == WebSocketState.CloseReceived)
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                        break;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    var bytes = message.ToArray();
                    message.SetLength(0);

                    if (result.MessageType == WebSocketMessageType.Text) onText(Encoding.UTF8.GetString(bytes));
                }
            }
            catch
            {
                // The connection was lost.
            }
            finally
            {
                _socket.Dispose();
            }
        }

        public void Close()
        {
            connected = false;
            _ = _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                .ContinueWith(_ => { });
        }
    }

    /// <summary>
    /// common/api/discord.ts: messages in the configured channel become global chat (through the callback), and the
    /// game's messages go to the channel. The bot needs to send messages and manage the channel (its topic).
    /// </summary>
    public sealed class Discord
    {
        DiscordSocket.DiscordSocketClient client;

        Action<string, string, string> messageCallback;

        public Discord(bool skip = false)
        {
            if (!Config.Current.discordEnabled || skip) return;

            client = new DiscordSocket.DiscordSocketClient(new DiscordSocket.DiscordSocketConfig
            {
                GatewayIntents = DiscordNet.GatewayIntents.Guilds | DiscordNet.GatewayIntents.GuildMessages |
                                 DiscordNet.GatewayIntents.MessageContent | DiscordNet.GatewayIntents.GuildPresences
            });

            var post = Node.Post;

            // Discord is successfully connected.
            client.Ready += () =>
            {
                post(() => Log.Notice("Successfully connected to the Discord server."));
                return Task.CompletedTask;
            };

            // Receive any message acitivty from the Discord server.
            client.MessageReceived += message =>
            {
                post(() => HandleMessage(message));
                return Task.CompletedTask;
            };

            // Connect to the Discord server.
            Task.Run(async () =>
            {
                try
                {
                    await client.LoginAsync(DiscordNet.TokenType.Bot, Config.Current.discordBotToken);
                    await client.StartAsync();
                }
                catch (Exception error)
                {
                    // discord.js rejects login() here (an unhandled rejection); the C# server logs it.
                    post(() => Log.Error($"Could not log in to Discord: {error.Message}"));
                }
            });
        }

        /// <summary>Handles an incoming message from the Discord server.</summary>
        void HandleMessage(DiscordSocket.SocketMessage message)
        {
            try
            {
                if (message.Channel.Id.ToString() != Config.Current.discordChannelId) return;
                if (client.CurrentUser?.Id == message.Author.Id) return; // Skip if it's the bot.
                if (string.IsNullOrEmpty(message.Content)) return; // Picture sent or something.

                var source = $"[Discord | {message.Author.Username}]";
                var text = $"@goldenrod@{message.Content}";

                messageCallback?.Invoke(source, text, "tomato");
            }
            catch
            {
                Log.Error("An error has occurred while handling a message from the Discord server.");
            }
        }

        /// <summary>Sets the topic of the primary Discord channel.</summary>
        public void SetTopic(string message)
        {
            if (string.IsNullOrEmpty(message) || !Config.Current.discordEnabled || client == null) return;

            try
            {
                var channel = GetChannel();

                if (channel != null) Catch(channel.ModifyAsync(properties => properties.Topic = message));
            }
            catch
            {
                Log.Error("An error has occurred while setting the Discord channel topic.");
            }
        }

        /// <summary>
        /// Formats a message and then sends the raw string to the Discord server.
        /// </summary>
        /// <param name="source">The player username that is sending the message.</param>
        /// <param name="text">The message that the player is sending.</param>
        /// <param name="serverName">The `serverName` the message is coming from (config.name when null).</param>
        /// <param name="withArrow">If to add an arrow ASCII character to the message.</param>
        public void SendMessage(string source, string text, string serverName = null, bool withArrow = true)
        {
            if (string.IsNullOrEmpty(source) || !Config.Current.discordEnabled) return;

            serverName ??= Config.Current.name;

            SendRawMessage($"**[{serverName}]** {source}{(withArrow ? " »" : "")} {text}");
        }

        /// <summary>Sends a raw message string to the Discord server.</summary>
        public void SendRawMessage(string message)
        {
            if (client == null) return;

            try
            {
                var channel = GetChannel();

                if (channel != null) Catch(channel.SendMessageAsync(message));
            }
            catch
            {
                Log.Error("An error has occurred while sending a message to the Discord server.");
            }
        }

        /// <summary>A callback for when the discord server requests message to be broadcast.</summary>
        public void OnMessage(Action<string, string, string> callback) => messageCallback = callback;

        /// <returns>The default channel that the bot is assigned to (null until the bot has it cached).</returns>
        DiscordSocket.SocketTextChannel GetChannel() =>
            ulong.TryParse(Config.Current.discordChannelId, out var id) ? client.GetChannel(id) as DiscordSocket.SocketTextChannel : null;

        /// <summary>promise.catch((error) => log.error(error)).</summary>
        static void Catch(Task task)
        {
            var post = Node.Post;
            task.ContinueWith(t => post?.Invoke(() => Log.Error(t.Exception?.GetBaseException().Message)),
                TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
