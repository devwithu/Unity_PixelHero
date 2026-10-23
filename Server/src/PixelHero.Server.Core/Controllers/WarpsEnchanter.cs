using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Info;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Controllers
{
    /// <summary>controllers/warps.ts.</summary>
    public sealed class Warps
    {
        readonly List<object> warps;
        public double lastWarp; // The last time we warped to.
        readonly double warpTimeout = 300_000; // 300 seconds between using the warps.

        public Warps(World world)
        {
            warps = world.map.warps;

            Log.Info($"Loaded {warps.Count} warp{(warps.Count == 1 ? "" : "s")}.");
        }

        public void Warp(Player player, object id)
        {
            if (warps == null) return;

            // Prevent players from warping when they are jailed.
            if (player.IsJailed())
            {
                player.Notify($"warps:CANNOT_WARP_JAIL;time={player.GetJailDuration()}");
                return;
            }

            // Prevent warping outside the tutorial.
            if (!player.quests.IsTutorialFinished())
            {
                player.Notify("warps:CANNOT_WARP_TUTORIAL");
                return;
            }

            // Prevent warping while in combat.
            if (player.InCombat())
            {
                player.Notify("warps:CANNOT_WARP_COMBAT");
                return;
            }

            // Prevent teleporting too often.
            if (!IsCooldown(player))
            {
                player.Notify($"warps:CANNOT_WARP_COOLDOWN;time={GetDuration(player)}");
                return;
            }

            var warp = GetWarp(id);

            // No warp found.
            if (warp == null)
            {
                Log.Warning($"Could not find warp with id {JsString.Of(id ?? PixelHero.Js.Undefined.Value)}.");
                return;
            }

            // The `hasRequirement()` function will send notifications to the player.
            if (!HasRequirement(player, warp)) return;

            // Perform warping.
            Teleport(player, warp);

            player.SetLastWarp();
        }

        static void Teleport(Player player, JsObject warp)
        {
            double wx = warp.Num("x") ?? double.NaN, wy = warp.Num("y") ?? double.NaN;
            var x = Utils.RandomInt(wx, wx + (warp.Num("width") ?? double.NaN) - 1);
            var y = Utils.RandomInt(wy, wy + (warp.Num("height") ?? double.NaN) - 1);

            player.Teleport(x, y, true);

            player.Notify($"warps:WARPED_TO;name={Utils.FormatName(warp.Str("name"))}");
        }

        public bool UnlockedWarp(double level) => warps.Any(warp => ((JsObject)warp)["level"] is double l && l == level);

        bool IsCooldown(Player player) => GetDifference(player) > warpTimeout || player.IsAdmin();

        static bool HasRequirement(Player player, JsObject warp)
        {
            // Check if the warp has a level requirement.
            if (JsData.Truthy(warp["level"]) && player.level < (warp.Num("level") ?? double.NaN))
            {
                player.Notify($"warps:CANNOT_WARP_LEVEL;level={JsString.Of(warp["level"])}");
                return false;
            }

            // Check if the warp has a quest requirement.
            if (JsData.Truthy(warp["quest"]) && !(player.quests.Get(warp.Str("quest"))?.IsFinished() ?? false))
            {
                var quest = player.quests.Get(warp.Str("quest"));

                if (!(quest?.IsFinished() ?? false))
                {
                    player.Notify(
                        $"warps:CANNOT_WARP_QUEST;questName={(quest ?? throw new JsTypeError("Cannot read properties of undefined (reading 'name')")).name};name={Utils.FormatName(warp.Str("name"))}");
                    return false;
                }
            }

            // Check if the warp has an achievement requirement.
            if (JsData.Truthy(warp["achievement"]) && !(player.achievements.Get(warp.Str("achievement"))?.IsFinished() ?? false))
            {
                player.Notify("warps:CANNOT_WARP_ACHIEVEMENT");
                return false;
            }

            return true;
        }

        /// <summary>Modules.Warps[id]?.toLowerCase(): the enum's name for a number (or the number for a name).</summary>
        JsObject GetWarp(object id)
        {
            string warpName = null;

            if (id is double d && d % 1 == 0 && Enum.IsDefined(typeof(Modules.Warps), (int)d))
                warpName = JsString.ToLowerCase(((Modules.Warps)(int)d).ToString());
            else if (id is string s && Enum.TryParse<Modules.Warps>(s, false, out var warpEnum) && warpEnum.ToString() == s)
                warpName = JsString.ToLowerCase(JsString.Of((double)(int)warpEnum));

            return warps.Cast<JsObject>().FirstOrDefault(warp => warp.Str("name") == warpName && warpName != null);
        }

        public JsObject GetWarpByAchievement(string achievement) =>
            warps.Cast<JsObject>().FirstOrDefault(warp => warp.Str("achievement") == achievement);

        string GetDuration(Player player)
        {
            var difference = warpTimeout - GetDifference(player);

            if (difference == 0 || double.IsNaN(difference)) return "5 minutes";

            return difference > 60_000
                ? $"{JsString.Of(Math.Ceiling(difference / 60_000))} minutes"
                : $"{JsString.Of(Math.Floor(difference / 1000))} seconds";
        }

        static double GetDifference(Player player) => Node.DateNow() - player.lastWarp;
    }

    /// <summary>controllers/enchanter.ts.</summary>
    public sealed class Enchanter
    {
        public void Select(Player player, double index = -1)
        {
            // Prevent against packet manipulation.
            if (double.IsNaN(index) || index == -1)
            {
                player.Notify("enchant:CANNOT_ENCHANT");
                return;
            }

            // Grab the slot at the index provided.
            var slot = SlotAt(player, index);

            // Preliminary checks for item validity.
            if (!JsData.Truthy(slot?.key))
            {
                player.Notify("enchant:CANNOT_ENCHANT");
                return;
            }

            // Send a select packet to the client if the item is a shard.
            if (slot.key.StartsWith("shardt", StringComparison.Ordinal))
            {
                player.Send(new EnchantPacket(Opcodes.Enchant.Select, new JsObject("index", index, "isShard", true)));
                return;
            }

            // Check that the item is enchantable.
            if (slot.count > 1 || !slot.equippable || slot.maxStackSize > 1)
            {
                player.Notify("enchant:CANNOT_ENCHANT");
                return;
            }

            // Check that the item has available enchantments.
            if (player.inventory.GetItem(slot).GetAvailableEnchantments().Count == 0)
            {
                player.Notify("enchant:CANNOT_ENCHANT");
                return;
            }

            // Send a select packet to the client.
            player.Send(new EnchantPacket(Opcodes.Enchant.Select, new JsObject("index", index)));
        }

        /// <summary>inventory.get(index) where undefined is allowed (`slot?.key`).</summary>
        static Slot SlotAt(Player player, double index)
        {
            try
            {
                return player.inventory.Get(index);
            }
            catch (JsTypeError)
            {
                return null;
            }
        }

        public void Enchant(Player player, double index = -1, double? shardIndexValue = null)
        {
            var shardIndex = shardIndexValue ?? -1;

            // Prevent invalid data from being used.
            if (shardIndex == -1 || index == -1 || double.IsNaN(index) || double.IsNaN(shardIndex))
            {
                player.Notify("enchant:NO_ITEM_SELECTED");
                return;
            }

            // Attempt to grab the items from the inventory and verify that they exist.
            var itemSlot = SlotAt(player, index);
            var shardSlot = SlotAt(player, shardIndex);

            // Ensure the slot element has an item before creating an item instance.
            if (!JsData.Truthy(itemSlot?.key) || !JsData.Truthy(shardSlot?.key))
            {
                player.Notify("enchant:NO_ITEM_SELECTED");
                return;
            }

            // Ensure the player is providing shards to enchant an item.
            if (!shardSlot.key.StartsWith("shardt", StringComparison.Ordinal))
            {
                player.Notify("enchant:NO_SHARD");
                return;
            }

            // Item instance that we can manipulate and do checking on.
            var item = player.inventory.GetItem(itemSlot).Copy();
            var enchantments = item.GetAvailableEnchantments();

            // No enchantments available means the item cannot be enchanted.
            if (enchantments.Count == 0)
            {
                player.Notify("enchant:NO_ITEM_SELECTED");
                return;
            }

            // Get the shard tier by removing the `shardt` prefix from the shard key.
            var parts = shardSlot.key.Split("shardt");
            var tier = JsString.ParseInt(parts.Length > 1 ? parts[1] : null);
            var chance = Formulas.GetEnchantChance(tier);

            // Remove one shard from the player's inventory.
            player.inventory.Remove(shardIndex, 1);

            // Enchantment failed to apply.
            if (!chance) player.Notify("enchant:FAILED_ENCHANT");

            var enchantment = enchantments[(int)Utils.RandomInt(0, enchantments.Count - 1)];
            var level = Utils.RandomInt(1, tier);

            if (!CanEnchant(item, enchantment, level)) player.Notify("enchant:FAILED_ENCHANT");

            // Apply the enchantment only if the level is greater or it doesn't exist.
            item.SetEnchantment(enchantment, level);

            // Remove the item from the inventory and add the enchanted item.
            itemSlot.Update(item);

            // Send a notification to the player.
            player.Notify("enchant:SUCCESSFUL_ENCHANT");

            // Synchronize the inventory with the new slots.
            player.inventory.loadCallback?.Invoke();
        }

        static bool CanEnchant(Item item, Modules.Enchantment enchantment, double level)
        {
            if (!item.HasEnchantment(enchantment)) return true;

            return level > item.GetEnchantmentLevel((double)enchantment);
        }
    }
}
