using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Js;
using GameEntity = PixelHero.Server.Game.Entity;

namespace PixelHero.Server.Network
{
    // common/network/impl: one class per packet, with the original's constructor shapes.

    public sealed class ConnectedPacket : Packet
    {
        public ConnectedPacket() : base(Packets.Connected) { }
    }

    public sealed class HandshakePacket : Packet
    {
        public HandshakePacket(JsObject data) : base(Packets.Handshake, null, data) { }
    }

    public sealed class WelcomePacket : Packet
    {
        public WelcomePacket(JsObject data) : base(Packets.Welcome, null, data) { }
    }

    /// <summary>Utils.compress(JSON.stringify(data)) with Utils.getBufferSize(data).</summary>
    public sealed class MapPacket : Packet
    {
        public MapPacket(object data)
            : base(Packets.Map, null, Utils.Compress(PixelHero.Js.JsJson.Stringify(data)), Utils.GetBufferSize(data)) { }
    }

    public sealed class SpawnPacket : Packet
    {
        public SpawnPacket(GameEntity entity, Player player = null) : base(Packets.Spawn, null, Serialize(entity, player)) { }

        // Serialize differently for player and mobs.
        static JsObject Serialize(GameEntity entity, Player player) =>
            entity.IsPlayer() ? ((Player)entity).Serialize(true)
            : entity.IsMob() ? ((Mob)entity).Serialize(player)
            : entity.Serialize();
    }

    public sealed class ListPacket : Packet
    {
        public ListPacket(Opcodes.List opcode, JsObject info) : base(Packets.List, opcode, info) { }
    }

    public sealed class EquipmentPacket : Packet
    {
        public EquipmentPacket(Opcodes.Equipment opcode, JsObject data) : base(Packets.Equipment, opcode, data) { }
    }

    public sealed class SyncPacket : Packet
    {
        public SyncPacket(JsObject data) : base(Packets.Sync, null, data) { }
    }

    public sealed class MovementPacket : Packet
    {
        public MovementPacket(Opcodes.Movement opcode, JsObject data = null) : base(Packets.Movement, opcode, data) { }
    }

    public sealed class TeleportPacket : Packet
    {
        public TeleportPacket(JsObject data) : base(Packets.Teleport, null, data) { }
    }

    public sealed class DespawnPacket : Packet
    {
        public DespawnPacket(JsObject info) : base(Packets.Despawn, null, info) { }
    }

    public sealed class CombatPacket : Packet
    {
        public CombatPacket(Opcodes.Combat opcode, JsObject data) : base(Packets.Combat, opcode, data) { }
    }

    public sealed class AnimationPacket : Packet
    {
        public AnimationPacket(JsObject data) : base(Packets.Animation, null, data) { }
    }

    public sealed class PointsPacket : Packet
    {
        public PointsPacket(JsObject data) : base(Packets.Points, null, data) { }
    }

    public sealed class NetworkPacket : Packet
    {
        public NetworkPacket(Opcodes.Network opcode, JsObject data = null) : base(Packets.Network, opcode, data) { }
    }

    public sealed class ChatPacket : Packet
    {
        public ChatPacket(JsObject data) : base(Packets.Chat, null, data) { }
    }

    public sealed class CommandPacket : Packet
    {
        public CommandPacket(JsObject data) : base(Packets.Command, null, data) { }
    }

    public sealed class ContainerPacket : Packet
    {
        public ContainerPacket(Opcodes.Container opcode, JsObject data) : base(Packets.Container, opcode, data) { }
    }

    public sealed class AbilityPacket : Packet
    {
        public AbilityPacket(Opcodes.Ability opcode, JsObject data) : base(Packets.Ability, opcode, data) { }
    }

    public sealed class QuestPacket : Packet
    {
        public QuestPacket(Opcodes.Quest opcode, JsObject data) : base(Packets.Quest, opcode, data) { }
    }

    public sealed class AchievementPacket : Packet
    {
        public AchievementPacket(Opcodes.Achievement opcode, JsObject data) : base(Packets.Achievement, opcode, data) { }
    }

    public sealed class NotificationPacket : Packet
    {
        public NotificationPacket(Opcodes.Notification opcode, JsObject data) : base(Packets.Notification, opcode, data) { }
    }

    public sealed class BlinkPacket : Packet
    {
        public BlinkPacket(string instance) : base(Packets.Blink, null, instance) { }
    }

    public sealed class HealPacket : Packet
    {
        public HealPacket(JsObject data) : base(Packets.Heal, null, data) { }
    }

    public sealed class ExperiencePacket : Packet
    {
        public ExperiencePacket(Opcodes.Experience opcode, JsObject data) : base(Packets.Experience, opcode, data) { }
    }

