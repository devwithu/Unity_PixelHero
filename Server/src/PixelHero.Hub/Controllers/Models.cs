using PixelHero.Common;
using PixelHero.Hub.Model;
using PixelHero.Hub.Network;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;
using Connection = PixelHero.Hub.Network.Connection;
using ServerModel = PixelHero.Hub.Model.Server;

namespace PixelHero.Hub.Controllers
{
    /// <summary>controllers/models.ts: the servers and admins connected to the hub.</summary>
    public sealed class Models
    {
        /// <param name="logout">Whether or not the player logged out.</param>
        /// <param name="population">The current population of all the servers.</param>
        public delegate void PlayerCallback(string username, double serverId, bool logout, double population);

        public delegate void ServerCallback(double id, string name);

        public delegate void MessageCallback(string source, string message, string serverName, bool withArrow);

        readonly JsObject<Model.Model> models = new();

        public PlayerCallback playerCallback;
        public MessageCallback messageCallback;

        ServerCallback addCallback;
        ServerCallback removeCallback;

        /// <summary>
        /// Handles the creation of a new server object upon initial connection. This creates the object and adds it to
        /// our list of servers after the handshake has completed.
        /// </summary>
        public void Connect(string instance, Connection connection) =>
            connection.OnMessage(message =>
            {
                var parts = JsAny.Destructure(message, 3);
                object packet = parts[0], opcode = parts[1], info = parts[2];

                if (!Utils.ValidPacket(JsAny.ToNumber(packet)))
                {
                    Log.Error($"Non-existent packet received: {JsAny.Key(packet)} data: ");
                    Log.Error(info);

                    return;
                }

                if (JsAny.Is(packet, Packets.Handshake)) HandleHandshake(opcode, instance, connection);
                else
                {
                    var model = models[instance] ??
                        throw new JsTypeError("Cannot read properties of undefined (reading 'handlePacket')");

                    model.HandlePacket(packet, opcode, info);
                }

                SyncAdmins();
            });

        void HandleHandshake(object data, string instance, Connection connection)
        {
            // Port fix (ANALYSIS.md §9 #13; the harness patches the original the same way): a server or an admin must
            // send the hub's HUB_ACCESS_TOKEN.
            var type = JsAny.Get(data, "type");
            if ((type is "hub" || type is "admin") && !(JsAny.Get(data, "accessToken") is string token && token == Config.Current.hubAccessToken))
            {
                Log.Error($"Invalid access token from {instance}.");

                connection.Close();
                return;
            }

            switch (type)
            {
                case "hub":
                {
                    var gVer = JsAny.Get(data, "gVer");

                    if (!(gVer is string version && version == Config.Current.gver))
                    {
                        Log.Error($"Game version mismatch: {Config.Current.gver} {JsAny.Key(gVer)} from {JsAny.Key(JsAny.Get(data, "serverId"))}");

                        connection.Close();
                        return;
                    }

                    var server = new ServerModel(this, instance, connection);

                    server.Load(data);
                    AddServer(server);

                    break;
                }

                case "admin":
                {
                    var admin = new Admin(this, instance, connection);

                    AddAdmin(admin);

                    break;
                }

                case "client":
                {
                    Log.Error($"Client {instance} is not a valid server.");

                    break;
                }
            }
        }

        /// <summary>Get a server object from our list of servers.</summary>
        ServerModel GetServer(string instance) => models[instance] as ServerModel;

        /// <summary>Get a list of all servers.</summary>
        List<ServerModel> GetAllServers() => models.Values.OfType<ServerModel>().ToList();

        /// <summary>Get an admin object from our list of admins.</summary>
        Admin GetAdmin(string instance) => models[instance] as Admin;

        /// <summary>Get a list of all admins.</summary>
        List<Admin> GetAllAdmins() => models.Values.OfType<Admin>().ToList();

        /// <summary>Handles adding a server to our list of servers.</summary>
        void AddServer(ServerModel server)
        {
            if (models.Has(server.instance))
            {
                Log.Error($"Server {server.instance} already exists.");
                return;
            }

            models.Set(server.instance, server);
            addCallback?.Invoke(server.id, server.name);
        }

        /// <summary>Handles adding an admin to our list of admins.</summary>
        void AddAdmin(Admin admin)
        {
            if (models.Has(admin.instance))
            {
                Log.Error($"Admin {admin.instance} already exists.");
                return;
            }

            models.Set(admin.instance, admin);
        }

