using System.Collections.Generic;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Network
{
    /// <summary>
    /// One server → client packet: the id and the arguments its handler receives, i.e. the serialized
    /// <c>[id, opcode?, data, bufferSize?]</c> minus the id (common/network/packet.ts serialize). Arguments are
    /// <see cref="JsJson"/> values: null, bool, double, string, List&lt;object&gt; or Dictionary&lt;string, object&gt;.
    /// </summary>
    public sealed class Packet
    {
        public readonly Packets Id;
        public readonly IReadOnlyList<object> Args;

        public Packet(Packets id, IReadOnlyList<object> args)
        {
            Id = id;
            Args = args;
        }

        public object Arg(int index) => index < Args.Count ? Args[index] : null;

        public override string ToString() => $"{Id} {JsJson.Stringify(Args)}";
    }

    /// <summary>The frame rules of the original client (client/src/network/socket.ts receive and send).</summary>
    public static class PacketFrames
    {
        /// <summary>
        /// Splits a server frame into packets. Frames are always an array of packets (server network.ts parse).
        /// Like socket.ts, text that does not start with '[' is ignored.
        /// </summary>
        public static List<Packet> Parse(string frame)
        {
            var packets = new List<Packet>();
            if (!frame.StartsWith("[")) return packets;

            // socket.ts: data.length > 1 ? handleBulkData(data) : handleData(data.shift()). Both read each element
            // as [id, ...args]. (An empty frame would make the original throw; the server never sends one.)
            foreach (var element in (List<object>)JsJson.Parse(frame))
            {
                var packet = (List<object>)element;
                var args = packet.GetRange(1, packet.Count - 1);
                packets.Add(new Packet((Packets)(int)(double)packet[0], args));
            }

            return packets;
        }

        /// <summary>socket.ts send: <c>JSON.stringify([packet, data])</c>; no data is written as null.</summary>
        public static string Serialize(Packets id, object data = null) =>
            JsJson.Stringify(new List<object> { id, data ?? Undefined.Value });
    }
}
