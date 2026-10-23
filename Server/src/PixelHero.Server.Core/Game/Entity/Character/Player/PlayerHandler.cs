using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>game/entity/character/player/handler.ts.</summary>
    public sealed class PlayerHandler
    {
        readonly World world;
        readonly Map map;
        readonly Player player;

        readonly double updateTime = 600;
        double updateTicks;
        JsTimeout updateInterval;

        public PlayerHandler(Player player)
        {
            this.player = player;
            world = player.world;
            map = player.world.map;

            // Death callback
            player.OnDeath(HandleDeath);

            // Hit callback
            player.OnHit(HandleHit);
            player.combat.OnAttack(HandleAttack);

            // Movement-related callbacks
            player.OnDoor(HandleDoor);
            player.OnMovement(HandleMovement);

            // Region callback
            player.OnRegion(HandleRegion);
            player.OnRecentRegions(HandleRecentRegions);

            // Loading callbacks
            player.equipment.OnLoaded(HandleEquipment);
            player.inventory.OnLoaded(HandleInventory);
            player.bank.OnLoaded(HandleBank);
            player.quests.OnLoaded(HandleQuests);
            player.achievements.OnLoaded(HandleAchievements);
            player.skills.OnLoaded(HandleSkills);
            player.abilities.OnLoaded(HandleAbilities);

            // Inventory callbacks
            player.inventory.OnAdd(HandleInventoryAdd);
            player.inventory.OnRemove(HandleInventoryRemove);
            player.inventory.OnNotify(message => player.Notify(message));

            // Bank callbacks
            player.bank.OnAdd(HandleBankAdd);
            player.bank.OnRemove((slot, _, _, _, _) => HandleBankRemove(slot));
            player.bank.OnNotify(message => player.Notify(message));

            // Friends list callback
            player.friends.OnLoad(HandleFriends);
            player.friends.OnAdd(HandleFriendsAdd);
            player.friends.OnRemove(HandleFriendsRemove);
            player.friends.OnStatus(HandleFriendsStatus);

            // Equipment callbacks
            player.equipment.OnEquip(HandleEquip);
            player.equipment.OnUnequip(HandleUnequip);
            player.equipment.OnAttackStyle(HandleAttackStyle);

            // Ability callbacks
            player.abilities.OnAdd(HandleAbilityAdd);
            player.abilities.OnToggle(HandleAbilityToggle);

            // Trade callbacks
            player.trade.OnOpen(HandleTradeOpen);
            player.trade.OnAdd(HandleTradeAdd);
            player.trade.OnRemove(HandleTradeRemove);
            player.trade.OnAccept(HandleTradeAccept);

            // NPC talking callback
            player.OnTalkToNPC(HandleTalkToNPC);

            // Killing a character callback
            player.OnKill(HandleKill);

            // Poison callback
            player.OnPoison(HandlePoison);

            // Cheat-score callback
            player.OnCheatScore(HandleCheatScore);

            // Mana callback
            player.mana.OnMana(HandleMana);
        }

        public void StartUpdateInterval()
        {
            updateInterval = Node.SetInterval(() =>
            {
                if (IsTickInterval(4)) DetectAggro();

                if (IsTickInterval(32))
                {
                    player.Loiter();
                    player.cheatScore = 0;
                }

                updateTicks++;
            }, updateTime);
        }

        void HandleDeath(Character attacker)
        {
            player.dead = true;

            player.status.Clear();

            if (attacker != null)
            {
                attacker.ClearTarget();
                attacker.RemoveAttacker(player);

                if (attacker.IsPlayer())
                {
                    player.statistics.pvpDeaths++;

                    // Signal to attacker they just killed a player.
                    ((Player)attacker).killCallback?.Invoke(player);
                }
            }

            // Send despawn packet to all the nearby entities except the player.
            player.SendToRegions(new DespawnPacket(new JsObject("instance", player.instance)), true);

            // Despawn the pet from the world.
            if (player.HasPet()) world.entities.RemovePet(player.pet);

            // Clear the player's target.
            player.damageTable = new JsObject<double?>();

            // Remove the poison status.
            player.SetPoison();

            world.CleanCombat(player);

            player.skills.Stop();
            player.combat.Stop();

            player.Save();

            // Send death packet only to the player.
            player.Send(new DeathPacket(player.instance));
        }

        void HandleHit(double damage, Character attacker, bool isThorns)
        {
            if (attacker == null || player.IsDead()) return;

            player.AddAttacker(attacker);

            // Prevent endless loops of thorn damage.
            if (isThorns) return;

            var thornsLevel = player.equipment.GetChestplate().GetThornsLevel();

            // Stop if we do not have thorns on the armour.
            if (thornsLevel == 0 || double.IsNaN(thornsLevel)) return;

            // 40% chance to activate thorns.
            if (Utils.RandomInt(0, 100) > 40) return;

            // Thorns damage is 10% per level of thorns enchantment.
            var thornsDamage = Math.Floor(damage * thornsLevel * 0.1);

            // Send damage packet to the attacker.
            attacker.Hit(thornsDamage, player);
        }

        void HandleAttack()
        {
            if (player.IsMagic())
            {
                var manaCost = player.equipment.GetWeapon().manaCost;

                // If the player doesn't have enough mana to attack.
                if (!player.HasManaForAttack())
                {
                    // Warn the player once if they don't have enough mana.
                    if (!player.displayedManaWarning) player.Notify("misc:LOW_MANA");

                    player.displayedManaWarning = true;

                    return;
                }

                player.displayedManaWarning = false;

                player.mana.Decrement(manaCost);
            }

            if (player.IsArcher())
            {
                if (!player.HasArrows())
                {
                    player.Notify("misc:NO_ARROWS");
                    return;
                }

                player.equipment.DecrementArrows();
            }
        }

        void HandleDoor(JsObject door)
        {
            // Reset talking index when passing through any door.
            player.talkIndex = 0;

            var doorLevel = door.Num("level") ?? double.NaN;

            if (JsData.Truthy(door["level"]))
            {
                var level = JsData.Truthy(door["skill"])
                    ? player.skills.Get(Utils.GetSkill(door.Str("skill"))).level
                    : player.level;
                var message = JsData.Truthy(door["skill"])
                    ? $"misc:NO_SKILL_DOOR;skill={door.Str("skill")};level={JsString.Of(doorLevel)}"
                    : $"misc:NO_COMBAT_DOOR;level={JsString.Of(doorLevel)}";

                if (level < doorLevel)
                {
                    player.Notify(message);
                    return;
                }
            }

            // If a door has a quest, redirect to the quest handler's door callback.
            if (JsData.Truthy(door["quest"]))
            {
                var quest = player.quests.Get(door.Str("quest")) ??
                            throw new JsTypeError("Cannot read properties of undefined (reading 'doorCallback')");

                quest.doorCallback?.Invoke(door, player);
                return;
            }

            // If the door has an achievement associated with it, it gets completed here.
            if (JsData.Truthy(door["achievement"])) player.achievements.Get(door.Str("achievement"))?.Finish();

            // Some doors may require players to complete achievements before they can pass through.
            if (JsData.Truthy(door["reqAchievement"]))
            {
                var achievement = player.achievements.Get(door.Str("reqAchievement"));

                if (!(achievement?.IsFinished() ?? false))
                {
                    player.Notify($"misc:NO_ACHIEVEMENT_DOOR;achievement={JsString.Of((object)achievement?.name ?? PixelHero.Js.Undefined.Value)}");
                    return;
                }
            }

            // Ensure quest requirement is fullfilled before passing through the door.
            if (JsData.Truthy(door["reqQuest"]))
            {
                var quest = player.quests.Get(door.Str("reqQuest"));

                if (!(quest?.IsFinished() ?? false))
                {
                    player.Notify($"misc:NO_QUEST_DOOR;quest={JsString.Of((object)quest?.name ?? PixelHero.Js.Undefined.Value)}");
                    return;
                }
            }

            // Handle door requiring an item to proceed (and remove the item from the player's inventory).
            if (JsData.Truthy(door["reqItem"]))
            {
                var count = JsData.Truthy(door["reqItemCount"]) ? (double)door["reqItemCount"] : 1;

                if (!player.inventory.HasItem(door.Str("reqItem"), count))
                {
                    player.Notify("misc:NO_KEY_DOOR");
                    return;
                }

                player.inventory.RemoveItem(door.Str("reqItem"), count);

                player.Notify("misc:DOOR_KEY_CRUMBLES");
            }

            player.Teleport(door.Num("x") ?? double.NaN, door.Num("y") ?? double.NaN);

            Log.Debug($"[{player.username}] Going through door: {JsString.Of(door["x"])} - {JsString.Of(door["y"])}");
        }

        void HandleMovement(double x, double y)
        {
            map.regions.Handle(player);

            // Prevent out of bounds (placeholder coordinates) from being processed.
            if (map.IsOutOfBounds(x, y)) return;

            DetectAggro();
            DetectAreas(x, y);
            DetectEffects(x, y);

            player.storeOpen = "";

            player.plateauLevel = map.GetPlateauLevel(x, y);

            // Make the pet follow the player with every movement.
            if (player.HasPet())
            {
                var distance = player.GetDistance(player.pet);

                // Send a new follow packet if the pet is too far away.
                if (distance > 2) player.pet?.Follow(player);

                // If the distance exceeds 10 tiles, we despawn and respawn the pet.
                if (distance > 10)
                {
                    world.entities.RemovePet(player.pet);

                    player.pet = world.entities.SpawnPet(player, player.pet.key);

                    player.pet?.Follow(player);
                }
            }
        }

        void HandleRegion(int region)
        {
            Log.Debug($"Player {player.username} entered region: {region}.");

            HandleLights(region);

            player.UpdateEntityList();

            player.lastRegionChange = Node.DateNow();
        }

        void HandleRecentRegions(List<int> regions) =>
            player.SendToRecentRegions(new DespawnPacket(new JsObject(
                "instance", player.instance,
                "regions", regions.ConvertAll(region => (object)(double)region))));

        void HandleEquipment() =>
            player.Send(new EquipmentPacket(Opcodes.Equipment.Batch, new JsObject("data", player.equipment.Serialize(true))));

        void HandleEquip(Equipment equipment)
        {
            player.Send(new EquipmentPacket(Opcodes.Equipment.Equip, new JsObject("data", equipment.Serialize(true))));

            // Sync to nearby players.
            player.Sync();
        }

        void HandleUnequip(Modules.Equipment type, double? count)
        {
            player.Send(new EquipmentPacket(Opcodes.Equipment.Unequip, new JsObject("type", (double)type, "count", count)));

            // Sync to nearby players.
            player.Sync();
        }

        void HandleAttackStyle(Modules.AttackStyle attackStyle) =>
            player.Send(new EquipmentPacket(Opcodes.Equipment.Style, new JsObject(
                "attackStyle", (double)attackStyle,
                "attackRange", player.attackRange)));

        void HandleAbilityAdd(Ability ability) =>
            player.Send(new AbilityPacket(Opcodes.Ability.Add, ability.Serialize(true)));

        void HandleAbilityToggle(string key) =>
            player.Send(new AbilityPacket(Opcodes.Ability.Toggle, new JsObject("key", key, "level", -1.0)));

        void HandleTradeOpen(string instance) =>
            player.Send(new TradePacket(Opcodes.Trade.Open, new JsObject("instance", instance)));

        void HandleTradeAdd(string instance, double index, double count, string key) =>
            player.Send(new TradePacket(Opcodes.Trade.Add, new JsObject("instance", instance, "index", index, "count", count, "key", key)));

        void HandleTradeRemove(string instance, double index) =>
            player.Send(new TradePacket(Opcodes.Trade.Remove, new JsObject("instance", instance, "index", index)));

        void HandleTradeAccept(string message) =>
            player.Send(new TradePacket(Opcodes.Trade.Accept, new JsObject("message", message)));

        void HandleInventory() =>
            // Send Batch packet to the client.
            player.Send(new ContainerPacket(Opcodes.Container.Batch, new JsObject(
                "type", (double)Modules.ContainerType.Inventory,
                "data", player.inventory.Serialize(true))));

        void HandleBank() =>
            // Send Batch packet to the client.
            player.Send(new ContainerPacket(Opcodes.Container.Batch, new JsObject(
                "type", (double)Modules.ContainerType.Bank,
                "data", player.bank.Serialize(true))));

        void HandleInventoryAdd(Slot slot) =>
            player.Send(new ContainerPacket(Opcodes.Container.Add, new JsObject(
                "type", (double)Modules.ContainerType.Inventory,
                "slot", new LiveJson(slot.ToJson))));

        void HandleInventoryRemove(Slot slot, string key, double count, JsObject enchantments, bool drop)
        {
            // Spawn the item in the world if drop is true, cheater accounts don't drop anything.
            if (drop && !player.IsCheater() && !player.IsHollowAdmin())
            {
                var item = new Item(key, player.x, player.y, true, count, enchantments);

                // Pets spawn an entity, and items spawn in the world.
                if (item.IsPetItem())
                {
                    if (player.HasPet())
                    {
                        player.Notify("misc:ALREADY_HAVE_PET");
                        return;
                    }

                    player.SetPet(item.pet);
                }
                else world.entities.AddItem(item);

                Log.Drop($"Player {player.username} dropped {JsString.Of(count)} {key}.");
            }

            player.Send(new ContainerPacket(Opcodes.Container.Remove, new JsObject(
                "type", (double)Modules.ContainerType.Inventory,
                "slot", slot.Serialize(true))));
        }

        void HandleQuests() => player.Send(new QuestPacket(Opcodes.Quest.Batch, player.quests?.Serialize(true)));

        void HandleAchievements() =>
            player.Send(new AchievementPacket(Opcodes.Achievement.Batch, player.achievements?.Serialize(true)));

        void HandleSkills() => player.Send(new SkillPacket(Opcodes.Skill.Batch, player.skills?.Serialize(true)));

        void HandleAbilities() => player.Send(new AbilityPacket(Opcodes.Ability.Batch, player.abilities?.Serialize(true)));

        void HandleBankAdd(Slot slot) =>
            player.Send(new ContainerPacket(Opcodes.Container.Add, new JsObject(
                "type", (double)Modules.ContainerType.Bank,
                "slot", new LiveJson(slot.ToJson))));

        void HandleBankRemove(Slot slot) =>
            player.Send(new ContainerPacket(Opcodes.Container.Remove, new JsObject(
                "type", (double)Modules.ContainerType.Bank,
                "slot", slot.Serialize(true))));

        void HandleFriends() =>
            player.Send(new FriendsPacket(Opcodes.Friends.List, new JsObject("list", player.friends?.GetFriendsList())));

        void HandleFriendsAdd(string username, bool status, double serverId) =>
            player.Send(new FriendsPacket(Opcodes.Friends.Add, new JsObject("username", username, "status", status, "serverId", serverId)));

        void HandleFriendsRemove(string username) =>
            player.Send(new FriendsPacket(Opcodes.Friends.Remove, new JsObject("username", username)));

        void HandleFriendsStatus(string username, bool status, double serverId) =>
            player.Send(new FriendsPacket(Opcodes.Friends.Status, new JsObject("username", username, "status", status, "serverId", serverId)));

        void HandleTalkToNPC(NPC npc)
        {
            // Primarily for the prevention of packet injection.
            if (!player.IsAdjacent(npc))
            {
                Log.Warning($"Player {player.username} tried to talk to NPC {npc.key} but is not adjacent.");
                return;
            }

            // Checks if the NPC has an active quest associated with it.
            var quest = player.quests.GetQuestFromNPC(npc);

            if (quest != null)
            {
                quest.talkCallback?.Invoke(npc, player);
                return;
            }

            // Checks if the NPC has an active achievement associated with it.
            var achievement = player.achievements.GetAchievementFromEntity(npc);

            if (achievement != null)
            {
                achievement.talkCallback?.Invoke(npc, player);
                return;
            }

            // NPC is a store.
            if (JsData.Truthy(npc.store))
            {
                world.stores.Open(player, npc);
                return;
            }

            // Used to toggle interaction with the containers.
            if (npc.role == "banker" || npc.role == "enchanter") player.canAccessContainer = true;

            switch (npc.role)
            {
                case "banker":
                    player.Send(new NPCPacket(Opcodes.NPC.Bank, player.bank.Serialize(true)));
                    return;

                case "enchanter":
                    player.Send(new NPCPacket(Opcodes.NPC.Enchant, new JsObject()));
                    break;
            }

            npc.Talk(player);
        }

        void HandleKill(Character character)
        {
            // Have the minigame handle the kill if present.
            if (character.IsPlayer())
            {
                if (player.InMinigame()) player.GetMinigame()?.Kill(player);

                // Incremebt the pvp kill count.
                player.statistics.pvpKills++;
            }

            // Skip if the kill is not a mob entity.
            if (character is not Mob mob) return;

            // Add the mob kill to the player's statistics.
            player.statistics.AddMobKill(mob.key);

            var mobAchievement = mob.achievement;

            if (JsData.Truthy(mobAchievement))
                (player.achievements.Get(mobAchievement) ??
                 throw new JsTypeError("Cannot read properties of undefined (reading 'finish')")).Finish();

            // Checks if the mob has a active quest associated with it.
            var quest = player.quests.GetQuestFromMob(mob);

            if (quest != null) quest.killCallback?.Invoke(mob);

            // Checks if the mob has an active achievement associated with it.
            var achievement = player.achievements.GetAchievementFromEntity(mob);

            if (achievement != null) achievement.killCallback?.Invoke(mob);
        }

        void HandlePoison(double type, bool exists)
        {
            // Notify the player when the poison status changes.
            if (type == -1) player.Notify("misc:POISONED");
            else if (exists) player.Notify("misc:POISON_WORN_OFF");

            player.Send(new PoisonPacket(type));
        }

        void HandleCheatScore()
        {
            if (player.cheatScore > 15)
            {
                player.SendToSpawn();
                player.connection.Reject("cheating");
            }

            Log.General($"[{player.username}] Cheat score: {JsString.Of(player.cheatScore)}");
        }

        void HandleMana() =>
            player.Send(new PointsPacket(new JsObject(
                "instance", player.instance,
                "mana", player.mana.GetMana(),
                "maxMana", player.mana.GetMaxMana())));

        void HandleLights(int region)
        {
            if (region < 0) return;

            map.regions.ForEachSurroundingRegion(region, regionId =>
            {
                var surrounding = map.regions.Get(regionId);

                surrounding.ForEachLight(light =>
                {
                    if (player.HasLoadedLight(light.id)) return;

                    player.Send(new OverlayPacket(Opcodes.Overlay.Lamp, new JsObject("light", light.Serialize())));

                    player.lightsLoaded.Add(light.id);
                });
            });
        }

        void DetectAreas(double x, double y)
        {
            map.ForEachAreas((areas, name) =>
            {
                var info = areas.InArea(x, y);

                switch (name)
                {
                    case "pvp":
                        player.UpdatePVP(info != null);
                        return;
                    case "overlay":
                        player.UpdateOverlay(info);
                        return;
                    case "camera":
                        player.UpdateCamera(info);
                        return;
                    case "music":
                        player.UpdateMusic(info);
                        return;
                    case "minigame":
                        player.UpdateMinigame(info);
                        return;
                }
            }, new List<string> { "pvp", "music", "overlay", "camera", "minigame" });
        }

        void DetectAggro()
        {
            var region = map.regions.Get(player.region) ??
                         throw new JsTypeError("Cannot read properties of undefined (reading 'forEachEntity')");

            region.ForEachEntity(entity =>
            {
                // Ignore non-mob entities.
                if (entity is not Mob mob) return;

                // Check if the mob can aggro the player and initiate the combat.
                if (mob.CanAggro(player)) mob.combat.Attack(player);
            });
        }

        void DetectEffects(double x, double y)
        {
            var entity = world.GetGrids().GetEffectAt(x, y);

            if (entity == null) return;

            switch (entity.key)
            {
                case "lava":
                    if (player.status.Has(Modules.Effects.Burning)) return;

                    player.status.AddWithTimeout(Modules.Effects.Burning, 15_000);
                    return;
            }
        }

        bool IsTickInterval(double interval) => updateTicks % interval == 0;

        public void Clear()
        {
            Node.ClearInterval(updateInterval);
            updateInterval = null;

            Node.ClearInterval(player.readyTimeout);
            player.readyTimeout = null;
        }
    }
}
