using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>game/entity/objects/item.ts.</summary>
    public sealed class Item : Entity
    {
        readonly JsObject data;

        // Item Data
        string itemType = "object"; // weapon, armour, pendant, etc.
        public bool stackable;
        public bool edible;
        public bool interactable;
        public double maxStackSize = 1; // Default max stack size.
        public IItemPlugin plugin;

        // Store variables
        public double price = -2;
        public double storeCount = -1;

        // Requirement for equipping the item.
        double level = -1;
        string skill = "";
        string achievement = "";
        string quest = "";

        // Points usage
        public double manaCost;

        // Pet information
        public string pet = "";

        // Item information
        public string description = "";
        public string projectileName = "arrow";

        // Equipment variables
        public double attackRate = (double)Modules.Defaults.ATTACK_RATE;
        public bool twoHanded;
        public bool poisonous;
        public bool freezing;
        public bool burning;
        public JsObject light = new();
        public string weaponType = "";

        // Bowl variables
        public bool smallBowl;
        public bool mediumBowl;

        // Stats
        public JsObject attackStats;
        public JsObject defenseStats;

        // Bonuses
        public JsObject bonuses;

        // Miscellaneous variables
        public double movementModifier = -1;
        public double? stockAmount; // Used for stores to increase count by this amount.
        public double maxCount = -1; // Used for stores to know maximum limit.
        public double lumberjacking = -1;
        public double mining = -1;
        public double fishing = -1;
        public double attackRange = 1;
        public bool exists = true;
        public bool undroppable;

        double respawnDelay = (double)Modules.ItemDefaults.RESPAWN_DELAY;
        readonly double despawnDuration = (double)Modules.ItemDefaults.DESPAWN_DURATION;
        readonly double blinkDelay = (double)Modules.ItemDefaults.BLINK_DELAY;

        JsTimeout blinkTimeout;
        JsTimeout despawnTimeout;

        Action blinkCallback;
        Action respawnCallback;
        Action despawnCallback;

        public bool dropped;
        public double count;
        public JsObject enchantments;
        public string owner;

        public Item(string key, double x, double y, bool dropped = false, double count = -1, JsObject enchantments = null,
            string owner = "")
            : base(Utils.CreateInstance(Modules.EntityType.Item), key, x, y)
        {
            // Parameter properties, then the field initializers that call functions.
            this.dropped = dropped;
            this.count = count;
            this.enchantments = enchantments ?? new JsObject();
            this.owner = owner ?? "";
            attackStats = Utils.GetEmptyStats();
            defenseStats = Utils.GetEmptyStats();
            bonuses = Utils.GetEmptyBonuses();

            // Prevent invalid count values.
            this.count = Utils.SanitizeNumber(this.count);

            data = ServerData.Items.Obj(key);

            if (data == null)
            {
                Log.Error($"[Item] Could not find data for {key}.");
                exists = false;
                return;
            }

            // Set all the item data (set defaults if value doesn't exist).
            itemType = data.Str("type");
            this.key = Or(data["spriteName"], this.key) as string;
            name = data.Str("name");
            stackable = JsData.Truthy(Or(data["stackable"], stackable));
            edible = JsData.Truthy(Or(data["edible"], edible));
            interactable = JsData.Truthy(Or(data["interactable"], interactable));
            maxStackSize = GetMaxStackSize(data.Num("maxStackSize"));
            price = (double)Or(data["price"], price);
            storeCount = (double)Or(data["storeCount"], storeCount);
            level = (double)Or(data["level"], level);
            skill = Or(data["skill"], skill) as string;
            attackStats = (JsObject)Or(data["attackStats"], attackStats);
            defenseStats = (JsObject)Or(data["defenseStats"], defenseStats);
            bonuses = (JsObject)Or(data["bonuses"], bonuses);
            attackRate = (double)Or(data["attackRate"], attackRate);
            twoHanded = JsData.Truthy(Or(data["twoHanded"], twoHanded));
            poisonous = JsData.Truthy(Or(data["poisonous"], poisonous));
            freezing = JsData.Truthy(Or(data["freezing"], freezing));
            burning = JsData.Truthy(Or(data["burning"], burning));
            light = (JsObject)Or(data["light"], light);
            movementModifier = (double)Or(data["movementModifier"], movementModifier);
            lumberjacking = (double)Or(data["lumberjacking"], lumberjacking);
            mining = (double)Or(data["mining"], mining);
            fishing = (double)Or(data["fishing"], fishing);
            undroppable = JsData.Truthy(Or(data["undroppable"], undroppable));
            respawnDelay = (double)Or(data["respawnDelay"], respawnDelay);
            attackRange = JsData.Truthy(data["attackRange"]) ? (double)data["attackRange"] : GetDefaultAttackRange();
            projectileName = Or(data["projectileName"], projectileName) as string;
            description = Or(data["description"], description) as string;
            manaCost = (double)Or(data["manaCost"], manaCost);
            weaponType = Or(data["weaponType"], weaponType) as string;
            smallBowl = JsData.Truthy(Or(data["smallBowl"], smallBowl));
            mediumBowl = JsData.Truthy(Or(data["mediumBowl"], mediumBowl));
            pet = Or(data["pet"], pet) as string;

            if (JsData.Truthy(data["plugin"])) LoadPlugin();
        }

        /// <summary>a || b.</summary>
        static object Or(object a, object b) => JsData.Truthy(a) ? a : b;

        void LoadPlugin()
        {
            var pluginName = data.Str("plugin");

            if (!ItemPlugins.Has(pluginName))
            {
                Log.Error($"[Item] Could not find plugin {pluginName}.");
                return;
            }

            plugin = ItemPlugins.Create(pluginName, data);
        }

        public Item Copy() =>
            // These are weird hacks because of pointer references junk.
            new($"{key}", x, y, dropped, Utils.SanitizeNumber(count), CopyEnchantments(), owner);

        JsObject CopyEnchantments()
        {
            var copy = new JsObject();

            foreach (var pair in enchantments) copy.Set(pair.Key, pair.Value);

            return copy;
        }

        public void Destroy()
        {
            Node.ClearTimeout(blinkTimeout);
            Node.ClearTimeout(despawnTimeout);

            if (!dropped) Respawn();
        }

        public void Despawn(bool noTimeout = false)
        {
            if (noTimeout)
            {
                despawnCallback?.Invoke();
                return;
            }

            blinkTimeout = Node.SetTimeout(() =>
            {
                blinkCallback?.Invoke();

                // Clear the owner of the item when it starts blinking.
                owner = "";

                despawnTimeout = Node.SetTimeout(() => despawnCallback?.Invoke(), despawnDuration);
            }, blinkDelay);
        }

        public void Respawn() => Node.SetTimeout(() => respawnCallback?.Invoke(), respawnDelay);

        public bool CanEquip(Player player)
        {
            var requirement = GetRequirement();

            if (JsData.Truthy(skill))
            {
                var skillObject = player.skills.Get(Utils.GetSkill(skill));

                // Separate conditional if skill exists.
                if (skillObject != null)
                {
                    if (skillObject.level < requirement)
                    {
                        // If the player's skill level is less than the requirement.
                        player.Notify(
                            $"item:SKILL_LEVEL_REQUIREMENT_EQUIP;skill={skillObject.name};level={JsString.Of(requirement)}");
                        return false;
                    }

                    return true; // If the player's skill fulfills the requirement.
                }
            }

            // Default to using the total level for the requirement.
            if (player.level < requirement)
            {
                player.Notify($"item:TOTAL_LEVEL_REQUIREMENT;level={JsString.Of(requirement)}");
                return false;
            }

            // Check if the item has an achievement requirement and if it is completed.
            if (JsData.Truthy(achievement) && !(player.achievements.Get(achievement)?.IsFinished() ?? false))
            {
                player.Notify("item:REQUIRES_ACHIEVEMENT");
                return false;
            }

            // If an item has a quest requirement, we check it here.
            if (JsData.Truthy(quest))
            {
                var questObject = player.quests.Get(quest);

                // Send an error if the quest cannot be found.
                if (questObject == null)
                {
                    Log.Error($"[Item: {key}] Could not find quest {quest}.");
                    return false;
                }

                // Check if the quest is finished.
                if (!questObject.IsFinished())
                {
                    player.Notify($"item:REQUIRES_QUEST;quest={questObject.name}");
                    return false;
                }
            }

            return true;
        }

        public Modules.Equipment? GetEquipmentType() => itemType switch
        {
            "helmet" => Modules.Equipment.Helmet,
            "chestplate" => Modules.Equipment.Chestplate,
            "legplates" => Modules.Equipment.Legplates,
            "skin" => Modules.Equipment.ArmourSkin,
            "weapon" or "weaponarcher" or "weaponmagic" => Modules.Equipment.Weapon,
            "weaponskin" => Modules.Equipment.WeaponSkin,
            "pendant" => Modules.Equipment.Pendant,
            "boots" => Modules.Equipment.Boots,
            "ring" => Modules.Equipment.Ring,
            "arrow" => Modules.Equipment.Arrows,
            "shield" => Modules.Equipment.Shield,
            "cape" => Modules.Equipment.Cape,
            _ => null
        };

        public double GetRequirement()
        {
            if (level != -1) return level;

            return 0;
        }

        public string GetDescription() => description;

        public double GetMaxStackSize(double? maxStackSize = null)
        {
            if (stackable)
                return maxStackSize is double m && m != 0 && !double.IsNaN(m) ? m : Constants.MAX_STACK;

            return this.maxStackSize;
        }

        double GetDefaultAttackRange() => IsArcherWeapon() ? Constants.ARCHER_ATTACK_RANGE : attackRange;

        public List<Modules.AttackStyle> GetAttackStyles()
        {
            // Why would you want to attack with a non-weapon?
            if (!IsWeapon()) return new List<Modules.AttackStyle>();

            // If the item has a specified attack style, we use that.
            return weaponType switch
            {
                "sword" => new List<Modules.AttackStyle>
                {
                    Modules.AttackStyle.Stab, Modules.AttackStyle.Slash, Modules.AttackStyle.Shared,
                    Modules.AttackStyle.Defensive
                },
                "bigsword" => new List<Modules.AttackStyle>
                    { Modules.AttackStyle.Slash, Modules.AttackStyle.Crush, Modules.AttackStyle.Defensive },
                "axe" => new List<Modules.AttackStyle>
                    { Modules.AttackStyle.Hack, Modules.AttackStyle.Chop, Modules.AttackStyle.Defensive },
                "pickaxe" => new List<Modules.AttackStyle> { Modules.AttackStyle.Stab, Modules.AttackStyle.Defensive },
                "blunt" => new List<Modules.AttackStyle>
                    { Modules.AttackStyle.Crush, Modules.AttackStyle.Shared, Modules.AttackStyle.Defensive },
                "spear" => new List<Modules.AttackStyle>
                    { Modules.AttackStyle.Stab, Modules.AttackStyle.Slash, Modules.AttackStyle.Defensive },
                "scythe" => new List<Modules.AttackStyle>
                    { Modules.AttackStyle.Slash, Modules.AttackStyle.Crush, Modules.AttackStyle.Defensive },
                "bow" => new List<Modules.AttackStyle>
                    { Modules.AttackStyle.Accurate, Modules.AttackStyle.Fast, Modules.AttackStyle.LongRange },
                "whip" => new List<Modules.AttackStyle> { Modules.AttackStyle.Slash, Modules.AttackStyle.Defensive },
                "staff" => new List<Modules.AttackStyle> { Modules.AttackStyle.Focused, Modules.AttackStyle.LongRange },
                _ => new List<Modules.AttackStyle>()
            };
        }

        public List<Modules.Enchantment> GetAvailableEnchantments() => itemType switch
        {
            "weapon" => new List<Modules.Enchantment>
                { Modules.Enchantment.Bloodsucking, Modules.Enchantment.Critical, Modules.Enchantment.DoubleEdged },
            "weaponarcher" or "weaponmagic" => new List<Modules.Enchantment>
                { Modules.Enchantment.Explosive, Modules.Enchantment.Stun },
            "armour" or "armourarcher" => new List<Modules.Enchantment>
                { Modules.Enchantment.Evasion, Modules.Enchantment.Thorns, Modules.Enchantment.AntiStun },
            _ => new List<Modules.Enchantment>()
        };

        /// <summary>this.enchantments[enchantment].level.</summary>
        public double GetEnchantmentLevel(double enchantment)
        {
            var entry = enchantments.Obj(JsString.Of(enchantment)) ??
                        throw new JsTypeError("Cannot read properties of undefined (reading 'level')");
            return entry.Num("level") ?? double.NaN;
        }

        public bool HasEnchantment(Modules.Enchantment id) => enchantments.Has(((int)id).ToString());

        public bool IsEnchanted() => enchantments.Count > 0;

        public bool IsOwner(string username)
        {
            if (!JsData.Truthy(owner)) return true;

            return owner == username;
        }

        static readonly string[] EquippableTypes =
        {
            "helmet", "chestplate", "legplates", "skin", "weapon", "weaponarcher", "weaponmagic", "weaponskin",
            "pendant", "boots", "ring", "arrow", "shield", "cape"
        };

        public bool IsEquippable() => EquippableTypes.Contains(itemType);

        /// <summary>itemType.includes('weapon'); an undefined type throws like the original.</summary>
        public bool IsWeapon() =>
            (itemType ?? throw new JsTypeError("Cannot read properties of undefined (reading 'includes')"))
            .Contains("weapon", StringComparison.Ordinal);

        public bool IsArcherWeapon() => itemType == "weaponarcher";

        public bool IsMagicWeapon() => itemType == "weaponmagic";

        public bool IsSmallBowl() => smallBowl;

        public bool IsMediumBowl() => mediumBowl;

        public bool IsPetItem() => pet != "";

        public bool IsBow() => weaponType == "bow";

        public bool IsTwoHanded() => twoHanded;

        public void SetEnchantment(Modules.Enchantment id, double level) =>
            enchantments.Set(((int)id).ToString(), new JsObject("level", level));

        public override JsObject Serialize()
        {
            var data = base.Serialize();

            data.Set("count", count);
            data.Set("enchantments", enchantments);

            return data;
        }

        public void OnRespawn(Action callback) => respawnCallback = callback;

        public void OnBlink(Action callback) => blinkCallback = callback;

        public void OnDespawn(Action callback) => despawnCallback = callback;
    }
}
