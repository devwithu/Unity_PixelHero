using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Game;
using PixelHero.Server.Js;

namespace PixelHero.Server.Network
{
    /// <summary>network/network.ts: the per-player packet queues, flushed by the world tick.</summary>
    public sealed class GameNetwork
    {
        readonly IGameDatabase database;
        readonly SocketHandler socketHandler;
        readonly Regions regions;

        readonly double timeoutThreshold = 5000;

        /// <summary>Serialized packets (arrays holding live data objects) per player instance; JSON at flush.</summary>
        readonly JsObject<List<object>> packets = new();

        readonly World world;

        public GameNetwork(World world)
        {
            this.world = world;
            database = world.database;
            socketHandler = world.socketHandler;
            regions = world.map.regions;
        }

        public void Parse()
        {
            foreach (var pair in packets)
                if (pair.Value.Count > 0)
                {
                    var connection = socketHandler.Get(pair.Key);

                    if (connection != null)
                    {
                        connection.Send(pair.Value);

                        packets.Set(pair.Key, new List<object>());
                    }
                    else socketHandler.Remove(pair.Key);
                }
        }

        public void HandleConnection(Connection connection) =>
            database.IsIpBanned(connection.address, banned =>
            {
                // Ensure that the connection is not banned.
                if (banned)
                {
                    connection.Reject("banned");
                    return;
                }

                // Create the packet queue for the connection instance.
                packets.Set(connection.instance, new List<object>());

                // Check the time difference between the last connection.
                var timeDifference = Node.DateNow() - GetLastConnection(connection);

                // Update the last time the connection was made.
                socketHandler.UpdateLastTime(connection.address);

                // Skip if we are in debug mode.
                if (!Config.Current.debugging)
                {
                    // Check that the connections aren't coming too fast.
                    if (timeDifference < timeoutThreshold)
                    {
                        connection.Reject("toofast");
                        return;
                    }

                    // Ensure that we don't have too many connections from the same IP address.
                    if (socketHandler.IsMaxConnections(connection.address))
                    {
                        connection.Reject("toomany");
                        return;
                    }
                }

                // Create the player instance finally.
                _ = new Player(world, database, connection);
            });

        public void DeletePacketQueue(Player player) => packets.Delete(player.instance);

        public void Broadcast(Packet packet)
        {
            var serializedPacket = packet.Serialize();

            foreach (var queue in packets.Values) queue.Add(serializedPacket);
        }

        public void Send(Player player, Packet packet)
        {
            if (player == null || !packets.Has(player.instance)) return;

            packets[player.instance].Add(packet.Serialize());
        }

        public void SendToPlayers(List<Player> players, Packet packet)
        {
            foreach (var player in players) Send(player, packet);
        }

        public void SendToRegion(int regionId, Packet packet, string ignore = null)
        {
            var region = regions.Get(regionId);

            if (region == null) return;

            region.ForEachPlayer(player =>
            {
                if (player.instance != ignore) Send(player, packet);
            });
        }

        public void SendToSurroundingRegions(int regionId, Packet packet, string ignore = null)
        {
            if (regionId < 0) return;

            regions.ForEachSurroundingRegion(regionId, surroundingRegion => SendToRegion(surroundingRegion, packet, ignore));
        }

        public void SendToRegionList(List<int> list, Packet packet, string ignore = null)
        {
            foreach (var region in list) SendToRegion(region, packet, ignore);
        }

        /// <summary>addresses[address]?.lastTime (undefined makes the difference NaN, which is not below the threshold).</summary>
        double GetLastConnection(Connection connection) =>
            socketHandler.addresses[connection.address]?.lastTime ?? double.NaN;
    }
}
