using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Server.Network
{
    /// <summary>common/network/packet.ts. Data left out is undefined (written as null in the array).</summary>
    public class Packet
    {
        public readonly Packets Id;
        public readonly int? Opcode;
        public readonly object Data;
        readonly double? _bufferSize;

        public Packet(Packets id, int? opcode = null, object data = null, double? bufferSize = null)
        {
            Id = id;
            Opcode = opcode;
            Data = data ?? Undefined.Value;
            _bufferSize = bufferSize;
        }

        public Packet(Packets id, Enum opcode, object data = null) : this(id, opcode == null ? null : Convert.ToInt32(opcode), data) { }

        public virtual List<object> Serialize()
        {
            var packet = Opcode == null
                ? new List<object> { (double)Id, Data }
                : new List<object> { (double)Id, (double)Opcode.Value, Data };

            // Append the buffer size for compression only if it exists.
            if (_bufferSize is double size && size != 0 && !double.IsNaN(size)) packet.Add(size);

            return packet;
        }
    }

    /// <summary>
    /// new Packet(id, opcode, data) with values received from another process (the hub's relay): each is kept as
    /// received, so a relayed packet without an opcode becomes [id, data, undefined] like the original's.
    /// </summary>
    public sealed class UntypedPacket : Packet
    {
        readonly object _id, _opcode, _data;

        public UntypedPacket(object id, object opcode = null, object data = null) : base(Packets.Handshake)
        {
            _id = id;
            _opcode = opcode;
            _data = data;
        }

        public override List<object> Serialize() =>
            _opcode == null ? new List<object> { _id, _data ?? Undefined.Value } : new List<object> { _id, _opcode, _data ?? Undefined.Value };
    }
}
