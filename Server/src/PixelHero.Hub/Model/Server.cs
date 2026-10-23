using PixelHero.Common;
using PixelHero.Hub.Controllers;
using PixelHero.Hub.Network;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;
using Connection = PixelHero.Hub.Network.Connection;

namespace PixelHero.Hub.Model
{
    /// <summary>model/server.ts: a game server connected to the hub, with the usernames of its players.</summary>
    public sealed class Server : Model
    {
        public double id = -1;
        public string name = "";
        public string host = "";
        public double port = -1;

        public List<string> players = new();

        public double maxPlayers = 2;

        public Server(Models controller, string instance, Connection connection) : base(controller, instance, connection) { }

        /// <summary>Loads the information from the handshake packet into the server object.</summary>
        public void Load(object data)
        {
            var type = JsAny.Get(data, "type");

            if (type is "hub")
            {
                name = JsAny.Get(data, "name") as string;
                id = JsAny.ToNumber(JsAny.Get(data, "serverId"));
                host = JsAny.Get(data, "remoteHost") as string;
                port = JsAny.ToNumber(JsAny.Get(data, "port"));
                players = (JsAny.Get(data, "players") as List<object>)?.ConvertAll(player => player as string);
                maxPlayers = JsAny.ToNumber(JsAny.Get(data, "maxPlayers"));
            }
            else Log.Error($"Invalid handshake type: {JsAny.Key(type)}");
        }

        public override void HandlePacket(object packet, object opcode, object info)
        {
            if (JsAny.Is(packet, Packets.Chat)) HandleChat(opcode);
            else if (JsAny.Is(packet, Packets.Guild)) HandleGuild(opcode, info);
            else if (JsAny.Is(packet, Packets.Friends)) HandleFriends(info);
            else if (JsAny.Is(packet, Packets.Player)) HandlePlayer(opcode, info);
            else if (JsAny.Is(packet, Packets.Relay)) Relay(opcode);
        }

        /// <summary>Receives information about the player's login our logout activity.</summary>
        void HandlePlayer(object opcode, object info)
        {
            var username = JsAny.Get(info, "username") as string;
            var guild = JsAny.Get(info, "guild") as string;

            if (JsAny.Is(opcode, Opcodes.Player.Login)) Add(username, guild ?? "");
            else if (JsAny.Is(opcode, Opcodes.Player.Logout)) Remove(username, guild ?? "");
        }

        /// <summary>
        /// Sends a chat callback message to the server. Depending on the information received, we determine whether
        /// it's a private message or a normal message.
        /// </summary>
        void HandleChat(object info) =>
            Message(JsAny.Get(info, "source") as string, JsAny.Get(info, "message") as string, JsAny.Get(info, "target") as string);

        /// <summary>
        /// Handles incoming messages regarding a guild. Used to broadcast to other servers actions that are performed
        /// on a guild. Things like a player joining, a player's rank being changed, or someone leaving.
        /// </summary>
        public void HandleGuild(object opcode, object info)
        {
            if (!JsAny.Is(opcode, Opcodes.Guild.Update)) return;

            var username = JsAny.Get(info, "username");
            var inactiveMembers = JsAny.Get(info, "usernames");
            var activeMembers = new List<object>();

            foreach (var member in Iterate(inactiveMembers))
            {
                var targetServer = controller.FindPlayer(member as string);

                if (targetServer != null) activeMembers.Add(new JsObject("username", member, "serverId", targetServer.id));
            }

            // Send the list of active members back to the source server's player.
            Send(new GuildPacket(Opcodes.Guild.Update, new JsObject("username", username, "members", activeMembers)));
        }

