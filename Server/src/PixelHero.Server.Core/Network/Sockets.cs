using PixelHero.Common;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Network
{
    /// <summary>network/sockethandler.ts.</summary>
    public sealed class SocketHandler
    {
        public sealed class AddressInfo
        {
            public double count;
            public double lastTime;
        }

        public readonly JsObject<AddressInfo> addresses = new(); // Keeps track of addresses, their counts, and their last connection time.
        public readonly JsObject<Connection> connections = new(); // List of all connections to the server.

        Action<Connection> connectionCallback;

        /// <summary>The uWS layer (network/sockets/uws.ts) that turns socket events into connections.</summary>
        public readonly Sockets sockets;

        public SocketHandler()
        {
            sockets = new Sockets(this);
            sockets.OnAdd(Add);
        }

        void Add(Connection connection)
        {
            connections.Set(connection.instance, connection);

            AddAddress(connection.address);

            connectionCallback?.Invoke(connection);
        }

        void AddAddress(string address)
        {
            // Create the first entry if it does not exist.
            if (!addresses.Has(address)) addresses.Set(address, new AddressInfo { count = 0, lastTime = 0 });

            addresses[address].count++;
        }

        public void Remove(string instance)
        {
            // In the event the connection disconnects before it is added to the dictionaries.
            if (!connections.Has(instance)) return;

            RemoveAddress(connections[instance].address);

            connections.Delete(instance);
        }

        void RemoveAddress(string address)
        {
            addresses[address].count--;

            // Delete if there are no more connections of this address.
            if (addresses[address].count == 0) addresses.Delete(address);
        }

        public Connection Get(string instance) => instance == null ? null : connections[instance];

        public bool IsMaxConnections(string address) => addresses[address].count > Constants.MAX_CONNECTIONS;

        public bool HasConnections() => connections.Count > 0;

        public void UpdateLastTime(string address) => addresses[address].lastTime = Node.DateNow();

        public void OnConnection(Action<Connection> callback) => connectionCallback = callback;
    }

    /// <summary>
    /// network/sockets/uws.ts without uWS: the host (SuperSocket, or the test harness) reports opens, messages and
    /// closes here, each as one macrotask on the world's event loop.
    /// </summary>
    public sealed class Sockets
    {
        readonly SocketHandler socketHandler;
        Action<Connection> addCallback;

        public Sockets(SocketHandler socketHandler) => this.socketHandler = socketHandler;

        public void OnAdd(Action<Connection> callback) => addCallback = callback;

        /// <summary>open: a new connection with a fresh player instance. Returns the instance (uWS user data).</summary>
        public string HandleConnection(ISocket socket)
        {
            var instance = Utils.CreateInstance(Modules.EntityType.Player);
            var connection = new Connection(instance, socket);

            socket.Instance = instance;

            addCallback?.Invoke(connection);

            return instance;
        }

        /// <summary>message: rate limit, duplicate filter, JSON.parse, then the connection's message callback.</summary>
        public void HandleMessage(string instance, string text)
        {
            var connection = socketHandler.Get(instance);

            // Prevent the server from crashing if the connection is not found.
            if (connection == null)
            {
                Log.Error($"No connection found for {instance}");
                return;
            }

            // Increment the rate for the connection.
            connection.messageRate++;

            // Reject the connection once we reach the rate limit threshold.
            if (connection.messageRate > Config.Current.messageLimit)
            {
                connection.Reject("ratelimit");
                return;
            }

            try
            {
                // Prevent duplicates in a short period of time.
                if (connection.IsDuplicate(text)) return;

                // Pass the parsed JSON onto the message handler.
                connection.messageCallback?.Invoke(JsData.Parse(text));
            }
            catch (Exception error)
            {
                Log.Error($"Message could not be parsed: {text}.");
                Log.Error(error is JsJson.SyntaxError ? new SyntaxError(error.Message) : error);
            }
        }

        /// <summary>close: mark the connection closed, forget it, then run its close handling.</summary>
        public void HandleClose(string instance)
        {
            var connection = socketHandler.Get(instance);

            if (connection == null)
            {
                Log.Error($"No connection found closing {instance}");
                return;
            }

            // Mark the connection as closed to prevent any further messages from being sent.
            connection.closed = true;

            socketHandler.Remove(connection.instance);

            connection.HandleClose();
        }
    }

    /// <summary>A JSON.parse SyntaxError, logged with its JavaScript name.</summary>
    public sealed class SyntaxError : Exception
    {
        public SyntaxError(string message) : base(message) { }
    }
}