        void SyncAdmins() =>
            BroadcastAdmins(new Packet(Packets.AdminSync, null, new JsObject("servers", SerializeServers().ConvertAll(s => (object)s))));

        /// <summary>Handles removing a server from our list of servers.</summary>
        public void Remove(string instance)
        {
            if (instance == null || !models.Has(instance)) return;

            var model = models[instance];

            // Prevent crashes from removing non-existent servers.
            if (model is ServerModel server) removeCallback?.Invoke(server.id, server.name);

            models.Delete(instance);

            SyncAdmins();
        }

        /// <summary>Broadcasts a message to all servers. Optionally we can exclude a server.</summary>
        public void BroadcastServers(Packet packet, string exclude = "")
        {
            foreach (var instance in models.Keys)
            {
                if (instance == exclude) continue;

                GetServer(instance)?.Send(packet);
            }
        }

        /// <summary>Broadcasts a message to all admins. Optionally we can exclude an admin.</summary>
        public void BroadcastAdmins(Packet packet, string exclude = "")
        {
            foreach (var instance in models.Keys)
            {
                if (instance == exclude) continue;

                GetAdmin(instance)?.Send(packet);
            }
        }

        /// <summary>Relays a global chat message to all the servers connected to the hub.</summary>
        /// <param name="source">Who is sending the message (generally someone on Discord).</param>
        /// <param name="message">The string contents of the message.</param>
        /// <param name="colour">Colour of the text we are sending.</param>
        public void Global(string source, string message, string colour) =>
            BroadcastServers(new ChatPacket(new JsObject("source", source, "message", message, "colour", colour)));

        /// <summary>
        /// Handles a player logging in or out of the game. We use this to update the Discord server with the amount
        /// of players online and with the logout/login activity.
        /// </summary>
        public void HandlePlayer(string username, double serverId, bool logout = false)
        {
            var total = GetTotalPlayers();

            playerCallback?.Invoke(username, serverId, logout, total);
        }

        /// <summary>Checks that there is at least one server with space for a new player.</summary>
        public bool HasSpace() => models.Values.Any(model => model is ServerModel server && server.players.Count < server.maxPlayers);

        /// <summary>The first server that has space for the new player, or null.</summary>
        public ServerModel FindEmptyServer()
        {
            foreach (var server in GetAllServers())
            {
                if (server.players.Count >= server.maxPlayers - 1) continue;

                return server;
            }

            return null;
        }

        /// <summary>The server that contains the `username` specified, or null.</summary>
        public ServerModel FindPlayer(string username)
        {
            foreach (var server in GetAllServers())
                if (server.players.Contains(username))
                    return server;

            return null;
        }

        /// <summary>Number of total players spanning all servers.</summary>
        public double GetTotalPlayers()
        {
            double total = 0;

            // Iterate through all the servers and add the amount of players.
            ForEachServer(server => total += server.players.Count);

            return total;
        }

        /// <summary>Iterates through all the servers and serializes them into an array (sorted by id).</summary>
        public List<JsObject> SerializeServers() =>
            GetAllServers().ConvertAll(server => server.Serialize())
                .OrderBy(server => server, Comparer<JsObject>.Create((a, b) => JsSortOrder((double)a["id"] - (double)b["id"])))
                .ToList();

        /// <summary>Array.prototype.sort's use of the comparator's sign (NaN counts as equal; the sort is stable).</summary>
        static int JsSortOrder(double difference) => difference > 0 ? 1 : difference < 0 ? -1 : 0;

        /// <summary>Iterates through all the server objects.</summary>
        public void ForEachServer(Action<ServerModel> callback)
        {
            foreach (var server in GetAllServers()) callback(server);
        }

        /// <summary>Callback handler for when a new server is added.</summary>
        public void OnAdd(ServerCallback callback) => addCallback = callback;

        /// <summary>Callback handler for when a server is removed from our list.</summary>
        public void OnRemove(ServerCallback callback) => removeCallback = callback;

        /// <summary>Callback for when a player logs in or out of the server (Discord population updates).</summary>
        public void OnPlayer(PlayerCallback callback) => playerCallback = callback;

        /// <summary>Message for when anyone on any server sends a message. We send these messages to the Discord bot.</summary>
        public void OnMessage(MessageCallback callback) => messageCallback = callback;
    }
}
