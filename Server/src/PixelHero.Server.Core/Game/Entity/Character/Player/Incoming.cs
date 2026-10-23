using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Controllers;
using PixelHero.Server.Database;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>
    /// game/entity/character/player/incoming.ts. Client messages are untyped JSON: fields are read with
    /// <see cref="JsAny"/> so that wrong types behave like they do in JavaScript (coercion, or a TypeError that the
    /// handler's try/catch logs).
    /// </summary>
    public sealed class Incoming
    {
        readonly World world;
        readonly Connection connection;
        readonly Entities entities;
        readonly IGameDatabase database;
        readonly Commands commands;
        readonly Player player;

        bool completedHandshake;

        public Incoming(Player player)
        {
            this.player = player;
            connection = player.connection;
            world = player.world;
            entities = world.entities;
            database = player.database;
            commands = new Commands(player);

            connection.OnMessage(data =>
            {
                var parts = JsAny.Destructure(data, 2);
                object packet = parts[0], message = parts[1];

                if (!Utils.ValidPacket(JsAny.ToNumber(packet)))
                {
                    Log.Error($"Non-existent packet received: {JsString.Of(packet ?? PixelHero.Js.Undefined.Value)} data: ");
                    Log.Error(message);
                    return;
                }

                player.connection.RefreshTimeout();

                // Prevent server from crashing due to a packet malfunction.
                try
                {
                    if (packet is not double id) return;

                    switch ((Packets)(int)id)
                    {
                        case Packets.Handshake when id % 1 == 0:
                            HandleHandshake(message);
                            return;
                        case Packets.Login when id % 1 == 0:
                            HandleLogin(message);
                            return;
                        case Packets.Ready when id % 1 == 0:
                            HandleReady(message);
                            return;
                        case Packets.List when id % 1 == 0:
                            player.UpdateEntityList();
                            return;
                        case Packets.Who when id % 1 == 0:
                            HandleWho(message);
                            return;
                        case Packets.Equipment when id % 1 == 0:
                            HandleEquipment(message);
                            return;
                        case Packets.Movement when id % 1 == 0:
                            HandleMovement(message);
                            return;
                        case Packets.Target when id % 1 == 0:
                            HandleTarget(message);
                            return;
                        case Packets.Network when id % 1 == 0:
                            HandleNetwork(message);
                            return;
                        case Packets.Chat when id % 1 == 0:
                            HandleChat(message);
                            return;
                        case Packets.Command when id % 1 == 0:
                            HandleCommand(message);
                            return;
                        case Packets.Container when id % 1 == 0:
                            HandleContainer(message);
                            return;
                        case Packets.Quest when id % 1 == 0:
                            HandleQuest(message);
                            return;
                        case Packets.Ability when id % 1 == 0:
                            HandleAbility(message);
                            return;
                        case Packets.Respawn when id % 1 == 0:
                            player.Respawn();
                            return;
                        case Packets.Trade when id % 1 == 0:
                            HandleTrade(message);
                            return;
                        case Packets.Enchant when id % 1 == 0:
                            HandleEnchant(message);
                            return;
                        case Packets.Guild when id % 1 == 0:
                            HandleGuild(message);
                            return;
                        case Packets.Warp when id % 1 == 0:
                            HandleWarp(message);
                            return;
                        case Packets.Store when id % 1 == 0:
                            HandleStore(message);
                            return;
                        case Packets.Friends when id % 1 == 0:
                            HandleFriends(message);
                            return;
                        case Packets.Focus when id % 1 == 0:
                            player.UpdateEntityPositions();
                            return;
                        case Packets.Examine when id % 1 == 0:
                            HandleExamine(message);
                            return;
                        case Packets.Crafting when id % 1 == 0:
                            HandleCrafting(message);
                            return;
                        case Packets.LootBag when id % 1 == 0:
                            HandleLootBag(message);
                            return;
                        case Packets.Pet when id % 1 == 0:
                            HandlePet(message);
                            return;
                    }
                }
                catch (Exception error)
                {
                    Log.Error(error);
                }
            });
        }

        static object Get(object data, string key) => JsAny.Get(data, key);

        void HandleHandshake(object data)
        {
            // Reject if the client is not on the right version.
            if (!(Get(data, "gVer") is string gVer && gVer == Config.Current.gver))
            {
                connection.Reject("updated");
                return;
            }

            // Used to prevent the client from sending any packets before the handshake is complete.
            completedHandshake = true;

            player.connection.Send(new List<object>
            {
                new HandshakePacket(new JsObject(
                    "type", "client",
                    "instance", player.instance,
                    "serverId", Config.Current.serverId,
                    "serverTime", (double)PixelHero.Js.JsValue.ToInt32(Node.PerformanceNow()))).Serialize()
            });
        }

        void HandleLogin(object data)
        {
            // Ensure the handshake has been completed before proceeding.
            if (!completedHandshake)
            {
                connection.Reject("lost");
                return;
            }

            var opcode = Get(data, "opcode");
            var username = Get(data, "username");
            var password = Get(data, "password");
            var email = Get(data, "email");

            if (JsAny.Truthy(username))
            {
                var name = username as string ?? throw new JsTypeError("username.toLowerCase is not a function");

                // Format username by making it all lower case, shorter than 32 characters, and no spaces.
                var lower = JsString.ToLowerCase(name);
                player.username = Filter.Clean(JsString.Trim(lower.Length > 32 ? lower.Substring(0, 32) : lower));

                // Verify that the password fulfills the requirements.
                if (!IsValidPassword(password))
                {
                    connection.Reject("invalidpassword");
                    return;
                }

                if (JsAny.Truthy(password))
                {
                    var text = (string)password;
                    player.password = text.Length > 64 ? text.Substring(0, 64) : text;
                }

                if (JsAny.Truthy(email)) player.email = email as string ?? JsString.Of(email);

                // Reject connection if player is already logged in.
                if (world.IsOnline(player.username))
                {
                    connection.Reject("loggedin");
                    return;
                }

                // Proceed directly to login with default player data if skip database is present.
                if (Config.Current.skipDatabase)
                {
                    player.Load(Creator.Serialize(player));
                    return;
                }
            }

            // Handle login for each particular case.
            if (JsAny.Is(opcode, Opcodes.Login.Login))
            {
                // Check the player in other servers first (defaults to false if hub is not present).
                world.api.IsPlayerOnline(player.username, online =>
                {
                    if (online)
                    {
                        connection.Reject("loggedin");
                        return;
                    }

                    database.Login(player);
                });
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Login.Register))
            {
                if (Config.Current.disableRegister)
                {
                    connection.Reject("disabledregister");
                    return;
                }

                database.Register(player);
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Login.Guest))
            {
                // Authenticated so that we send the logout packet to the hub.
                player.authenticated = true;
                player.isGuest = true; // Makes sure player doesn't get saved to database.
                player.username = $"guest{JsString.Of(Utils.counter++)}"; // Generate a random guest username.
                player.Load(Creator.Serialize(player));
            }
        }

        /// <summary>Utils.isValidPassword(password) with password = '' as the default for undefined.</summary>
        static bool IsValidPassword(object password)
        {
            if (password == null) return Utils.IsValidPassword("");
            var length = JsAny.ToNumber(Get(password, "length"));
            return length >= 3 && length <= 64;
        }

        void HandleReady(object data)
        {
            var regionsLoaded = Get(data, "regionsLoaded");
            var userAgent = Get(data, "userAgent");

            player.HandleUserAgent(userAgent as string,
                regionsLoaded == null ? 0 : regionsLoaded is double d ? d : double.NaN);

            player.handler.StartUpdateInterval();

            player.ready = true;

            player.UpdateRegion();
            player.UpdateEntities();
            player.UpdateEntityList();

            world.SyncFriendsList(player.username);
            world.SyncGuildMembers(player.guild, player.username);

            world.discord.SendMessage(player.username, "has logged in!");

            // Synchronize friends list cross-server by sending inactive friends to hub.
            player.friends.Sync();

            if (player.IsDead()) player.deathCallback?.Invoke(null);

            player.Welcome();

            // A secondary check after the player has fully loaded in.
            world.api.IsPlayerOnline(player.username, online =>
            {
                if (online) player.connection.Reject("loggedin");
            });
        }

        void HandleWho(object message)
        {
            if (message is not List<object> list)
            {
                if (message is string s) list = s.Select(c => (object)c.ToString()).ToList();
                else throw new JsTypeError("message is not iterable");
            }

            foreach (var item in list)
            {
                var entity = entities.Get(JsAny.Key(item));

                if (entity == null || entity.dead) continue;

                /* We handle player-specific entity statuses here. */
                player.Send(new SpawnPacket(entity, entity.HasDisplayInfo(player) ? player : null));
            }
        }

        void HandleEquipment(object data)
        {
            var opcode = Get(data, "opcode");

            if (JsAny.Is(opcode, Opcodes.Equipment.Unequip))
            {
                player.equipment.Unequip(Get(data, "type"));
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Equipment.Style)) player.equipment.UpdateAttackStyle(Get(data, "style"));
        }

        void HandleMovement(object data)
        {
            var opcode = Get(data, "opcode");
            var requestX = Get(data, "requestX");
            var requestY = Get(data, "requestY");
            var playerX = Get(data, "playerX");
            var playerY = Get(data, "playerY");
            var nextGridX = Get(data, "nextGridX");
            var nextGridY = Get(data, "nextGridY");
            var movementSpeed = Get(data, "movementSpeed");
            var targetInstance = Get(data, "targetInstance");
            var orientation = Get(data, "orientation");
            var timestamp = Get(data, "timestamp");

            if (player.IsDead()) return;

            // Sanitize position information to prevent cheating.
            if (JsAny.Truthy(requestX)) requestX = Utils.SanitizeNumber(JsAny.ToNumber(requestX));
            if (JsAny.Truthy(requestY)) requestY = Utils.SanitizeNumber(JsAny.ToNumber(requestY));
            if (JsAny.Truthy(playerX)) playerX = Utils.SanitizeNumber(JsAny.ToNumber(playerX));
            if (JsAny.Truthy(playerY)) playerY = Utils.SanitizeNumber(JsAny.ToNumber(playerY));
            if (JsAny.Truthy(nextGridX)) nextGridX = Utils.SanitizeNumber(JsAny.ToNumber(nextGridX));
            if (JsAny.Truthy(nextGridY)) nextGridY = Utils.SanitizeNumber(JsAny.ToNumber(nextGridY));

            // Prevent any crazy packet tampering.
            if (world.map.IsOutOfBounds(JsAny.ToNumber(requestX), JsAny.ToNumber(requestY))) return;
            if (world.map.IsOutOfBounds(JsAny.ToNumber(playerX), JsAny.ToNumber(playerY))) return;

            double px = JsAny.ToNumber(playerX), py = JsAny.ToNumber(playerY);

            if (JsAny.Is(opcode, Opcodes.Movement.Request))
            {
                player.HandleMovementRequest(px, py, targetInstance as string);
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Movement.Started))
            {
                player.HandleMovementStarted(px, py, movementSpeed, targetInstance as string);
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Movement.Step))
            {
                player.HandleMovementStep(px, py, JsAny.ToNumber(nextGridX), JsAny.ToNumber(nextGridY),
                    timestamp == null ? null : JsAny.ToNumber(timestamp));
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Movement.Stop))
            {
                player.HandleMovementStop(px, py, targetInstance as string, JsAny.ToNumber(orientation));
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Movement.Entity))
            {
                var entity = entities.Get(JsAny.Key(targetInstance));

                if (entity == null) return;

                double rx = JsAny.ToNumber(requestX), ry = JsAny.ToNumber(requestY);

                // Pet movement is only handled by the owner.
                if (entity is Pet pet && pet.owner?.instance == player.instance)
                {
                    pet.SetPosition(rx, ry);
                    return;
                }

                // Skip players or invalid entities.
                if (entity is not Mob mob || mob.IsStunned() || mob.IsDead()) return;

                // Do not update if it's the same value.
                if (mob.x == rx && mob.y == ry) return;

                // Prevent crazy position updates, add some leeway to the roaming distance.
                if (mob.OutsideRoaming(rx, ry, mob.roamDistance * 2)) return;

                // For mobs update the position without a packet.
                mob.SetPosition(rx, ry, false);
            }
        }

        void HandleTarget(object message)
        {
            var parts = JsAny.Destructure(message, 4);
            object opcode = parts[0], instance = parts[1], x = parts[2], y = parts[3];

            if (JsAny.Is(opcode, Opcodes.Target.Talk))
            {
                var entity = entities.Get(JsAny.Key(instance));

                if (entity == null || !player.IsAdjacent(entity)) return;

                player.cheatScore = 0;

                if (entity.IsChest())
                {
                    ((Chest)entity).OpenChest(player);
                    return;
                }

                if (entity.dead) return;

                player.npcTalkCallback?.Invoke(entity as NPC ??
                                               throw new JsTypeError("npc is not an NPC"));
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Target.Attack))
            {
                player.HandleTargetAttack(instance as string, x == null ? null : JsAny.ToNumber(x),
                    y == null ? null : JsAny.ToNumber(y));
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Target.Object)) player.HandleObjectInteraction(instance as string ??
                throw new JsTypeError("instance.split is not a function"));
        }

        void HandleNetwork(object message)
        {
            var opcode = JsAny.Destructure(message, 1)[0];

            if (JsAny.Is(opcode, Opcodes.Network.Pong))
            {
                if (!player.requestedPing) return;

                var time = Node.DateNow();

                player.Notify($"Latency of {JsString.Of(time - player.pingTime)}ms", "red");

                player.requestedPing = false;
            }
        }

        void HandleChat(object message)
        {
            // Message sanitization.
            var text = Sanitizer.Escape(Sanitizer.Sanitize(JsAny.Get(message, 0)));

            if (string.IsNullOrEmpty(text) || !text.Any(c => !JsString.IsWhiteSpace(c))) return;

            // Handle commands if the prefix is / or ;
            if (text.StartsWith('/') || text.StartsWith(';'))
            {
                commands.Parse(text);
                return;
            }

            // Check for mute before filtering the message.
            if (player.IsMuted())
            {
                player.Notify("misc:MUTED", "crimson");
                return;
            }

            player.Chat(Filter.Clean(text));
        }

        void HandleCommand(object message)
        {
            var parts = JsAny.Destructure(message, 2);
            object opcode = parts[0], position = parts[1];

            if (!player.IsAdmin() && !player.IsHollowAdmin()) return;

            if (JsAny.Is(opcode, Opcodes.Command.CtrlClick))
                player.Teleport(JsAny.ToNumber(Get(position, "gridX")), JsAny.ToNumber(Get(position, "gridY")), true, false, true);
        }

        void HandleContainer(object packet)
        {
            var packetObject = packet as JsObject;

            // Sanitize the incoming packet information.
            if (JsAny.Truthy(Get(packet, "value")))
                packetObject?.Set("value", Utils.SanitizeNumber(JsAny.ToNumber(Get(packet, "value"))));
            if (JsAny.Truthy(Get(packet, "fromIndex")))
                packetObject?.Set("fromIndex", Utils.SanitizeNumber(JsAny.ToNumber(Get(packet, "fromIndex"))));

            var opcode = Get(packet, "opcode");
            var type = (Modules.ContainerType)(int)JsAny.ToNumber(Get(packet, "type") ?? double.NaN);
            var fromIndex = Get(packet, "fromIndex");
            var value = Get(packet, "value");

            if (JsAny.Is(opcode, Opcodes.Container.Select))
            {
                // Ensure the packet has a valid index.
                if (JsAny.IsNaN(fromIndex) || JsAny.Is(fromIndex, -1)) return;

                player.HandleContainerSelect(
                    TypeOf(Get(packet, "type")),
                    TypeOf(Get(packet, "fromContainer")),
                    JsAny.ToNumber(fromIndex),
                    TypeOf(Get(packet, "toContainer")),
                    value == null ? null : JsAny.ToNumber(value));
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Container.Remove))
            {
                player.HandleContainerRemove(TypeOf(Get(packet, "type")), JsAny.ToNumber(fromIndex), JsAny.ToNumber(value));
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Container.Swap))
                player.HandleContainerSwap(TypeOf(Get(packet, "type")), JsAny.ToNumber(fromIndex), JsAny.ToNumber(value));
        }

        /// <summary>A container type from the client: matched with === against the enum values.</summary>
        static Modules.ContainerType TypeOf(object value) =>
            value is double d && d % 1 == 0 && d >= int.MinValue && d <= int.MaxValue
                ? (Modules.ContainerType)(int)d
                : (Modules.ContainerType)(-1);

        void HandleQuest(object packet)
        {
            var key = Get(packet, "key");
            var quest = player.quests.Get(JsAny.Key(key));

            if (quest == null)
            {
                Log.Warning($"Quest {JsString.Of(key ?? PixelHero.Js.Undefined.Value)} does not exist.");
                return;
            }

            quest.HandlePrompt();
        }

        void HandleAbility(object packet)
        {
            var index = Get(packet, "index");

            // Sanitize the incoming index information.
            if (JsAny.Truthy(index)) index = Utils.SanitizeNumber(JsAny.ToNumber(index));

            var opcode = Get(packet, "opcode");

            if (JsAny.Is(opcode, Opcodes.Ability.Use))
            {
                player.abilities.Use(Get(packet, "key") as string);
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Ability.QuickSlot))
                player.abilities.SetQuickSlot(Get(packet, "key") as string, JsAny.ToNumber(index));
        }

        void HandleTrade(object packet)
        {
            var opcode = Get(packet, "opcode");

            if (JsAny.Is(opcode, Opcodes.Trade.Request))
            {
                var oPlayer = entities.Get(JsAny.Key(Get(packet, "instance")));

                if (oPlayer is not Player other) return;

                player.trade.Request(other);
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Trade.Accept))
            {
                player.trade.Accept();
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Trade.Close))
            {
                player.trade.Close();
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Trade.Add))
            {
                player.trade.Add(JsAny.ToNumber(Get(packet, "index")),
                    Utils.SanitizeNumber(JsAny.ToNumber(Get(packet, "count")), true));
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Trade.Remove)) player.trade.Remove(JsAny.ToNumber(Get(packet, "index")));
        }

        void HandleEnchant(object packet)
        {
            if (!player.canAccessContainer)
            {
                player.Notify("misc:CANNOT_DO_THAT");
                return;
            }

            var index = Get(packet, "index");
            var shardIndex = Get(packet, "shardIndex");

            // Sanitize the index and the shard index.
            if (JsAny.Truthy(index)) index = Utils.SanitizeNumber(JsAny.ToNumber(index));
            if (JsAny.Truthy(shardIndex)) shardIndex = Utils.SanitizeNumber(JsAny.ToNumber(shardIndex));

            var opcode = Get(packet, "opcode");

            if (JsAny.Is(opcode, Opcodes.Enchant.Select))
            {
                world.enchanter.Select(player, JsAny.ToNumber(index));
                return;
            }

            if (JsAny.Is(opcode, Opcodes.Enchant.Confirm))
                world.enchanter.Enchant(player, JsAny.ToNumber(index), shardIndex == null ? null : JsAny.ToNumber(shardIndex));
        }

        void HandleGuild(object packet)
        {
            var opcode = Get(packet, "opcode");

            if (JsAny.Is(opcode, Opcodes.Guild.Create))
                world.guilds.Create(player, Get(packet, "name") as string, Get(packet, "colour"), Get(packet, "outline"),
                    Get(packet, "outlineColour"), Get(packet, "crest"));
            else if (JsAny.Is(opcode, Opcodes.Guild.Join)) world.guilds.Join(player, Get(packet, "identifier") as string);
            else if (JsAny.Is(opcode, Opcodes.Guild.Leave)) world.guilds.Leave(player);
            else if (JsAny.Is(opcode, Opcodes.Guild.List))
                world.guilds.Get(player, JsAny.ToNumber(Get(packet, "from")), JsAny.ToNumber(Get(packet, "to")));
            else if (JsAny.Is(opcode, Opcodes.Guild.Chat)) world.guilds.Chat(player, Get(packet, "message") as string);
            else if (JsAny.Is(opcode, Opcodes.Guild.Promote)) world.guilds.Promote(player, Get(packet, "username") as string);
            else if (JsAny.Is(opcode, Opcodes.Guild.Demote)) world.guilds.Demote(player, Get(packet, "username") as string);
            else if (JsAny.Is(opcode, Opcodes.Guild.Kick)) world.guilds.Kick(player, Get(packet, "username") as string);
        }

        void HandleWarp(object data) => world.warps.Warp(player, Get(data, "id"));

        void HandleStore(object data)
        {
            var obj = data as JsObject;

            // Ignore invalid packets.
            if (JsAny.ToNumber(Get(data, "index")) < 0) return;

            // Sanitize the packet information to prevent any funny business.
            var count = Utils.SanitizeNumber(JsAny.ToNumber(Get(data, "count")), true);
            var index = Utils.SanitizeNumber(JsAny.ToNumber(Get(data, "index")), true);

            // Make sure the count is at least 1.
            if (count < 1) count = 1;

            obj?.Set("count", count);
            obj?.Set("index", index);

            var opcode = Get(data, "opcode");
            var key = Get(data, "key") as string;

            if (JsAny.Is(opcode, Opcodes.Store.Buy)) world.stores.Purchase(player, key, index, count);
            else if (JsAny.Is(opcode, Opcodes.Store.Sell)) world.stores.Sell(player, key, index, count);
            else if (JsAny.Is(opcode, Opcodes.Store.Select)) world.stores.Select(player, key, index, count);
        }

        void HandleFriends(object data)
        {
            var opcode = Get(data, "opcode");

            if (JsAny.Is(opcode, Opcodes.Friends.Add)) player.friends.Add(Get(data, "username") as string);
            else if (JsAny.Is(opcode, Opcodes.Friends.Remove)) player.friends.Remove(Get(data, "username") as string);
        }

        void HandleExamine(object instance)
        {
            // The client sends [instance]; entities[array] uses the array's string.
            var entity = entities.Get(JsAny.Key(instance));

            if (entity == null) return;

            if (!entity.IsMob() && !entity.IsItem()) return;

            player.statistics.AddMobExamine(entity.key);

            // `entity.description`: a mob's description, or an item's.
            var description = entity is Mob mob ? mob.description : ((Item)entity).description;

            if (!JsAny.Truthy(description))
            {
                player.Notify("misc:NO_IDEA");
                return;
            }

            player.Notify(entity is Mob m ? m.GetDescription() : ((Item)entity).GetDescription());
        }

        void HandleCrafting(object data)
        {
            // Ensure the player is not maliciously trying to craft something.
            if (player.activeCraftingInterface == -1)
            {
                player.Notify("misc:CANNOT_DO_THAT");
                return;
            }

            var count = Get(data, "count");

            // Sanitize the packet information to prevent any funny business.
            if (JsAny.Truthy(count)) count = Utils.SanitizeNumber(JsAny.ToNumber(count), true);

            var opcode = Get(data, "opcode");

            if (JsAny.Is(opcode, Opcodes.Crafting.Select)) world.crafting.Select(player, Get(data, "key") as string);
            else if (JsAny.Is(opcode, Opcodes.Crafting.Craft))
                world.crafting.Craft(player, Get(data, "key") as string, JsAny.ToNumber(count));
        }

        void HandleLootBag(object data)
        {
            if (JsAny.Is(Get(data, "opcode"), Opcodes.LootBag.Take))
            {
                if (world.entities.Get(player.activeLootBag) is not LootBag lootBag) return;

                lootBag.Take(player, JsAny.ToNumber(Get(data, "index")));
            }
        }

        void HandlePet(object data)
        {
            if (JsAny.Is(Get(data, "opcode"), Opcodes.Pet.Pickup))
            {
                if (player.pickingUpPet || !player.HasPet()) return;

                player.pickingUpPet = true;
                player.pickingUpPet = !player.RemovePet();
            }
        }
    }
}