    public sealed class DeathPacket : Packet
    {
        public DeathPacket(string instance) : base(Packets.Death, null, instance) { }
    }

    public sealed class MusicPacket : Packet
    {
        public MusicPacket(string newSong = null) : base(Packets.Music, null, newSong) { }
    }

    public sealed class NPCPacket : Packet
    {
        public NPCPacket(Opcodes.NPC opcode, object data) : base(Packets.NPC, opcode, data) { }
    }

    public sealed class RespawnPacket : Packet
    {
        public RespawnPacket(GameEntity entity) : base(Packets.Respawn, null, new JsObject("x", entity.x, "y", entity.y)) { }
    }

    public sealed class TradePacket : Packet
    {
        public TradePacket(Opcodes.Trade opcode, JsObject data) : base(Packets.Trade, opcode, data) { }
    }

    public sealed class EnchantPacket : Packet
    {
        public EnchantPacket(Opcodes.Enchant opcode, object data) : base(Packets.Enchant, opcode, data) { }
    }

    public sealed class GuildPacket : Packet
    {
        public GuildPacket(Opcodes.Guild? opcode = null, JsObject data = null)
            : base(Packets.Guild, opcode is { } o ? (int)o : null, data) { }
    }

    public sealed class PointerPacket : Packet
    {
        public PointerPacket(Opcodes.Pointer opcode, object data = null) : base(Packets.Pointer, opcode, data) { }
    }

    public sealed class PVPPacket : Packet
    {
        public PVPPacket(JsObject data) : base(Packets.PVP, null, data) { }
    }

    public sealed class PoisonPacket : Packet
    {
        public PoisonPacket(double type) : base(Packets.Poison, null, type) { }
    }

    public sealed class StorePacket : Packet
    {
        public StorePacket(Opcodes.Store opcode, JsObject data) : base(Packets.Store, opcode, data) { }
    }

    public sealed class OverlayPacket : Packet
    {
        public OverlayPacket(Opcodes.Overlay opcode, JsObject data = null) : base(Packets.Overlay, opcode, data) { }
    }

    public sealed class CameraPacket : Packet
    {
        public CameraPacket(Opcodes.Camera opcode) : base(Packets.Camera, opcode) { }
    }

    public sealed class BubblePacket : Packet
    {
        public BubblePacket(object data, Opcodes.Bubble opcode = Opcodes.Bubble.Entity) : base(Packets.Bubble, opcode, data) { }
    }

    public sealed class SkillPacket : Packet
    {
        public SkillPacket(Opcodes.Skill opcode, JsObject data = null) : base(Packets.Skill, opcode, data) { }
    }

    public sealed class UpdatePacket : Packet
    {
        public UpdatePacket(List<object> data = null) : base(Packets.Update, null, data) { }
    }

    public sealed class MinigamePacket : Packet
    {
        public MinigamePacket(Opcodes.Minigame opcode, JsObject data = null) : base(Packets.Minigame, opcode, data) { }
    }

    public sealed class EffectPacket : Packet
    {
        public EffectPacket(Opcodes.Effect opcode, JsObject data) : base(Packets.Effect, opcode, data) { }
    }

    public sealed class FriendsPacket : Packet
    {
        public FriendsPacket(Opcodes.Friends? opcode = null, JsObject data = null)
            : base(Packets.Friends, opcode is { } o ? (int)o : null, data) { }
    }

    public sealed class RankPacket : Packet
    {
        public RankPacket(Modules.Ranks rank) : base(Packets.Rank, null, (double)rank) { }
    }

    public sealed class CraftingPacket : Packet
    {
        public CraftingPacket(Opcodes.Crafting opcode, JsObject data) : base(Packets.Crafting, opcode, data) { }
    }

    public sealed class LootBagPacket : Packet
    {
        public LootBagPacket(Opcodes.LootBag opcode, JsObject info) : base(Packets.LootBag, opcode, info) { }
    }

    public sealed class CountdownPacket : Packet
    {
        public CountdownPacket(JsObject data) : base(Packets.Countdown, null, data) { }
    }

    public sealed class ResourcePacket : Packet
    {
        public ResourcePacket(JsObject data) : base(Packets.Resource, null, data) { }
    }

    public sealed class InterfacePacket : Packet
    {
        public InterfacePacket(Opcodes.Interface opcode, JsObject data) : base(Packets.Interface, opcode, data) { }
    }

    public sealed class PlayerPacket : Packet
    {
        public PlayerPacket(Opcodes.Player opcode, JsObject data = null) : base(Packets.Player, opcode, data) { }
    }

    /// <summary>[username, [...packet.serialize()]].</summary>
    public sealed class RelayPacket : Packet
    {
        public RelayPacket(string username, Packet packet)
            : base(Packets.Relay, null, new List<object> { username, packet.Serialize() }) { }
    }
}