        /// <summary>
        /// Contains a list of inactive friends list for a given username. We check if any server has a player that is
        /// on the inactive list.
        /// </summary>
        void HandleFriends(object info)
        {
            var username = JsAny.Get(info, "username");
            var inactiveFriends = JsAny.Get(info, "inactiveFriends");
            var activeFriends = new JsObject();

            // Look through all the inactive friends and try to find them on a server.
            foreach (var friend in Iterate(inactiveFriends))
            {
                var targetServer = controller.FindPlayer(friend as string);

                // If the player is online, add them to the active friends list.
                if (targetServer != null)
                    activeFriends.Set(JsAny.Key(friend), new JsObject("online", true, "serverId", targetServer.id));
            }

            // Send the active friends back to the server.
            Send(new FriendsPacket(Opcodes.Friends.Sync, new JsObject("username", username, "activeFriends", activeFriends)));
        }

        /// <summary>for (let x of value): arrays and strings; anything else throws.</summary>
        static IEnumerable<object> Iterate(object value) => value switch
        {
            List<object> list => list.ToList(),
            string text => text.EnumerateRunes().Select(rune => (object)rune.ToString()).ToList(),
            _ => throw new JsTypeError($"{(value == null ? "undefined" : JsAny.Key(value))} is not iterable")
        };

        /// <summary>Handles sending a message to the server's websocket connection.</summary>
        public void Send(Packet packet) => connection.Send(JsJson.Stringify(packet.Serialize()));

        /// <summary>A broadcast message is sent to all the servers currently connected to the hub.</summary>
        public void Broadcast(Packet packet) => controller.BroadcastServers(packet, instance);

        /// <summary>
        /// Relay is a packet that is sent across servers to another player. This is generally used by things like
        /// guilds when we want to synchronize the joining and leaving of a player across all servers.
        /// </summary>
        /// <param name="info">The username of the player as first element, and the packet data as the second.</param>
        public void Relay(object info)
        {
            var server = controller.FindPlayer(JsAny.Get(info, 0) as string);

            if (server == null) return;

            // Create a new packet with the relay data and send it to the target server.
            server.Send(new Packet(Packets.Relay, null, info));
        }

        /// <summary>Adds a player to the server's player list.</summary>
        public void Add(string username, string guild = "")
        {
            // Ensure the player is not already in the list.
            if (players.Contains(username)) return;

            // Add the player to the list.
            players.Add(username);

            // Broadcast the login to all the servers.
            controller.BroadcastServers(new Packet(Packets.Player, Opcodes.Player.Login,
                new JsObject("username", username, "serverId", id, "guild", guild)));

            // Create a callback for the player logging in.
            controller.HandlePlayer(username, id);
        }

        /// <summary>Handles removing a player from the server's player list.</summary>
        public void Remove(string username, string guild = "")
        {
            // Filter out the player from the list.
            players = players.Where(player => player != username).ToList();

            // Broadcast the logout to all the servers.
            controller.BroadcastServers(new Packet(Packets.Player, Opcodes.Player.Logout,
                new JsObject("username", username, "serverId", id, "guild", guild)));

            // Create a callback for the player logging out.
            controller.HandlePlayer(username, id, true);
        }

        /// <summary>
        /// Handles sending a chat message between servers. This can refer to a private message or redirecting an
        /// in-game chat to the Discord bot for logging.
        /// </summary>
        public void Message(string source, string message, string target = null)
        {
            // If we don't have a target then we just send the message to the Discord bot.
            if (string.IsNullOrEmpty(target))
            {
                controller.messageCallback?.Invoke(source, message, $"{name} {JsString.Of(id)}", true);
                return;
            }

            // Attempt to find the target player on the server.
            var targetServer = controller.FindPlayer(target);

            // No player could be found, so we tell the source server that the player is not online.
            if (targetServer == null)
            {
                Send(new ChatPacket(new JsObject("source", source, "target", target, "notFound", true)));
                return;
            }

            // Send the private message to the target player's server.
            targetServer.Send(new ChatPacket(new JsObject("source", source, "message", message, "target", target)));

            // Send a confirmation to the source server that the message was sent.
            Send(new ChatPacket(new JsObject("source", source, "message", message, "target", target, "success", true)));
        }

        /// <summary>Condenses the crucial server information into a JSON object.</summary>
        public JsObject Serialize() => new(
            "id", id,
            "name", name,
            "host", host,
            "port", port,
            "players", (double)players.Count,
            "maxPlayers", maxPlayers);
    }
}
