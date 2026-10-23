using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Controllers
{
    /// <summary>controllers/crafting.ts.</summary>
    public sealed class Crafting
    {
        /// <summary>Modules.Skills[type].toLowerCase(); undefined throws when lower-cased.</summary>
        static string SkillName(double type)
        {
            if (type % 1 != 0 || !Enum.IsDefined(typeof(Modules.Skills), (int)type))
                throw new JsTypeError("Cannot read properties of undefined (reading 'toLowerCase')");

            return JsString.ToLowerCase(((Modules.Skills)(int)type).ToString());
        }

        public void Open(Player player, Modules.Skills type)
        {
            // Attempt to obtain the keys and if none are available then we cannot open the interface.
            var previews = GetCraftingPreviews(type);

            if (previews == null)
            {
                player.Notify("misc:CANNOT_DO_THAT");
                return;
            }

            // Set the currently active crafting interface.
            player.activeCraftingInterface = (double)type;

            // Send a packet to the client to open the crafting interface and pass the previews.
            player.Send(new CraftingPacket(Opcodes.Crafting.Open, new JsObject("type", (double)type, "previews", previews)));
        }

        public void Select(Player player, string key)
        {
            // Get the crafting item based on the index.
            var skillName = SkillName(player.activeCraftingInterface);
            var craftingData = ServerData.Crafting.Obj(skillName);

            // Another layer of checking the validity of the crafting data.
            if (craftingData == null)
            {
                player.Notify("crafting:INVALID_DATA");
                return;
            }

            // Get the item key from the crafting keys based on the index the player selected.
            var craftingItem = key == null ? null : craftingData.Obj(key);

            // Ensure that the item exists.
            if (craftingItem == null)
            {
                player.Notify("crafting:INVALID_ITEM");
                return;
            }

            // Send the requirements and result to the player.
            player.Send(new CraftingPacket(Opcodes.Crafting.Select, new JsObject(
                "key", key,
                "name", ServerData.Items.Obj(key)?.Str("name"),
                "level", craftingItem["level"],
                "result", craftingItem.Obj("result")["count"],
                "requirements", GetRequirements(craftingItem))));
        }

        public void Craft(Player player, string key, double count = 1)
        {
            // Prevent players from spamming the craft menu.
            if (!player.CanCraft()) return;

            // Ensure the index is valid.
            if (!JsData.Truthy(key))
            {
                player.Notify("crafting:SELECT_ITEM");
                return;
            }

            // Verify that the count is valid.
            if (double.IsNaN(count) || count < 1) count = 1;

            // Invalid counts are set to 1, prevent client manipulation.
            if (count != 1 && count != 5 && count != 10) count = 1;

            var skillName = SkillName(player.activeCraftingInterface);
            var craftingData = ServerData.Crafting.Obj(skillName);

            // Verify the crafting data
            if (craftingData == null)
            {
                player.Notify("crafting:INVALID_DATA");
                return;
            }

            // The skill that is being used to craft the item.
            var craftingItem = craftingData.Obj(key);
            var skill = player.skills.Get((Modules.Skills)(int)GetSkillByInterface(player.activeCraftingInterface));

            // Verify the crafting data
            if (craftingItem == null)
            {
                player.Notify("crafting:INVALID_ITEM");
                return;
            }

            var level = craftingItem.Num("level") ?? double.NaN;

            // Ensure the player has the correct level to craft the item.
            if (skill.level < level)
            {
                player.Notify(InvalidLevel(player.activeCraftingInterface, level));
                return;
            }

            var actualCount = count;
            var requirements = craftingItem.Arr("requirements");

            // Calculate the amount of each requirement we can take from the player's inventory.
            foreach (JsObject requirement in requirements)
            {
                var requirementCount = requirement.Num("count") ?? double.NaN;

                // Stop the loop if the player does not have one of the requirements.
                if (!player.inventory.HasItem(requirement.Str("key"), requirementCount))
                {
                    player.Notify("crafting:INVALID_ITEMS");
                    return;
                }

                var itemCount = player.inventory.Count(requirement.Str("key"));
                var requestedAmount = requirementCount * count;

                // Requested amount is greater than the amount of items the player has.
                if (requestedAmount > itemCount) actualCount = Math.Floor(itemCount / requirementCount);
            }

            // Remove the items from the player's inventory.
            foreach (JsObject requirement in requirements)
                player.inventory.RemoveItem(requirement.Str("key"), (requirement.Num("count") ?? double.NaN) * actualCount);

            double failures = 0;

            var chance = JsData.Truthy(craftingItem["chance"]) ? (double)craftingItem["chance"] : 100;

            // Roll the random chance for every count of the item.
            for (var i = 0; i < actualCount; i++)
                if (Utils.RandomInt(0, 100) > chance + skill.level)
                    failures++;

            // Remove the amount of failures from the actual count.
            actualCount -= failures;

            // Notify the players of the amount of failures.
            if (failures > 0)
                player.Notify(failures > 1
                    ? $"crafting:FAILED_CRAFT;failedText={JsString.Of(failures)}x {ServerData.Items.Obj(key).Str("name")}."
                    : "crafting:FAILED_CRAFT_ONE");

            // Award experience to the player.
            skill.AddExperience((craftingItem.Num("experience") ?? double.NaN) * actualCount);

            // Add the crafted item to the player's inventory.
            player.inventory.Add(new Item(key, -1, -1, false,
                (craftingItem.Obj("result").Num("count") ?? double.NaN) * actualCount));
        }

        public List<object> GetCraftingPreviews(Modules.Skills skill)
        {
            // Skill name based on the enum.
            var skillName = SkillName((double)skill);
            var data = ServerData.Crafting.Obj(skillName);
            var previews = new List<object>();

            // Iterate through the crafting data and add the key and level to the previews.
            if (data != null)
                foreach (var pair in data)
                    previews.Add(new JsObject("key", pair.Key, "level", ((JsObject)pair.Value)["level"]));

            return previews;
        }

        public List<object> GetRequirements(JsObject craftingItem)
        {
            var requirements = craftingItem.Arr("requirements");

            // Iterate through the requirements and add the name of the item.
            foreach (JsObject requirement in requirements)
            {
                var item = ServerData.Items.Obj(requirement.Str("key"));

                if (JsData.Truthy(item?["name"])) requirement.Set("name", item.Str("name"));
            }

            return requirements;
        }

        static double GetSkillByInterface(double activeCraftingInterface) =>
            activeCraftingInterface == (double)Modules.Skills.Smelting ? (double)Modules.Skills.Smithing
            : activeCraftingInterface == (double)Modules.Skills.Chiseling ? (double)Modules.Skills.Crafting
            : activeCraftingInterface;

        /// <summary>common/text/en/crafting.ts INVALID_LEVEL.</summary>
        static string InvalidLevel(double type, double level)
        {
            var text = JsString.Of(level);
            return (Modules.Skills)(int)type switch
            {
                Modules.Skills.Fletching => $"You must be level {text} in order to fletch this.",
                Modules.Skills.Chiseling or Modules.Skills.Crafting => $"You must be level {text} in order to craft this.",
                Modules.Skills.Cooking => $"You must be level {text} in order to cook this.",
                Modules.Skills.Smithing => $"You must be level {text} in order to smith this.",
                Modules.Skills.Smelting => $"You must be level {text} in order to smelt this.",
                Modules.Skills.Alchemy => $"You must be level {text} in order to brew this.",
                _ => "INVALID_LEVEL() NOT IMPLEMENTED"
            };
        }
    }
}
