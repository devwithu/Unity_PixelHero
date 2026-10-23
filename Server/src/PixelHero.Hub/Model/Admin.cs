using PixelHero.Hub.Controllers;
using PixelHero.Hub.Network;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;
using Connection = PixelHero.Hub.Network.Connection;

namespace PixelHero.Hub.Model
{
    /// <summary>model/admin.ts.</summary>
    public sealed class Admin : Model
    {
        public Admin(Models controller, string instance, Connection connection) : base(controller, instance, connection) { }

        /// <summary>Handles sending a message to the admin's websocket connection.</summary>
        public void Send(Packet packet) => connection.Send(JsJson.Stringify(packet.Serialize()));

        /// <summary>A broadcast message is sent to all the admins currently connected to the hub.</summary>
        public void Broadcast(Packet packet) => controller.BroadcastAdmins(packet, instance);

        public override void HandlePacket(object packet, object opcode, object info) =>
            Log.Info($"Received packet {JsAny.Key(packet)} from admin {address}");
    }
}
