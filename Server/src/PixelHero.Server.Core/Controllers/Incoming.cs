using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Controllers
{
    /// <summary>
    /// controllers/incoming.ts: the global incoming controller. This is responsible for communication between the hub
    /// and the server (the player-based one is Game.Incoming).
    /// </summary>
    public sealed class Incoming
    {
        readonly World world;

        public Incoming(World world) => this.world = world;

        /// <summary>
        /// Entrypoint for handling incoming packets from the hub. We organize them in an array. First element is the
        /// packet type, the second is the opcode, and lastly we have whatever data the hub decided to send.
        /// </summary>
        public void Handle(object message)
        {
            var parts = JsAny.Destructure(message, 3);
            object packet = parts[0], opcode = parts[1], data = parts[2];

            if (JsAny.Is(packet, Packets.Player)) HandlePlayer(opcode, data);
            else if (JsAny.Is(packet, Packets.Chat)) HandleChat(opcode);
            else if (JsAny.Is(packet, Packets.Guild)) HandleGuild(opcode, data);
            else if (JsAny.Is(packet, Packets.Friends)) HandleFriends(data);
            else if (JsAny.Is(packet, Packets.Relay)) HandleRelay(opcode);
        }

        /// <summary>
        /// Responsible for synchronizing the player's login or logout actions with the controllers in the server. This
        /// updates the friends list/guilds/etc of other players and notifies them that the player has logged in or out.
        /// </summary>
        void HandlePlayer(object opcode, object data)
        {
            var guild = JsAny.Get(data, "guild");
            var username = JsAny.Get(data, "username") as string;
            var serverId = JsAny.Get(data, "serverId") as double?;
            var logout = JsAny.Is(opcode, Opcodes.Player.Logout);

            // Synchronize the player's login or logout to the guild members.
            if (JsAny.Truthy(guild)) world.SyncGuildMembers(guild as string, username, logout, serverId);

            // Synchronize the player's login or logout to the friends list.
            world.SyncFriendsList(username, logout, serverId);
        }

        /// <summary>
        /// Handles the event received from the hub about chat messages. This can be used for private messages or
        /// global messages (from the Discord bot).
        /// </summary>
        void HandleChat(object data)
        {
            var source = JsAny.Get(data, "source") as string;
            var message = JsAny.Get(data, "message") as string;
            var target = JsAny.Get(data, "target");

            // Not found occurs when the hub could not find the player anywhere.
            if (JsAny.Truthy(JsAny.Get(data, "notFound")))
            {
                world.GetPlayerByName(source)?.Notify($"misc:NOT_ONLINE;username={JsAny.Key(target)}");
                return;
            }

            // Success is an event sent from the hub when the message was successfully delivered.
            if (JsAny.Truthy(JsAny.Get(data, "success")))
            {
                world.GetPlayerByName(source)?.Notify(message, "aquamarine", $"[To {Utils.FormatName(target as string)}]", true);
                return;
            }

            // No target means that the message is globally sent.
            if (!JsAny.Truthy(target))
            {
                var colour = JsAny.Get(data, "colour");
                world.GlobalMessage(source, message, JsAny.Truthy(colour) ? colour as string : "tomato", true);
                return;
            }

            // Find who the message is targeted towards and attempt to send them a message.
            world.GetPlayerByName(target as string)?.SendMessage(target as string, message, source);
        }

        /// <summary>
        /// Receives information from the hub regarding a guild. Generally this is used when a player logs in and
        /// requests to verify the online status of their guild's members. We relay the list to the player's client.
        /// </summary>
        void HandleGuild(object opcode, object data)
        {
            if (!JsAny.Is(opcode, Opcodes.Guild.Update)) return;

            var username = JsAny.Get(data, "username");
            var members = JsAny.Get(data, "members");

            if (!JsAny.Truthy(username) || !JsAny.Truthy(members)) return;

            var player = world.GetPlayerByName(username as string);

            // Send the packet to the player's client.
            player?.Send(new GuildPacket(Opcodes.Guild.Update, new JsObject("members", members)));
        }

        /// <summary>
        /// Receives the player's active friends on other servers (the hub checked the ones not on this server) and
        /// updates the player's friends list.
        /// </summary>
        void HandleFriends(object data)
        {
            var player = world.GetPlayerByName(JsAny.Get(data, "username") as string);

            if (JsAny.Get(data, "activeFriends") is JsObject activeFriends) player?.friends.SetActiveFriends(activeFriends);
        }

        /// <summary>
        /// Handles receiving a relay packet: [username, packet]. We send the packet to that player on this server.
        /// </summary>
        void HandleRelay(object data)
        {
            var parts = JsAny.Destructure(data, 2);
            var username = parts[0] as string;
            var info = parts[1];
            var player = world.GetPlayerByName(username);

            // Could hypothetically happen if the player is in the process of logging out.
            if (player == null)
            {
                Log.Debug($"Could not find player {JsAny.Key(username)} to relay packet.");
                return;
            }

            // Relays the packet to the player's packet handler.
            player.Send(new UntypedPacket(JsAny.Get(info, 0), JsAny.Get(info, 1), JsAny.Get(info, 2)));
        }
    }
}
