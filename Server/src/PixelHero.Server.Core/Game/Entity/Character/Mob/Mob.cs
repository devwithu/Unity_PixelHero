using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Info;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>game/entity/character/mob/mob.ts.</summary>
    public sealed class Mob : Character
    {
        public sealed class ItemDrop
        {
            public string key;
            public double count;
        }

        JsObject data;

        public double spawnX;
        public double spawnY;

        public object description = ""; // string | string[]

        // An achievement that is completed upon defeating the mob.
        public string achievement = "";

        public Area area;
        public Chest chest; // Used for mimics since they belong to a chest

        public bool boss;
        public bool respawnable = true;
        public bool miniboss;
        public bool roaming = true; // Roaming is true by default
        public bool poisonous;
        public bool freezing;
        public bool burning;

        public bool aggressive;
        public bool alwaysAggressive;
        bool hiddenName;

        // Stats & Bonuses
        JsObject attackStats;
        JsObject defenseStats;
        JsObject bonuses;

        List<object> drops = new();
        object dropTables = new List<object>();

        JsObject skills;

        public double respawnDelay = (double)Modules.MobDefaults.RESPAWN_DELAY; // Use default spawn delay if not specified.
        public double aggroRange = (double)Modules.MobDefaults.AGGRO_RANGE;
        public double roamDistance = (double)Modules.MobDefaults.ROAM_DISTANCE;

        MobHandler handler;

        double lastChangedTarget = 0;

        Action respawnCallback;
        public Action<string> talkCallback;
        public Action roamingCallback;

        public Mob(World world, string key, double x, double y, bool plugin = false)
            : base(Utils.CreateInstance(Modules.EntityType.Mob), world, key, x, y)
        {
            attackStats = Utils.GetEmptyStats();
            defenseStats = Utils.GetEmptyStats();
            bonuses = Utils.GetEmptyBonuses();
            skills = new JsObject(
                "accuracy", (double)Modules.MobDefaults.ACCURACY_LEVEL,
                "strength", (double)Modules.MobDefaults.STRENGTH_LEVEL,
                "defense", (double)Modules.MobDefaults.DEFENSE_LEVEL,
                "magic", (double)Modules.MobDefaults.MAGIC_LEVEL,
                "archery", (double)Modules.MobDefaults.ARCHERY_LEVEL);

            spawnX = this.x;
            spawnY = this.y;

            data = ServerData.Mobs.Obj(key);

            if (data == null)
            {
                Log.Error($"[Mob] Could not find data for {key}.");
                return;
            }

            LoadData(data);
            LoadPlugin(plugin ? key : data.Str("plugin")); // plugin boolean is used to load plugin based on key.
            LoadSpawns();

            if (handler == null) Log.Error($"[Mob] Mob handler for {key} is not initialized.");
        }

        void LoadData(JsObject data)
        {
            if (JsData.Truthy(data["hitPoints"])) hitPoints.UpdateHitPoints(data.Num("hitPoints") ?? double.NaN);

            name = Or(data["name"], name) as string;
            description = Or(data["description"], description);
            drops = Or(data["drops"], drops) as List<object>;
            dropTables = Or(data["dropTables"], dropTables);
            level = (double)Or(data["level"], level);
            skills = (JsObject)Or(data["skills"], skills);
            attackStats = (JsObject)Or(data["attackStats"], attackStats);
            defenseStats = (JsObject)Or(data["defenseStats"], defenseStats);
            bonuses = (JsObject)Or(data["bonuses"], bonuses);
            attackRange = (double)Or(data["attackRange"], attackRange);
            aggroRange = (double)Or(data["aggroRange"], aggroRange);
            aggressive = JsData.Truthy(Or(data["aggressive"], aggressive));
            alwaysAggressive = JsData.Truthy(Or(data["alwaysAggressive"], alwaysAggressive));
            attackRate = (double)Or(data["attackRate"], attackRate);
            respawnDelay = (double)Or(data["respawnDelay"], respawnDelay);
            movementSpeed = (double)Or(data["movementSpeed"], movementSpeed);
            boss = JsData.Truthy(Or(data["boss"], boss));
            miniboss = JsData.Truthy(Or(data["miniboss"], miniboss));
            poisonous = JsData.Truthy(Or(data["poisonous"], poisonous));
            freezing = JsData.Truthy(Or(data["freezing"], freezing));
            burning = JsData.Truthy(Or(data["burning"], burning));
            hiddenName = JsData.Truthy(Or(data["hiddenName"], hiddenName));
            achievement = Or(data["achievement"], achievement) as string;
            projectileName = Or(data["projectileName"], projectileName) as string;
            roamDistance = (double)Or(data["roamDistance"], roamDistance);
            healRate = (double)Or(data["healRate"], healRate);
            roaming = data.Has("roaming") && data["roaming"] is not Undefined ? JsData.Truthy(data["roaming"]) : roaming;
            plateauLevel = world.map.GetPlateauLevel(spawnX, spawnY);

            // Handle hiding the entity's name.
            if (hiddenName) name = "";
        }

        /// <summary>a || b.</summary>
        static object Or(object a, object b) => JsData.Truthy(a) ? a : b;

        void LoadPlugin(string pluginName)
        {
            if (string.IsNullOrEmpty(pluginName) || !MobPlugins.Has(pluginName))
            {
                // Initialize a mob handler since we couldn't find a plugin.
                handler = new MobHandler(this);
                return;
            }

            handler = MobPlugins.Create(pluginName, this);
        }

        void LoadSpawns()
        {
            // Coordinates of the mob's spawn location as a key.
            var key = $"{JsString.Of(spawnX)}-{JsString.Of(spawnY)}";

            // Check the coordinate key against the `spawns.json` dictionary JSON file.
            if (!ServerData.Spawns.Has(key)) return;

            var data = ServerData.Spawns.Obj(key);

            LoadData(data);

            // Custom plugin can be specified on a per-instance bassis.
            if (JsData.Truthy(data["plugin"])) LoadPlugin(data.Str("plugin"));
        }

        public override void Heal(double amount = 1) => Heal(amount, "passive");

        public void Heal(double amount, string type)
        {
            base.Heal(amount);

            if (type == "hitpoints")
            {
                // Increment hitpoints by the amount.
                hitPoints.Increment(amount);

                // Send the heal packet to the nearby regions.
                SendToRegions(new HealPacket(new JsObject(
                    "instance", instance,
                    "type", type,
                    "amount", amount)));
            }
        }

        public void Destroy()
        {
            dead = true;
            damageTable = new JsObject<double?>();

            combat.Stop();

            Respawn();

            SetPoison();

            SetPosition(spawnX, spawnY, false);
        }

        public void Despawn()
        {
            dead = true;

            combat.Stop();

            world.entities.RemoveMob(this);
        }

        public void Drop(Player player)
        {
            var drops = GetDrops(player);

            // No drops were calculated, so we stop here.
            if (drops.Count == 0) return;

            // If it's a single drop, drop only the item.
            if (drops.Count == 1)
            {
                world.entities.SpawnItem(drops[0].key, x, y, true, drops[0].count, new JsObject(), player.username);
                return;
            }

            var items = new List<Item>();

            // Create the item objects from the ItemDrop types.
            foreach (var drop in drops) items.Add(new Item(drop.key, -1, -1, false, drop.count));

            // Spawn the loot bag.
            world.entities.SpawnLootBag(x, y, player.username, items);
        }

        public override void Teleport(double x, double y, bool withAnimation = false)
        {
            SetPosition(x, y, false);

            teleporting = true;

            SendToRegions(new TeleportPacket(new JsObject(
                "instance", instance,
                "x", x,
                "y", y,
                "withAnimation", withAnimation)));

            // Untoggle the teleporting flag after 500ms.
            Node.SetTimeout(() => teleporting = false, 500);
        }

        /// <summary>setPosition(x, y) on a mob sends the movement packet (withPacket defaults to true).</summary>
        public override void SetPosition(double x, double y) => SetPosition(x, y, true);

        /// <summary>setPosition(x, y, withPacket).</summary>
        public override void SetPosition(double x, double y, bool withPacket)
        {
            if (teleporting) return;

            base.SetPosition(x, y, false);

            CalculateOrientation();

            if (withPacket)
                world.Push(Modules.PacketType.Regions, new PacketData
                {
                    region = region,
                    packet = new MovementPacket(Opcodes.Movement.Move, new JsObject(
                        "instance", instance,
                        "x", x,
                        "y", y))
                });

            lastMovement = Node.DateNow();
        }

        public List<ItemDrop> GetDrops(Player player)
        {
            var drops = new List<ItemDrop>(); // The items that the mob will drop
            var randomItem = GetRandomItem(this.drops, player);

            // Add a random item from the mob's personal list of drops.
            if (randomItem != null) drops.Add(randomItem);

            // Add items from the mob's drop table.
            drops = drops.Concat(GetDropTableItems(player)).ToList();

            return drops;
        }

        List<ItemDrop> GetDropTableItems(Player player)
        {
            var drops = new List<ItemDrop>();

            var keys = dropTables switch
            {
                List<object> list => list,
                JsObject obj => obj.Values,
                _ => new List<object>()
            };

            foreach (var key in keys)
            {
                var table = ServerData.Tables.Obj(JsString.Of(key)); // Pick the table from the list of drop tables.

                if (table == null)
                    throw new JsTypeError("Cannot read properties of undefined (reading 'achievement')");

                // Player doesn't have the achievement for the drop table completed.
                if (JsData.Truthy(table["achievement"]) &&
                    !(player.achievements.Get(table.Str("achievement"))?.IsFinished() ?? false))
                    continue;

                // Player doesn't have the quest completed to have access to the drop table.
                if (JsData.Truthy(table["quest"]) && !(player.quests.Get(table.Str("quest"))?.IsFinished() ?? false))
                    continue;

                var randomItem = GetRandomItem(table.Arr("drops"), player);

                // Add a random item from the table.
                if (randomItem != null) drops.Add(randomItem);
            }

            return drops;
        }

        ItemDrop GetRandomItem(List<object> items, Player player)
        {
            // No items to pick from.
            if (items.Count == 0) return null;

            var drop = items[(int)Utils.RandomInt(0, items.Count - 1)] as JsObject;
            var count = Or(drop["count"], 1.0);

            switch (drop.Str("key"))
            {
                case "gold":
                    count = Utils.RandomInt(level, level * 10);
                    break;

                case "flask":
                    count = Utils.RandomInt(1, 3);
                    break;

                case "arrow":
                    count = Utils.RandomInt(1, level);
                    break;

                case "firearrow":
                    count = Utils.RandomInt(1, Math.Ceiling(level / 2));
                    break;

                case "feather":
                    count = Utils.RandomInt(1, level);
                    break;
            }

            // Drop a varying amount depending on the variable property.
            if (JsData.Truthy(drop["variable"])) count = Utils.RandomInt(1, (double)count);

            // Check for quest/achievement requirements.
            if (JsData.Truthy(drop["quest"]) && !FullfillsQuest(player, drop)) return null;
            if (JsData.Truthy(drop["achievement"]) &&
                !(player.achievements.Get(drop.Str("achievement"))?.IsFinished() ?? false))
                return null;

            var probability = world.GetDropProbability();

            // If the chance is greater than the drop probability, we adjust the drop
            if ((drop.Num("chance") ?? double.NaN) > probability) drop.Set("chance", probability);

            return Utils.RandomInt(0, probability) < (drop.Num("chance") ?? double.NaN)
                ? new ItemDrop { key = drop.Str("key"), count = (double)count }
                : null;
        }

        /// <summary>Object.entries(damageTable).sort((a, b) => b[1] - a[1]).</summary>
        public List<KeyValuePair<string, double?>> GetDamageTable()
        {
            var entries = damageTable.Entries;
            // Array.prototype.sort is stable; OrderBy is too.
            return entries.OrderBy(pair => pair, Comparer<KeyValuePair<string, double?>>.Create((a, b) =>
            {
                var difference = (b.Value ?? double.NaN) - (a.Value ?? double.NaN);
                return double.IsNaN(difference) ? 0 : Math.Sign(difference);
            })).ToList();
        }

        void CalculateOrientation()
        {
            if (oldX < x) SetOrientation((double)Modules.Orientation.Right);
            else if (oldX > x) SetOrientation((double)Modules.Orientation.Left);
            else if (oldY < y) SetOrientation((double)Modules.Orientation.Down);
            else if (oldY > y) SetOrientation((double)Modules.Orientation.Up);
        }

        public bool CanAggro(Player player)
        {
            // Skip if mob has a target or the player targeted isn't fully loaded yet.
            if (!player.ready) return false;

            // Prevent aggro if we already have a target.
            if (target != null)
            {
                // However, for bosses, we want to add the player to the list of attackers.
                if (boss) AddAttacker(player);

                return false;
            }

            // Check for aggressive properties of the mob.
            if (!aggressive && !alwaysAggressive) return false;

            // Only aggro if the mob's level * 3 is lower than the player's level.
            if (Math.Floor(level * 3) < player.level && !alwaysAggressive) return false;

            // Ensure the mob isnear the player within its aggro range.
            return IsNear(player, aggroRange);
        }

        public void AddToChestArea(Areas chestAreas)
        {
            // In case chests area does not exist in the map.
            if (chestAreas == null) return;

            area = chestAreas.InArea(x, y);

            area?.AddEntity(this);
        }

        public void SendToSpawn()
        {
            combat.Stop();

            SetPosition(spawnX, spawnY);
        }

        /// <summary>outsideRoaming(x?, y?, distance = roamDistance): `x || this.x`, `y || this.y`.</summary>
        public bool OutsideRoaming(double? x = null, double? y = null, double? distance = null) =>
            Utils.GetDistance(
                x is double dx && dx != 0 && !double.IsNaN(dx) ? dx : this.x,
                y is double dy && dy != 0 && !double.IsNaN(dy) ? dy : this.y,
                spawnX, spawnY) > (distance ?? roamDistance);

        string GetNameColour(Player player)
        {
            if (area != null) return NameColour(Modules.SpecialEntityTypes.Area);
            if (boss) return NameColour(Modules.SpecialEntityTypes.Boss);
            if (miniboss) return NameColour(Modules.SpecialEntityTypes.Miniboss);

            if (player != null)
            {
                if (player.quests.GetQuestFromMob(this) != null) return NameColour(Modules.SpecialEntityTypes.Quest);

                if (player.achievements.GetAchievementFromEntity(this) != null)
                    return NameColour(Modules.SpecialEntityTypes.Achievement);
            }

            return "";
        }

        static JsObject _nameColours, _entityScale;

        /// <summary>Modules.NameColours[type].</summary>
        public static string NameColour(Modules.SpecialEntityTypes type)
        {
            _nameColours ??= (JsObject)JsData.Convert(JsJson.Parse(ModuleTables.NameColoursJson));
            return _nameColours.Str(((int)type).ToString());
        }

        public override JsObject GetDisplayInfo(Player player)
        {
            var displayInfo = new JsObject(
                "instance", instance,
                "colour", GetNameColour(player));

            // Only minibosses have a special scale, we send the scale only if the mob is a miniboss.
            if (miniboss)
            {
                _entityScale ??= (JsObject)JsData.Convert(JsJson.Parse(ModuleTables.EntityScaleJson));
                displayInfo.Set("scale", _entityScale[((int)Modules.SpecialEntityTypes.Miniboss).ToString()]);
            }

            return displayInfo;
        }

        public string GetDescription()
        {
            if (description is List<object> list)
                return JsString.Of(list[(int)Utils.RandomInt(0, list.Count - 1)]);

            return description as string;
        }

        public override bool HasDisplayInfo(Player player)
        {
            if (area != null || boss || miniboss) return true;

            // Check if player is provided and the mob's information against their quests/achievements.
            if (player != null)
            {
                if (player.quests?.GetQuestFromMob(this) != null) return true;
                if (player.achievements?.GetAchievementFromEntity(this) != null) return true;
            }

            return false;
        }

        bool FullfillsQuest(Player player, JsObject drop)
        {
            var quest = drop.Str("quest");

            if (JsData.Truthy(drop["status"]))
                switch (drop.Str("status"))
                {
                    // Drop only available before the quest.
                    case "notstarted":
                        return !(player.quests.Get(quest)?.IsStarted() ?? false);

                    // Started but hasn't finished (during the quest).
                    case "started":
                        return (player.quests.Get(quest)?.IsStarted() ?? false) &&
                               !(player.quests.Get(quest)?.IsFinished() ?? false);
                }

            // Just check if quest is finished by default. Drop is only available after the quest.
            return player.quests.Get(quest)?.IsFinished() ?? false;
        }

        public bool CanChangeTarget() => Node.DateNow() - lastChangedTarget > 13_000;

        void Respawn()
        {
            if (!respawnable) return;

            Node.SetTimeout(() => respawnCallback?.Invoke(), respawnDelay);
        }

        public override JsObject Serialize() => Serialize(null);

        public JsObject Serialize(Player player)
        {
            var data = base.Serialize();

            data.Set("hitPoints", hitPoints.GetHitPoints());
            data.Set("maxHitPoints", hitPoints.GetMaxHitPoints());
            data.Set("attackRange", attackRange);

            // Include the display info in the packet if the player parameter is specified.
            if (player != null) data.Set("displayInfo", GetDisplayInfo(player));

            // Level is hidden as well so that it doesn't display.
            if (!hiddenName) data.Set("level", level);

            return data;
        }

        public override JsObject GetAttackStats() => attackStats;

        public override JsObject GetDefenseStats() => defenseStats;

        public override JsObject GetBonuses() => bonuses;

        public override double GetAccuracyLevel() => skills.Num("accuracy") ?? double.NaN;

        public override double GetStrengthLevel() => skills.Num("strength") ?? double.NaN;

        public override double GetArcheryLevel() => skills.Num("archery") ?? double.NaN;

        public override double GetDefenseLevel() => skills.Num("defense") ?? double.NaN;

        public override Modules.Hits GetDamageType()
        {
            if (freezing && Formulas.GetEffectChance()) return Modules.Hits.Freezing;
            if (burning && Formulas.GetEffectChance()) return Modules.Hits.Burning;

            return damageType;
        }

        public override bool IsMagic() => IsRanged() && (attackStats.Num("magic") ?? double.NaN) > 0;

        public override bool IsPoisonous() => poisonous;

        public void OnRespawn(Action callback) => respawnCallback = callback;

        public void OnTalk(Action<string> callback) => talkCallback = callback;

        public void OnRoaming(Action callback) => roamingCallback = callback;
    }
}
