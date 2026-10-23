using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuperSocket.Server.Abstractions;
using SuperSocket.Server.Host;
using SuperSocket.WebSocket;
using SuperSocket.WebSocket.Server;

namespace PixelHero.Hub.Network
{
    /// <summary>
    /// network/handler.ts. The hub websocket works as a publisher/subscriber system. It is responsible for
    /// synchronizing events between the servers without relying on ridiculous amount of GET/POST API calls. The
    /// listener is SuperSocket; every socket event runs on the event loop (Node.Post).
    /// </summary>
    public sealed class Handler
    {
        public bool ready;

        readonly JsObject<Connection> connections = new();

        Action<string, Connection> connectionCallback;
        Action<string> disconnectCallback;

        IHost _host;

        /// <summary>
        /// The test harness's virtual network: when set, the handler registers there (port, handler) instead of
        /// listening, and the harness drives <see cref="HandleConnection"/>, <see cref="HandleMessage"/> and
        /// <see cref="HandleClose"/>.
        /// </summary>
        public static Action<double, Handler> VirtualListen;

        public Handler()
        {
            if (VirtualListen != null)
            {
                VirtualListen(Config.Current.hubWsPort, this);
                ready = true;
                return;
            }

            var post = Node.Post;
            var sockets = new ConcurrentDictionary<string, HubSocket>();

            _host = WebSocketHostBuilder.Create(Array.Empty<string>())
                .UseSession<HubSession>()
                .UseSessionHandler(
                    session =>
                    {
                        var socket = new HubSocket((WebSocketSession)session, this);
                        sockets[session.SessionID] = socket;
                        post(() => HandleConnection(socket));
                        return ValueTask.CompletedTask;
                    },
                    (session, _) =>
                    {
                        if (sockets.TryRemove(session.SessionID, out var socket))
                            post(() =>
                            {
                                // A close started by the hub already ran the close handling inside end().
                                if (socket.Closed) return;
                                socket.Closed = true;
                                HandleClose(socket);
                            });
                        return ValueTask.CompletedTask;
                    })
                .UseWebSocketMessageHandler((session, package) =>
                {
                    // uWS hands every message over as a buffer that the hub decodes as UTF-8.
                    var text = package.OpCode == OpCode.Binary ? Encoding.UTF8.GetString(package.Data.ToArray()) : package.Message;

                    if (sockets.TryGetValue(session.SessionID, out var socket))
                        post(() =>
                        {
                            if (!socket.Closed) HandleMessage(socket, text);
                        });

                    return ValueTask.CompletedTask;
                })
                .ConfigureAppConfiguration((_, builder) => builder.AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["serverOptions:name"] = "PixelHeroHub",
                    ["serverOptions:listeners:0:ip"] = "Any",
                    ["serverOptions:listeners:0:port"] = JsNumber.ToJsString(Config.Current.hubWsPort),
                    // maxPayloadLength: 32 * 1024 * 1024.
                    ["serverOptions:maxPackageLength"] = (32 * 1024 * 1024).ToString()
                }))
                .ConfigureLogging((_, logging) => logging.SetMinimumLevel(LogLevel.Warning))
                .Build();

            try
            {
                _host.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException($"Failed to listen on port {JsNumber.ToJsString(Config.Current.hubWsPort)}", error);
            }

            ready = true;
        }

        public void Stop() => _host?.StopAsync().Wait(TimeSpan.FromSeconds(2));

        /// <summary>Grabs a connection based on the instance provided.</summary>
        Connection Get(string instance) => instance == null ? null : connections[instance];

        /// <summary>
        /// Handles receiving a message from a socket. We just log the connection for now, the identification part
        /// comes with the handshake packet in the next step.
        /// </summary>
        public void HandleConnection(IHubSocket socket)
        {
            Log.Notice($"Received a connection from {Utils.BufferToAddress(socket.RemoteAddressText)}");

            var instance = Utils.CreateInstance();

            socket.Instance = instance;

            // We store the connection in the connections object so that we can use it later.
            connections.Set(instance, new Connection(socket));

            // Create a new callback for when the connection has been established.
            connectionCallback?.Invoke(instance, connections[instance]);
        }

        /// <summary>Handles a message from the socket. We decode the message and parse the JSON data.</summary>
        public void HandleMessage(IHubSocket socket, string data)
        {
            var connection = Get(socket.Instance);

            if (connection == null)
            {
                Log.Error($"No connection found for instance {socket.Instance}");
                return;
            }

            try
            {
                var message = JsData.Parse(data);

                connection.messageCallback?.Invoke(message);
            }
            catch (Exception error)
            {
                Log.Error($"Message could not be parsed: {data}.");
                Log.Error(error);
            }
        }

        /// <summary>Handles closing a connection and removing the connection from the connections object.</summary>
        public void HandleClose(IHubSocket socket)
        {
            disconnectCallback?.Invoke(socket.Instance);

            connections.Delete(socket.Instance);
        }

        /// <summary>Callback for when a connection has been established with the hub.</summary>
        public void OnConnection(Action<string, Connection> callback) => connectionCallback = callback;

        /// <summary>Callback for when a connection has been closed.</summary>
        public void OnDisconnect(Action<string> callback) => disconnectCallback = callback;
    }

    /// <summary>Closes the TCP connection after answering a client's close frame, as uWS does (see the server's GameSession).</summary>
    sealed class HubSession : WebSocketSession
    {
        public volatile bool ClosedByServer;

        public override async ValueTask SendAsync(WebSocketPackage message, CancellationToken cancellationToken)
        {
            await base.SendAsync(message, cancellationToken);
            if (message.OpCode == OpCode.Close && !ClosedByServer)
                await CloseAsync(SuperSocket.Connection.CloseReason.RemoteClosing);
        }
    }

    /// <summary>
    /// <see cref="IHubSocket"/> over a SuperSocket session, used on the event loop: End() marks the socket closed and
    /// runs the close handling at once; frames leave in order from one pump task.
    /// </summary>
    sealed class HubSocket : IHubSocket
    {
        readonly WebSocketSession _session;
        readonly Handler _handler;
        readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        public HubSocket(WebSocketSession session, Handler handler)
        {
            _session = session;
            _handler = handler;

            var endPoint = session.RemoteEndPoint as IPEndPoint;
            RemoteAddressText = endPoint?.Address.ToString() ?? "";

            _ = PumpAsync();
        }

        public string RemoteAddressText { get; }

        public bool Closed { get; set; }

        public string Instance { get; set; }

        void Check()
        {
            if (Closed) throw new InvalidOperationException("Invalid access of closed uWS.WebSocket/SSLWebSocket.");
        }

        public void Send(string message)
        {
            Check();
            _outgoing.Writer.TryWrite(message);
        }

        public void End()
        {
            Check();
            Closed = true;

            if (_session is HubSession hub) hub.ClosedByServer = true;
            _outgoing.Writer.TryWrite(null);

            // uWS calls the close handler from inside end().
            _handler.HandleClose(this);
        }

        async Task PumpAsync()
        {
            try
            {
                await foreach (var text in _outgoing.Reader.ReadAllAsync())
                {
                    if (text == null)
                    {
                        _outgoing.Writer.TryComplete();
                        await _session.CloseAsync(CloseReason.NormalClosure, "", CancellationToken.None);
                        return;
                    }

                    await _session.SendAsync(text);
                }
            }
            catch (Exception)
            {
                // The session is gone; SuperSocket reports the close through the session handler.
                _outgoing.Writer.TryComplete();
            }
        }
    }
}
