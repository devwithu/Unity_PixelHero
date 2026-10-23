using System;
using System.Collections.Generic;
using System.Diagnostics;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Network
{
    /// <summary>How the player logs in (client app.ts: guest, registering, or logging in).</summary>
    public sealed class LoginRequest
    {
        public Opcodes.Login Mode;
        public string Username = "";
        public string Password = "";
        public string Email = "";

        public static LoginRequest Guest() => new LoginRequest { Mode = Opcodes.Login.Guest };

        public static LoginRequest Login(string username, string password) =>
            new LoginRequest { Mode = Opcodes.Login.Login, Username = username, Password = password };

        public static LoginRequest Register(string username, string password, string email) =>
            new LoginRequest { Mode = Opcodes.Login.Register, Username = username, Password = password, Email = email };
    }

    /// <summary>The client's <c>performance.now()</c>: milliseconds since the client started, with fractions.</summary>
    public interface IClientClock
    {
        double Now { get; }
    }

    public sealed class StopwatchClock : IClientClock
    {
        readonly Stopwatch _watch = Stopwatch.StartNew();
        public double Now => _watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// The connection to the original game server: client/src/network/socket.ts (frames, close reasons),
    /// messages.ts (one handler per packet id) and the session start of connection.ts
    /// (Connected → Handshake → Login/Register/Guest). Everything after Welcome belongs to game code,
    /// which registers handlers with <see cref="On"/> and sends with <see cref="Send"/>.
    /// Call <see cref="Update"/> once per frame; all events and handlers run inside it.
    /// </summary>
    public sealed class GameConnection : IDisposable
    {
        readonly IWebSocketTransport _transport;
        readonly IClientClock _clock;
        readonly Dictionary<Packets, Action<Packet>> _handlers = new Dictionary<Packets, Action<Packet>>();

        LoginRequest _login;
        bool _listening;

        /// <summary>Entity instance of our player, from the Handshake.</summary>
        public string Instance { get; private set; }

        public int ServerId { get; private set; }

        /// <summary>
        /// connection.ts: <c>~~performance.now() - serverTime</c> from the Handshake, later replaced by Network Sync.
        /// A double, because the Sync timestamp is the server's unrounded performance.now() (server player.ts:1210).
        /// </summary>
        public double TimeOffset { get; private set; }

        /// <summary>The Step timestamp (player/handler.ts): <c>~~(performance.now() - timeOffset)</c>.</summary>
        public int ServerTimestamp() => JsValue.ToInt32(_clock.Now - TimeOffset);

        public IClientClock Clock => _clock;

        /// <summary>Raised for every packet before its handler, for logging and recording.</summary>
        public event Action<Packet> PacketReceived;

        /// <summary>Status line of the login screen (app.ts updateLoader).</summary>
        public event Action<string> StatusChanged;

        /// <summary>
        /// socket.ts handleConnectionError: the socket failed. The argument is the original's development message.
        /// </summary>
        public event Action<string> ConnectionFailed;

        /// <summary>The server rejected us (code 1010): the reason key and the original's English message.</summary>
        public event Action<string, string> Rejected;

        /// <summary>The connection ended (game.ts handleDisconnection), after <see cref="Rejected"/> if any.</summary>
        public event Action<int, string> Disconnected;

        public GameConnection(IWebSocketTransport transport, IClientClock clock = null)
        {
            _transport = transport;
            _clock = clock ?? new StopwatchClock();

            _transport.Opened += HandleOpened;
            _transport.MessageReceived += Receive;
            _transport.Closed += HandleClosed;

            On(Packets.Connected, _ => HandleConnected());
            On(Packets.Handshake, packet => HandleHandshake((Dictionary<string, object>)packet.Arg(0)));
            On(Packets.Network, packet => HandleNetwork((Opcodes.Network)(int)(double)packet.Arg(0), packet.Arg(1) as Dictionary<string, object>));
        }

        public TransportState State => _transport.State;

        /// <summary>messages.ts on*: one handler per packet id; a later registration replaces the earlier one.</summary>
        public void On(Packets id, Action<Packet> handler) => _handlers[id] = handler;

        public void Connect(string url, LoginRequest login)
        {
            _login = login;
            _listening = false;
            _transport.Error += HandleError;
            _transport.Connect(url);
        }

        public void Send(Packets id, object data = null)
        {
            if (_transport.State != TransportState.Open) return;
            _transport.Send(PacketFrames.Serialize(id, data));
        }

        public void Close() => _transport.Close();

        public void Update() => _transport.DispatchEvents();

        public void Dispose()
        {
            _transport.Opened -= HandleOpened;
            _transport.MessageReceived -= Receive;
            _transport.Closed -= HandleClosed;
            _transport.Error -= HandleError;
            _transport.Dispose();
        }

        void HandleOpened()
        {
            _listening = true;
            StatusChanged?.Invoke("Preparing handshake");
        }

        void HandleError(string message)
        {
            _listening = false;
            ConnectionFailed?.Invoke(message);
        }

        void HandleClosed(int code, string reason)
        {
            _transport.Error -= HandleError;

            if (code == CloseReasons.RejectCode && !string.IsNullOrEmpty(reason))
                Rejected?.Invoke(reason, CloseReasons.Describe(reason));

            Disconnected?.Invoke(code, reason);
        }

        void Receive(string message)
        {
            if (!_listening) return;

            foreach (var packet in PacketFrames.Parse(message))
            {
                PacketReceived?.Invoke(packet);
                if (_handlers.TryGetValue(packet.Id, out var handler)) handler(packet);
            }
        }

        // connection.ts handleConnected
        void HandleConnected() =>
            Send(Packets.Handshake, new Dictionary<string, object> { ["gVer"] = GameVersion.Gver });

        // connection.ts handleHandshake
        void HandleHandshake(Dictionary<string, object> data)
        {
            if (!(data["type"] is string type) || type != "client")
            {
                StatusChanged?.Invoke("Invalid client");
                return;
            }

            StatusChanged?.Invoke("Connecting to server");

            TimeOffset = JsValue.ToInt32(_clock.Now) - (double)data["serverTime"];
            Instance = (string)data["instance"];
            ServerId = (int)(double)data["serverId"];

            switch (_login.Mode)
            {
                case Opcodes.Login.Guest:
                    Send(Packets.Login, new Dictionary<string, object> { ["opcode"] = Opcodes.Login.Guest });
                    break;

                case Opcodes.Login.Register:
                    Send(Packets.Login, new Dictionary<string, object>
                    {
                        ["opcode"] = Opcodes.Login.Register,
                        ["username"] = _login.Username,
                        ["password"] = _login.Password,
                        ["email"] = _login.Email
                    });
                    break;

                default:
                    Send(Packets.Login, new Dictionary<string, object>
                    {
                        ["opcode"] = Opcodes.Login.Login,
                        ["username"] = _login.Username,
                        ["password"] = _login.Password
                    });
                    break;
            }
        }

        // connection.ts handleNetwork
        void HandleNetwork(Opcodes.Network opcode, Dictionary<string, object> info)
        {
            switch (opcode)
            {
                case Opcodes.Network.Ping:
                    Send(Packets.Network, new List<object> { Opcodes.Network.Pong });
                    break;

                case Opcodes.Network.Sync:
                    // if (!info?.timestamp) return;  — also skips a timestamp of 0
                    if (info == null || !(info.TryGetValue("timestamp", out var value) && value is double timestamp) ||
                        timestamp == 0 || double.IsNaN(timestamp))
                        return;

                    TimeOffset = JsValue.ToInt32(_clock.Now) - timestamp;
                    break;
            }
        }
    }
}
