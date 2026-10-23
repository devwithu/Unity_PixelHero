using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>player/equipments.ts.</summary>
    public sealed class Equipments
    {
        readonly Helmet helmet = new();
        readonly Pendant pendant = new();
        readonly Arrows arrows = new();
        readonly Chestplate chestplate = new();
        readonly Weapon weapon = new();
        readonly Shield shield = new();
        readonly Ring ring = new();
        readonly ArmourSkin armourSkin = new();
        readonly WeaponSkin weaponSkin = new();
        readonly Legplates legplates = new();
        readonly Cape cape = new();
        readonly Boots boots = new();

        // Store all equipments for parsing.
        // Make sure these are in the order of the enum.
        readonly List<Equipment> equipments;

        public JsObject totalAttackStats = Utils.GetEmptyStats();
        public JsObject totalDefenseStats = Utils.GetEmptyStats();
        public JsObject totalBonuses = Utils.GetEmptyBonuses();

        Action loadCallback;
        Action<Equipment> equipCallback;
        Action<Modules.Equipment, double?> unequipCallback;
        Action<Modules.AttackStyle> attackStyleCallback;

        readonly Player player;

        public Equipments(Player player)
        {
            this.player = player;

            equipments = new List<Equipment>
            {
                helmet, pendant, arrows, chestplate, weapon, shield, ring, armourSkin, weaponSkin, legplates, cape, boots
            };
        }

        public void Load(List<object> equipmentInfo)
        {
            foreach (var entry in equipmentInfo)
            {
                var info = entry as JsObject;
                var equipment = Get(info?["type"]);

                if (equipment == null) continue;

                if (!JsData.Truthy(info["key"])) continue; // Skip if the item is already null

                equipment.Update(new Item(info.Str("key"), -1, -1, true, info.Num("count") ?? double.NaN, info.Obj("enchantments")));

                // Only weapons have attack styles, so we update the last selected one.
                if (JsData.Truthy(info["attackStyle"])) UpdateAttackStyle(info["attackStyle"]);
            }

            loadCallback?.Invoke();

            player.Sync();

            CalculateStats();
        }

        public void Equip(Item item, double fromIndex)
        {
            if (item == null)
            {
                Log.Warning($"[{player.username}] Attempted to equip something mysterious.");
                return;
            }

            var type = item.GetEquipmentType();
            var equipment = type == null ? null : Get((double)type.Value);

            if (equipment == null) return;

            if (item.IsTwoHanded() && !GetShield().IsEmpty())
            {
                if (!player.inventory.HasSpace())
                {
                    player.Notify("misc:NO_SPACE");
                    return;
                }

                Unequip((double)Modules.Equipment.Shield);
            }

            if (type == Modules.Equipment.Shield && GetWeapon().IsTwoHanded())
            {
                if (!player.inventory.HasSpace())
                {
                    player.Notify("misc:NO_SPACE");
                    return;
                }

                Unequip((double)Modules.Equipment.Weapon);
            }

            // Remove the item from the inventory.
            player.inventory.Remove(fromIndex, item.count);

            // If there is already an item equipped, we unequip it.
            if (!equipment.IsEmpty())
                player.inventory.Add(new Item(equipment.key, -1, -1, false, equipment.count, equipment.enchantments));

            // Include the last attack style when we're equipping a weapon.
            if (equipment is Weapon weaponEquipment)
                weaponEquipment.Update(item, player.GetLastAttackStyle(item.weaponType));
            else equipment.Update(item);

            equipCallback?.Invoke(equipment);

            CalculateStats();
        }

        /// <summary>unequip(type): `this.equipments[type]` for whatever the client sent.</summary>
        public void Unequip(object type)
        {
            var equipment = Get(type) ?? throw new JsTypeError("Cannot read properties of undefined (reading 'key')");

            if (!JsData.Truthy(equipment.key)) return;

            var item = new Item(equipment.key, -1, -1, false, equipment.count, equipment.enchantments);
            var count = player.inventory.Add(item);

            // We stop here if the item cannot be added to the inventory.
            if (count < 1) return;

            equipment.count -= count;

            if (equipment.count < 1) equipment.Empty();
            else
            {
                item.count = equipment.count;
                equipment.Update(item);
            }

            unequipCallback?.Invoke(equipment.type, equipment.count);

            CalculateStats();
        }

        public void DecrementArrows()
        {
            var arrows = Get((double)Modules.Equipment.Arrows);

            // Remove 1 arrow from the stack.
            arrows.count -= 1;

            // If there are no more arrows, we empty the equipment.
            if (arrows.count < 1) arrows.Empty();

            unequipCallback?.Invoke(Modules.Equipment.Arrows, arrows.count);
        }

        public void UpdateAttackStyle(object style)
        {
            var weapon = GetWeapon();

            // Ensure the weapon has the attack style.
            if (!weapon.HasAttackStyle(style))
            {
                Log.Warning($"[{player.username}] Invalid attack style.");
                return;
            }

            var attackStyle = (Modules.AttackStyle)(int)(double)style;

            // Set new attack style.
            GetWeapon().UpdateAttackStyle(attackStyle);

            // Sync the player for everyone else and update data.
            player.Sync();

            // Callback with the new attack style.
            attackStyleCallback?.Invoke(attackStyle);
        }

        void CalculateStats()
        {
            // Clear the current stats and bonuses.
            totalAttackStats = Utils.GetEmptyStats();
            totalDefenseStats = Utils.GetEmptyStats();
            totalBonuses = Utils.GetEmptyBonuses();

            ForEachEquipment(equipment =>
            {
                if (equipment.IsEmpty()) return;

                // Attack stats
                AddTo(totalAttackStats, equipment.attackStats, "crush", "slash", "stab", "magic");

                // Defense stats
                AddTo(totalDefenseStats, equipment.defenseStats, "crush", "slash", "stab", "magic");

                // Bonuses
                AddTo(totalBonuses, equipment.bonuses, "accuracy", "strength", "archery", "magic");
            });
        }

        /// <summary>total.key += values.key (undefined makes NaN).</summary>
        static void AddTo(JsObject total, JsObject values, params string[] keys)
        {
            foreach (var key in keys)
                total.Set(key, (total.Num(key) ?? double.NaN) + (values.Num(key) ?? double.NaN));
        }

        /// <summary>this.equipments[type]: undefined for anything but an index of the array.</summary>
        public Equipment Get(object type) =>
            type is double d && d >= 0 && d < equipments.Count && d % 1 == 0 ? equipments[(int)d] : null;

        public Equipment Get(Modules.Equipment type) => Get((double)type);

        public Helmet GetHelmet() => (Helmet)Get(Modules.Equipment.Helmet);

        public Chestplate GetChestplate() => (Chestplate)Get(Modules.Equipment.Chestplate);

        public Legplates GetLegplates() => (Legplates)Get(Modules.Equipment.Legplates);

        public ArmourSkin GetSkin() => (ArmourSkin)Get(Modules.Equipment.ArmourSkin);

        public Equipment GetBoots() => Get(Modules.Equipment.Boots);

        public Equipment GetPendant() => Get(Modules.Equipment.Pendant);

        public Equipment GetRing() => Get(Modules.Equipment.Ring);

        public Weapon GetWeapon() => (Weapon)Get(Modules.Equipment.Weapon);

        public Arrows GetArrows() => (Arrows)Get(Modules.Equipment.Arrows);

        public Shield GetShield() => (Shield)Get(Modules.Equipment.Shield);

        public JsObject Serialize(bool clientInfo = false)
        {
            var list = new List<object>();

            ForEachEquipment(equipment => list.Add(equipment.Serialize(clientInfo)));

            // Store in an object so that it gets saved into Database faster.
            return new JsObject("equipments", list);
        }

        public void ForEachEquipment(Action<Equipment> callback)
        {
            foreach (var equipment in equipments) callback(equipment);
        }

        public void OnLoaded(Action callback) => loadCallback = callback;

        public void OnEquip(Action<Equipment> callback) => equipCallback = callback;

        public void OnUnequip(Action<Modules.Equipment, double?> callback) => unequipCallback = callback;

        public void OnAttackStyle(Action<Modules.AttackStyle> callback) => attackStyleCallback = callback;
    }
}
