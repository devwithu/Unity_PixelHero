using PixelHero.Hub.Controllers;
using PixelHero.Hub.Network;
using PixelHero.Server.Common;

namespace PixelHero.Hub.Model
{
    /// <summary>model/index.ts: something connected to the hub (a game server or an admin).</summary>
    public abstract class Model
    {
        public string address;

        public readonly Models controller;
        public readonly string instance;
        public readonly Connection connection;

        protected Model(Models controller, string instance, Connection connection)
        {
            this.controller = controller;
            this.instance = instance;
            this.connection = connection;

            address = Utils.BufferToAddress(connection.socket.RemoteAddressText);
        }

        public abstract void HandlePacket(object packet, object opcode, object info);
    }
}
