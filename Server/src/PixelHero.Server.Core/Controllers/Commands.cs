using System.Runtime.ExceptionServices;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Info;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Controllers
{
    /// <summary>
    /// controllers/commands.ts (chat commands). The player commands are an async function in the original: an error
    /// in them is an unhandled rejection (it reaches the uncaught handler after the current job), while an error in
    /// the moderator or admin commands propagates to incoming.ts' try/catch. Missing values (blocks.shift() of an
    /// empty list) are null and parse to NaN like undefined does.
    /// </summary>
    public sealed class Commands
    {
        readonly Player player;
        readonly World world;
        readonly Entities entities;

        public Commands(Player player)
        {
            this.player = player;

            world = player.world;
            entities = world.entities;
        }

        public void Parse(string rawText)
        {
            var blocks = new List<string>(rawText.Substring(1).Split(' '));

            var command = Shift(blocks);

            try
            {
                HandlePlayerCommands(command, blocks);
            }
            catch (Exception error)
            {
                // The async function's promise rejects and nothing handles it.
                Node.QueueMicrotask(() => ExceptionDispatchInfo.Throw(error));
            }

            HandleModeratorCommands(command, blocks);
            HandleAdminCommands(command, blocks);
        }

        /// <summary>Array.prototype.shift: the first element, or undefined (null) when the list is empty.</summary>
        static string Shift(List<string> blocks)
        {
            if (blocks.Count == 0) return null;

            var first = blocks[0];
            blocks.RemoveAt(0);
            return first;
        }

        static string Join(List<string> blocks) => string.Join(" ", blocks);

        static double ParseInt(string text) => JsString.ParseInt(text);

        /// <summary>`!value` for a parsed number: true for 0 and NaN.</summary>
        static bool Falsy(double value) => value == 0 || double.IsNaN(value);

        static string Str(double value) => JsString.Of(value);

        /// <summary>
        /// A TypeScript numeric enum indexed by a string: a member name gives its value, a value's digits give the
        /// member name (a string), anything else is undefined (null).
        /// </summary>
        static object EnumLookup<T>(string key) where T : struct, Enum
        {
            if (key == null) return null;

            foreach (var name in Enum.GetNames(typeof(T)))
                if (name == key) return (double)Convert.ToInt32(Enum.Parse<T>(name));

            foreach (T value in Enum.GetValues(typeof(T)))
                if (Str(Convert.ToInt32(value)) == key) return value.ToString();

            return null;
        }

        static string Capitalize(string key) => JsString.ToUpperCase(key.Substring(0, 1)) + key.Substring(1);

        /// <summary>The entity as a Character, or a TypeError where the original reads a Character-only member.</summary>
        static Character AsCharacter(Entity entity, string member) =>
            entity as Character ?? throw new JsTypeError($"{member} is not a function");

        /// <summary>
        /// Commands that are accessible to all the players.
        /// </summary>
        void HandlePlayerCommands(string command, List<string> blocks)
        {
            switch (command)
            {
                case "players":
                {
                    var players = world.entities.GetPlayerUsernames();
                    var population = players.Count;
                    var singular = population == 1;

                    player.Notify(
                        $"There {(singular ? "is" : "are")} currently {population} {(singular ? "person" : "people")} online.");

                    // Show the names of the players that are online.
                    if (player.IsAdmin()) player.Notify(string.Join(", ", players));

                    return;
                }

                case "coords":
                    player.Notify($"x: {Str(player.x)} y: {Str(player.y)}");
                    return;

                case "g":
                case "gc":
                case "global":
                    player.Chat(Filter.Clean(Join(blocks)), true, false, "rgba(191, 161, 63, 1.0)");
                    return;

                case "pm":
                case "msg":
                {
                    var asterikBlocks = Join(blocks).Split('*');
                    var username = asterikBlocks.Length > 1 ? asterikBlocks[1] : null;

                    if (string.IsNullOrEmpty(username)) return;

                    var message = string.Join(" ", blocks.Skip(username.Split(' ').Length));

                    player.SendPrivateMessage(JsString.ToLowerCase(username), message);

                    break;
                }

                case "ping":
                    player.Ping();
                    break;

                case "guild":
                {
                    var subCommand = Shift(blocks);

                    if (!JsData.Truthy(player.guild))
                    {
                        player.Notify("You are not in a guild.");
                        return;
                    }

                    switch (subCommand)
                    {
                        case "kick":
                        {
                            var username = Join(blocks);

                            if (username == "")
                            {
                                player.Notify("Malformed command, expected /guild kick [username]");
                                return;
                            }

                            world.guilds.Kick(player, username);

                            player.Notify($"You have kicked {username} from your guild.");

                            break;
                        }

                        case "rank":
                        {
                            var rank = Shift(blocks);
                            var username = Join(blocks);

                            if (string.IsNullOrEmpty(rank) || username == "")
                            {
                                player.Notify("Malformed command, expected /guild rank [rank 0-6] [username]");
                                return;
                            }

                            // Prevent the player from setting the rank to landlord.
                            if (ParseInt(rank) == 7 || rank == "landlord")
                            {
                                player.Notify("You cannot set a rank to landlord.");
                                return;
                            }

                            // await getGuild(...), then await getMember(...) (an async function: one job).
                            world.guilds.GetGuild(player.guild, guild =>
                            {
                                if (guild == null)
                                {
                                    player.Notify("You are not in a guild.");
                                    return;
                                }

                                var member = Guilds.GetMember(guild, username);

                                Node.QueueMicrotask(() =>
                                {
                                    if (member == null)
                                    {
                                        player.Notify($"Could not find a member with the name: {username}.");
                                        return;
                                    }

                                    world.guilds.SetRank(guild, player, member, ParseInt(rank));

                                    player.Notify($"You have set {username}'s rank to {rank}.");
                                });
                            });

                            break;
                        }
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// Commands only accessible to moderators and administrators.
        /// </summary>
        void HandleModeratorCommands(string command, List<string> blocks)
        {
            if (!player.IsMod() && !player.IsAdmin() && !player.IsHollowAdmin()) return;

            switch (command)
            {
                case "mute":
                case "ban":
                {
                    var duration = ParseInt(Shift(blocks));
                    var targetName = JsString.ToLowerCase(Join(blocks));

                    if (Falsy(duration) || targetName == "")
                    {
                        player.Notify("Malformed command, expected /ban(mute) [duration] [username]");
                        return;
                    }

                    // Prevent banning yourself.
                    if (targetName == player.username)
                    {
                        player.Notify("You cannot ban yourself you silly. Thanks James.");
                        return;
                    }

                    var user = world.GetPlayerByName(targetName);

                    if (user == null)
                    {
                        player.Notify($"Could not find player with name: {targetName}.");
                        return;
                    }

                    // Moderators can only mute/ban people within certain limits.
                    if (player.IsMod())
                    {
                        if (command == "mute" && duration > 168) duration = 168;
                        if (command == "ban" && duration > 72) duration = 72;
                    }

                    var hours = duration;

                    // Convert hours to milliseconds.
                    duration *= 60 * 60 * 1000;

                    var timeFrame = Node.DateNow() + duration;

                    if (command == "mute")
                    {
                        user.mute = timeFrame;
                        user.Save();

                        player.Notify($"{user.username} has been muted for {Str(hours)} hours.");
                    }
                    else if (command == "ban")
                    {
                        user.ban = timeFrame;

                        user.connection.SendUTF8("ban");
                        user.connection.Close("banned");

                        player.Notify($"{user.username} has been banned for {Str(hours)} hours.");
                    }

                    return;
                }

                case "unmute":
                {
                    var uTargetName = Join(blocks);
                    var uUser = world.GetPlayerByName(uTargetName);

                    if (uTargetName == "")
                    {
                        player.Notify($"Player {uTargetName} not found.");
                        return;
                    }

                    if (uUser == null) throw new JsTypeError("Cannot set properties of undefined (setting 'mute')");

                    uUser.mute = Node.DateNow() - 3600;

                    uUser.Save();

                    player.Notify($"{uUser.username} has been unmuted.");

                    return;
                }

                case "kick":
                case "forcekick":
                {
                    var username = Join(blocks);

                    if (username == "")
                    {
                        player.Notify("Malformed command, expected /kick username");
                        return;
                    }

                    var target = world.GetPlayerByName(username);

                    if (target == null)
                    {
                        player.Notify($"Could not find player with name: {username}");
                        return;
                    }

                    target.connection.Close($"{player.username} kicked {username}", command == "forcekick");

                    break;
                }

                case "jail":
                {
                    var duration = ParseInt(Shift(blocks));
                    var username = Join(blocks);

                    if (Falsy(duration) || username == "")
                    {
                        player.Notify("Malformed command, expected /jail [duration] [username]");
                        return;
                    }

                    var target = world.GetPlayerByName(username);

                    if (target == null)
                    {
                        player.Notify($"Could not find player with name: {username}");
                        return;
                    }

                    // Limit the duration of the jail sentence for mods.
                    if (player.IsMod() && duration > 12) duration = 12;

                    var hours = duration;

                    // Convert hours to milliseconds.
                    duration *= 60 * 60 * 1000;

                    target.jail = Node.DateNow() + duration;
                    target.SendToSpawn();

                    target.Notify($"You have been jailed for {Str(hours)} hours.", "crimsonred");
                    player.Notify($"{target.username} has been jailed for {Str(hours)} hours.");

                    break;
                }

                case "unjail":
                {
                    var username = Join(blocks);

                    if (username == "")
                    {
                        player.Notify("Malformed command, expected /unjail [username]");
                        return;
                    }

                    var target = world.GetPlayerByName(username);

                    if (target == null)
                    {
                        player.Notify($"Could not find player with name: {username}");
                        return;
                    }

                    target.jail = 0;
                    target.SendToSpawn();

                    target.Notify("You have been unjailed.");
                    player.Notify($"{target.username} has been unjailed.");
                    break;
                }
            }
        }

        /// <summary>
        /// The commands only accessible to administrators.
        /// </summary>
        void HandleAdminCommands(string command, List<string> blocks)
        {
            if (!player.IsAdmin()) return;

            switch (command)
            {
                case "spawn":
                {
                    var key = Shift(blocks);
                    var count = ParseInt(Shift(blocks));

                    if (string.IsNullOrEmpty(key)) return;

                    if (Falsy(count)) count = 1;

                    var item = new Item(key, -1, -1, true, 1);

                    if (!item.exists)
                    {
                        player.Notify($"No item with key {key} exists.");
                        return;
                    }

                    item.count = count;

                    player.inventory.Add(item);

                    return;
                }

                case "take":
                {
                    var index = ParseInt(Shift(blocks));
                    var container = Shift(blocks);
                    var username = Join(blocks);

                    if (Falsy(index) || username == "")
                    {
                        player.Notify("Invalid command, usage /take [index] [container=bank/inventory] [username]");
                        return;
                    }

                    var target = world.GetPlayerByName(username);

                    if (target == null)
                    {
                        player.Notify($"Player {username} not found.");
                        return;
                    }

                    Container containerType = container == "inventory" ? target.inventory : target.bank;
                    var slot = containerType.Get(index);

                    if (!JsData.Truthy(slot.key))
                    {
                        player.Notify($"Player {username} has no item at index {Str(index)}.");
                        return;
                    }

                    containerType.Remove(index, slot.count);

                    player.Notify($"Took {Str(slot.count)}x {slot.key} from {username}.");

                    return;
                }

                case "takeitem":
                {
                    var key = Shift(blocks);
                    var count = ParseInt(Shift(blocks));
                    var container = Shift(blocks);
                    var username = Join(blocks);

                    if (string.IsNullOrEmpty(key) || username == "" || (container != "inventory" && container != "bank"))
                    {
                        player.Notify(
                            "Invalid command, usage /takeitem [key] [count] [container=bank/inventory] [username]");
                        return;
                    }

                    var target = world.GetPlayerByName(username);

                    if (target == null)
                    {
                        player.Notify($"Player {username} not found.");
                        return;
                    }

                    Container containerType = container == "inventory" ? target.inventory : target.bank;

                    containerType.RemoveItem(key, count);

                    player.Notify($"Took {Str(count)}x {key} from {username}.");

                    return;
                }

                case "copybank":
                case "copyinventory":
                {
                    var username = Join(blocks);

                    if (username == "")
                    {
                        player.Notify("Invalid command, usage /copybank [username]");
                        return;
                    }

                    var target = world.GetPlayerByName(username);

                    if (target == null)
                    {
                        player.Notify($"Player {username} is not online.");
                        return;
                    }

                    if (command == "copybank")
                    {
                        player.bank.Empty();

                        target.bank.ForEachSlot(slot => player.bank.Add(player.bank.GetItem(slot)));
                    }
                    else
                    {
                        player.inventory.Empty();

                        target.inventory.ForEachSlot(slot => player.inventory.Add(player.inventory.GetItem(slot)));
                    }

                    player.Notify($"Copied {username}'s {command} to your {command}.");

                    break;
                }

                case "drop":
                {
                    var key = Shift(blocks);
                    var count = ParseInt(Shift(blocks));

                    if (string.IsNullOrEmpty(key)) return;

                    if (Falsy(count)) count = 1;

                    world.entities.SpawnItem(key, player.x, player.y, true, count);

                    break;
                }

                case "remove":
                {
                    var key = Shift(blocks);
                    var count = ParseInt(Shift(blocks));

                    if (string.IsNullOrEmpty(key) || Falsy(count)) return;

                    player.inventory.RemoveItem(key, count);

                    return;
                }

                case "empty":
                    player.inventory.Empty();
                    return;

                case "teleport":
                {
                    var x = ParseInt(Shift(blocks));
                    var y = ParseInt(Shift(blocks));
                    var withAnimation = ParseInt(Shift(blocks));

                    if (!Falsy(x) && !Falsy(y)) player.Teleport(x, y, !Falsy(withAnimation), false, true);

                    return;
                }

                case "teletome":
                    world.GetPlayerByName(Join(blocks))?.Teleport(player.x, player.y, false, false, true);
                    return;

                case "teleto":
                {
                    var target = world.GetPlayerByName(Join(blocks));

                    if (target != null) player.Teleport(target.x, target.y, false, false, true);

                    return;
                }

                case "immortal":
                case "nohit":
                case "invincible":
                    if (player.status.Has(Modules.Effects.Invincible))
                    {
                        player.status.Remove(Modules.Effects.Invincible);

                        player.Notify("You are no longer invincible.");
                    }
                    else
                    {
                        player.status.Add(Modules.Effects.Invincible);

                        player.Notify("You are now invincible.");
                    }
                    return;

                case "mob":
                {
                    var target = Shift(blocks);

                    if (string.IsNullOrEmpty(target))
                    {
                        player.Notify("No mob specified.");
                        return;
                    }

                    entities.SpawnMob(target, player.x, player.y);

                    return;
                }

                case "allattack":
                {
                    var region = world.map.regions.Get(player.region);
                    var target = Shift(blocks);

                    if (string.IsNullOrEmpty(target))
                    {
                        player.Notify("Invalid command. Usage: /allattack [target_instance]");
                        return;
                    }

                    if (region == null)
                    {
                        player.Notify("Bro what.");
                        return;
                    }

                    var targetEntity = entities.Get(target);

                    if (targetEntity == null) return;

                    region.ForEachEntity(entity =>
                    {
                        if (!entity.IsMob() || entity.instance == target) return;

                        ((Character)entity).combat.Attack(targetEntity);
                    });

                    break;
                }

                case "pointer":
                    if (blocks.Count > 1)
                    {
                        var posX = ParseInt(Shift(blocks));
                        var posY = ParseInt(Shift(blocks));

                        if (Falsy(posX) || Falsy(posY)) return;

                        player.Pointer(new JsObject(
                            "type", (double)Opcodes.Pointer.Location,
                            "instance", player.instance,
                            "x", posX,
                            "y", posY));
                    }
                    else
                    {
                        var instance = Shift(blocks);

                        if (string.IsNullOrEmpty(instance)) return;

                        player.Pointer(new JsObject("type", (double)Opcodes.Pointer.Entity, "instance", instance), false);
                    }

                    return;

                case "teleall":
                    entities.ForEachPlayer(target => target.Teleport(player.x, player.y, false, false, true));
                    return;

                case "getregion":
                    player.Notify($"Current Region: {player.region}");
                    return;

                case "debug":
                    player.Send(new CommandPacket(new JsObject("command", "debug")));
                    return;

                case "addexp":
                case "addexperience":
                {
                    var key = Shift(blocks);
                    var x = ParseInt(Shift(blocks));

                    if (string.IsNullOrEmpty(key) || Falsy(x)) return;

                    key = Capitalize(key);

                    player.skills.Get(EnumLookup<Modules.Skills>(key))?.AddExperience(x);

                    return;
                }

                case "setlevel":
                {
                    var key = Shift(blocks);
                    var x = ParseInt(Shift(blocks));
                    var username = Join(blocks);

                    if (username == "" || string.IsNullOrEmpty(key) || Falsy(x))
                    {
                        player.Notify("Malformed command, expected /setlevel [skill] [level] [username]");
                        return;
                    }

                    var target = world.GetPlayerByName(username);

                    if (target == null)
                    {
                        player.Notify($"Player {username} is not online.");
                        return;
                    }

                    key = Capitalize(key);

                    var skill = target.skills.Get(EnumLookup<Modules.Skills>(key));

                    if (skill == null)
                    {
                        player.Notify("Invalid skill.");
                        return;
                    }

                    if (x < skill.level)
                    {
                        skill.SetExperience(0);
                        skill.AddExperience(0);
                    }
                    else skill.AddExperience(Formulas.LevelsToExperience(skill.level, x));

                    return;
                }

                case "resetskills":
                    // Skills aren't meant to go backwards so you gotta sync and stuff lmao.
                    player.skills.ForEachSkill(skill =>
                    {
                        skill.SetExperience(0);
                        skill.AddExperience(0);
                    });
                    player.skills.Sync();
                    break;

                case "max":
                    player.skills.ForEachSkill(skill =>
                    {
                        skill.SetExperience(0);
                        skill.AddExperience(696_420_969);
                    });
                    break;

                case "attackrange":
                    Log.Info(player.attackRange);
                    return;

                case "resetregions":
                    Log.Info("Resetting regions...");

                    player.regionsLoaded = new List<double>();
                    player.UpdateRegion();

                    return;

                case "clear":
                    player.inventory.ForEachSlot(slot => player.inventory.Remove(slot.index, slot.count));
                    break;

                case "timeout":
                    player.connection.Reject("timeout");
                    break;

                case "togglepvp":
                    entities.ForEachPlayer(target => target.UpdatePVP(true));
                    break;

                case "ms":
                {
                    var movementSpeed = ParseInt(Shift(blocks));

                    if (Falsy(movementSpeed))
                    {
                        player.Notify("No movement speed specified.");
                        return;
                    }

                    if (movementSpeed > 10_000) movementSpeed = 2000;

                    // Just to not break stuff.
                    if (movementSpeed < 75) movementSpeed = 75;

                    player.overrideMovementSpeed = movementSpeed;

                    break;
                }

                case "popup":
                    player.Popup("New Quest Found!", "@blue@New @darkblue@quest @green@has@red@ been discovered!");
                    break;

                case "undostage":
                {
                    var key = Shift(blocks);

                    if (string.IsNullOrEmpty(key))
                    {
                        player.Notify("No quest specified.");
                        return;
                    }

                    var quest = player.quests.Get(key);

                    if (quest == null)
                    {
                        player.Notify("Could not find quest.");
                        return;
                    }

                    quest.SetStage(quest.GetStage() - 1);

                    break;
                }

                case "resetquests":
                    player.quests.ForEachQuest(quest => quest.SetStage(0, 0, true, true));
                    break;

                case "resetquest":
                {
                    var key = Shift(blocks);

                    if (string.IsNullOrEmpty(key))
                    {
                        player.Notify("No quest specified.");
                        return;
                    }

                    var quest = player.quests.Get(key)
                        ?? throw new JsTypeError("Cannot read properties of undefined (reading 'setStage')");

                    quest.SetStage(0, 0, true, true);
                    break;
                }

                case "resetachievements":
                    player.achievements.ForEachAchievement(achievement => achievement.SetStage(0));

                    player.UpdateRegion();

                    break;

                case "movenpc":
                {
                    var instance = Shift(blocks);
                    var x = ParseInt(Shift(blocks));
                    var y = ParseInt(Shift(blocks));

                    if (string.IsNullOrEmpty(instance))
                    {
                        player.Notify("Malformed command, expected /movenpc instance x y");
                        return;
                    }

                    var entity = entities.Get(instance);

                    if (entity == null)
                    {
                        player.Notify("Entity not found.");
                        return;
                    }

                    if (entity.IsMob()) entity.SetPosition(x, y);

                    break;
                }

                case "nvn":
                {
                    // NPC vs NPC (specify two instances)
                    var instance = Shift(blocks);
                    var target = Shift(blocks);

                    if (string.IsNullOrEmpty(instance) || string.IsNullOrEmpty(target))
                    {
                        player.Notify("Malformed command, expected /nvn instance target");
                        return;
                    }

                    var entity = entities.Get(instance);
                    var targetEntity = entities.Get(target);

                    if (entity == null || targetEntity == null)
                    {
                        player.Notify("Could not find entity instances specified.");
                        return;
                    }

                    if (entity is not Character character)
                        throw new JsTypeError("Cannot read properties of undefined (reading 'attack')");

                    character.combat.Attack(targetEntity);

                    player.Notify($"{entity.name} is attacking {targetEntity.name}");

                    break;
                }

                case "kill":
                {
                    var username = Join(blocks);

                    if (username == "")
                    {
                        player.Notify("Malformed command, expected /kill username/instance");
                        return;
                    }

                    var target = world.GetPlayerByName(username);

                    if (target != null) target.Hit(target.hitPoints.GetHitPoints());

                    var targetEntity = entities.Get(username);

                    if (targetEntity != null)
                    {
                        if (targetEntity is not Character character)
                            throw new JsTypeError("Cannot read properties of undefined (reading 'getHitPoints')");

                        character.Hit(character.hitPoints.GetHitPoints());
                    }

                    break;
                }

                case "finishquest":
                {
                    var questKey = Shift(blocks);

                    if (string.IsNullOrEmpty(questKey))
                    {
                        player.Notify("Malformed command, expected /finishquest questKey");
                        return;
                    }

                    var quest = player.quests.Get(questKey);

                    if (quest != null) quest.SetStage(9999);
                    else player.Notify($"Could not find quest with key: {questKey}");

                    break;
                }

                case "finishachievement":
                {
                    var achievementKey = Shift(blocks);

                    if (string.IsNullOrEmpty(achievementKey))
                    {
                        player.Notify("Malformed command, expected /finishachievement achievementKey");
                        return;
                    }

                    var achievement = player.achievements.Get(achievementKey);

                    if (achievement != null) achievement.Finish();
                    else player.Notify($"Could not find achievement with key: {achievementKey}");

                    break;
                }

                case "finishachievements":
                    player.achievements.ForEachAchievement(achievement => achievement.Finish());
                    return;

                case "poison":
                {
                    var instance = Shift(blocks);

                    if (!string.IsNullOrEmpty(instance))
                    {
                        Log.Debug("Poisoning entity...");

                        var entity = entities.Get(instance);

                        if (entity == null)
                        {
                            player.Notify($"Could not find entity with instance: {instance}");
                            return;
                        }

                        if (!entity.IsMob() && !entity.IsPlayer()) player.Notify("That entity cannot be poisoned.");

                        // Other entities have no poison and no setPoison.
                        var character = AsCharacter(entity, "entity.setPoison");

                        if (character.poison != null)
                        {
                            character.SetPoison();
                            player.Notify("Entity has been cured of poison.");
                        }
                        else
                        {
                            character.SetPoison(0);
                            player.Notify("Entity has been poisoned.");
                        }
                    }
                    else
                    {
                        Log.Debug("Poisoning player.");

                        if (player.poison != null)
                        {
                            player.SetPoison();
                            player.Notify("Your poison has been cured!");
                        }
                        else
                        {
                            player.SetPoison(0); // 0 === Modules.PoisonType.Venom
                            player.Notify("You have been poisoned!");
                        }
                    }

                    break;
                }

                case "poisonarea":
                {
                    var region = world.map.regions.Get(player.region);

                    if (region == null) player.Notify("Bro something went badly wrong wtf.");

                    player.Notify("All entities in the region will be nuked with poison.");

                    if (region == null) throw new JsTypeError("Cannot read properties of undefined (reading 'forEachEntity')");

                    region.ForEachEntity(entity =>
                    {
                        if (!entity.IsMob() && !entity.IsPlayer()) return;

                        ((Character)entity).SetPoison(0);
                    });

                    break;
                }

                case "roam":
                {
                    var region = world.map.regions.Get(player.region);

                    if (region == null) player.Notify("Bro something went badly wrong wtf.");

                    player.Notify("All mobs in the region will now roam!");

                    if (region == null) throw new JsTypeError("Cannot read properties of undefined (reading 'forEachEntity')");

                    region.ForEachEntity(entity =>
                    {
                        if (!entity.IsMob()) return;

                        ((Mob)entity).roamingCallback?.Invoke();
                    });

                    break;
                }

                case "talk":
                {
                    var instance = Shift(blocks);

                    if (string.IsNullOrEmpty(instance))
                    {
                        player.Notify("Malformed command, expected /talk instance");
                        return;
                    }

                    var targetEntity = entities.Get(instance);

                    if (targetEntity == null)
                    {
                        player.Notify($"Could not find entity with instance: {instance}");
                        return;
                    }

                    (targetEntity as Mob)?.talkCallback?.Invoke("This is a test talking message lol");

                    break;
                }

                case "distance":
                {
                    var x = ParseInt(Shift(blocks));
                    var y = ParseInt(Shift(blocks));

                    if (Falsy(x) || Falsy(y))
                    {
                        player.Notify("Malformed command, expected /distance x y");
                        return;
                    }

                    player.Notify($"Distance: {Str(Utils.GetDistance(player.x, player.y, x, y))}");

                    break;
                }

                case "nuke":
                {
                    var all = !string.IsNullOrEmpty(Shift(blocks));

                    var region = world.map.regions.Get(player.region)
                        ?? throw new JsTypeError("Cannot read properties of undefined (reading 'forEachEntity')");

                    region.ForEachEntity(entity =>
                    {
                        if (entity is not Character character) return;
                        if (entity.instance == player.instance) return;

                        if (!all && entity.IsPlayer()) return;

                        character.deathCallback?.Invoke(player);
                    });

                    player.Notify("Congratulations, you killed everyone, are you happy with yourself?");

                    break;
                }

                case "noclip":
                    player.noclip = !player.noclip;

                    player.Notify($"Noclip: {(player.noclip ? "true" : "false")}");
                    break;

                case "addability":
                {
                    var key = Shift(blocks);

                    if (string.IsNullOrEmpty(key))
                    {
                        player.Notify("Malformed command, expected /addability key");
                        return;
                    }

                    player.abilities.Add(key, 1);
                    break;
                }

                case "setability":
                {
                    var key = Shift(blocks);
                    var x = ParseInt(Shift(blocks));

                    if (string.IsNullOrEmpty(key) || Falsy(x))
                    {
                        player.Notify("Malformed command, expected /setability key level");
                        return;
                    }

                    player.abilities.SetLevel(key, x);

                    break;
                }

                case "setquickslot":
                {
                    var key = Shift(blocks);
                    var x = ParseInt(Shift(blocks));

                    if (string.IsNullOrEmpty(key) || double.IsNaN(x))
                    {
                        player.Notify("Malformed command, expected /setquickslot key quickslot");
                        return;
                    }

                    player.abilities.SetQuickSlot(key, x);
                    break;
                }

                case "resetabilities":
                    player.abilities.Reset();
                    return;

                case "store":
                {
                    var key = Shift(blocks);

                    if (string.IsNullOrEmpty(key))
                    {
                        player.Notify("Malformed command, expected /store key");
                        return;
                    }

                    player.Send(new StorePacket(Opcodes.Store.Open, world.stores.Serialize(key)));

                    player.storeOpen = key;

                    break;
                }

                case "aoe":
                    player.Hit(600, player, 2);
                    break;

                case "bank":
                    player.canAccessContainer = true;

                    player.Send(new NPCPacket(Opcodes.NPC.Bank, player.bank.Serialize()));
                    break;

                case "openbank":
                {
                    var username = Shift(blocks);

                    if (string.IsNullOrEmpty(username))
                    {
                        player.Notify("Malformed command, expected /openbank username");
                        return;
                    }

                    var target = world.GetPlayerByName(username);

                    if (target == null)
                    {
                        player.Notify($"Could not find player: {username}");
                        return;
                    }

                    player.Send(new NPCPacket(Opcodes.NPC.Bank, target.bank.Serialize()));

                    break;
                }

                case "setrank":
                {
                    if (player.IsHollowAdmin()) return;

                    var rankText = Shift(blocks);

                    var username = Join(blocks);

                    if (username == "" || string.IsNullOrEmpty(rankText))
                    {
                        player.Notify("Malformed command, expected /setrank username rank");
                        return;
                    }

                    var target = world.GetPlayerByName(username);
                    var rank = EnumLookup<Modules.Ranks>(rankText);

                    if (target == null)
                    {
                        world.database.SetRank(username, rank is double offline ? offline : double.NaN);
                        return;
                    }

                    // isNaN(rank): only a member name gives a number.
                    if (rank is not double value)
                    {
                        player.Notify($"Invalid rank: {rankText}");
                        return;
                    }

                    target.SetRank((Modules.Ranks)(int)value);
                    target.Sync();

                    break;
                }

                case "setpet":
                {
                    var key = Shift(blocks);

                    if (string.IsNullOrEmpty(key))
                    {
                        player.Notify("Malformed command, expected /setpet key");
                        return;
                    }

                    player.SetPet(key);

                    break;
                }

                case "opencrafting":
                    world.crafting.Open(player, Modules.Skills.Crafting);
                    return;

                case "openalchemy":
                    world.crafting.Open(player, Modules.Skills.Alchemy);
                    return;

                case "opencooking":
                    world.crafting.Open(player, Modules.Skills.Cooking);
                    return;

                case "opensmithing":
                    world.crafting.Open(player, Modules.Skills.Smithing);
                    return;

                case "opensmelting":
                    world.crafting.Open(player, Modules.Skills.Smelting);
                    return;

                case "lootbag":
                {
                    var items = new List<Item>
                    {
                        new("oldonesblade", -1, -1, false, 1),
                        new("froghelm", -1, -1, false, 1),
                        new("gold", -1, -1, false, 1500)
                    };

                    world.entities.SpawnLootBag(player.x, player.y, player.username, items);
                    break;
                }

                case "ipban":
                {
                    var username = JsString.ToLowerCase(Join(blocks));

                    if (username == "")
                    {
                        Log.Info($"Malformed command, expected /{command} <username>");
                        return;
                    }

                    var target = world.GetPlayerByName(username);

                    if (target == null)
                    {
                        Log.Info($"Could not find player by name: {username}.");
                        return;
                    }

                    player.database.SetIpBan(target.connection.address, command == "ipban");

                    player.Notify($"Player {target.username} has been IP banned");

                    // Kick all players with the same IP.
                    foreach (var p in entities.GetPlayersByIp(target.connection.address)) p.connection.Reject("banned");

                    break;
                }

                case "countdown":
                {
                    var time = ParseInt(Shift(blocks));

                    if (Falsy(time))
                    {
                        player.Notify("Malformed command, expected /countdown time");
                        return;
                    }

                    player.Countdown(time);

                    break;
                }

                case "find":
                {
                    var npc = world.entities.GetNPCByKey(Join(blocks));

                    player.Notify(npc != null
                        ? $"Found NPC: {npc.name} at x: {Str(npc.x)}, y: {Str(npc.y)}"
                        : "Could not find NPC.");

                    break;
                }

                case "testitems":
                    player.bank.Empty();

                    // Add 100 of each item into the bank.
                    foreach (var key in ServerData.Items.Keys) player.bank.Add(new Item(key, -1, -1, false, 100));

                    break;

                case "collision":
                {
                    var parsedX = ParseInt(Shift(blocks));
                    var parsedY = ParseInt(Shift(blocks));
                    var x = Falsy(parsedX) ? player.x : parsedX;
                    var y = Falsy(parsedY) ? player.y : parsedY;

                    if (Falsy(x) || Falsy(y))
                    {
                        player.Notify("Malformed command, expected /collision x y");
                        return;
                    }

                    var index = world.map.CoordToIndex(x, y);

                    player.Notify($"{(world.map.IsColliding(x, y) ? "true" : "false")} - index: {Str(index)}");

                    var data = index >= 0 && index < world.map.data.Count && index == Math.Floor(index)
                        ? world.map.data[(int)index]
                        : null;

                    Log.Debug($"Data: {(data is List<object> tiles ? string.Join(",", tiles.Select(JsString.Of)) : JsString.Of(data))}");

                    break;
                }

                case "hide":
                    player.visible = !player.visible;

                    if (player.visible)
                    {
                        player.Notify("You are now visible.");

                        player.SendToRegions(new SpawnPacket(player), true);
                    }
                    else
                    {
                        player.Notify("You are now invisible.");

                        player.SendToRegions(new DespawnPacket(new JsObject("instance", player.instance)), true);
                    }

                    break;

                case "tp":
                {
                    var key = Shift(blocks);

                    (double x, double y)? location = key switch
                    {
                        "home" => (191, 166),
                        "underwater" => (119, 290),
                        "underwatervolcano" => (194, 427),
                        "kok" => (290, 357),
                        "mountain" => (440, 312),
                        "santa" => (526, 256),
                        "ice" => (608, 332),
                        "pink" => (685, 433),
                        "hell" => (1112, 787),
                        "sewer" => (1117, 709),
                        "shroom" => (992, 631),
                        "skeletonking" => (120, 794),
                        "ogrelord" => (342, 166),
                        "queenant" => (591, 820),
                        "forestdragon" => (457, 807),
                        "crystalcave" => (886, 622),
                        "piratecaptain" => (930, 752),
                        _ => null
                    };

                    if (location is var (tpX, tpY)) player.Teleport(tpX, tpY, true, false, true);

                    break;
                }

                case "toggle":
                {
                    var key = Shift(blocks);
                    Modules.Effects effect;

                    switch (key)
                    {
                        case "cold":
                        case "freeze":
                        case "freezing":
                            if (player.status.Has(Modules.Effects.Freezing))
                            {
                                player.status.Remove(Modules.Effects.Freezing);
                                return;
                            }

                            effect = Modules.Effects.Freezing;
                            break;

                        case "fire":
                        case "burn":
                        case "burning":
                            if (player.status.Has(Modules.Effects.Burning))
                            {
                                player.status.Remove(Modules.Effects.Burning);
                                return;
                            }

                            effect = Modules.Effects.Burning;
                            break;

                        case "terror":
                            if (player.status.Has(Modules.Effects.TerrorStatus))
                            {
                                player.status.Remove(Modules.Effects.TerrorStatus);
                                return;
                            }

                            effect = Modules.Effects.TerrorStatus;
                            break;

                        case "stun":
                            if (player.status.Has(Modules.Effects.Stun))
                            {
                                player.status.Remove(Modules.Effects.Stun);
                                return;
                            }

                            effect = Modules.Effects.Stun;
                            break;

                        case "hide":
                            player.Send(new CommandPacket(new JsObject("command", "hide")));
                            return;

                        default:
                            player.status.Clear();
                            return;
                    }

                    player.status.AddWithTimeout(effect, 60_000);
                    return;
                }
            }
        }
    }
}
