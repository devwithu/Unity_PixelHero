using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Info;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>game/entity/character/player/player.ts.</summary>
    public sealed partial class Player : Character
    {
        public Map map;
        Regions regions;
        Entities entities;

        public Incoming incoming;
        public Bank bank;
        public Inventory inventory;
        public Abilities abilities;
        public Quests quests;
        public Achievements achievements;
        public Skills skills;
        public Equipments equipment;
        public Mana mana;
        public Statistics statistics;
        public Friends friends;
        public Trade trade;
        public PlayerHandler handler;

        public bool ready; // indicates if login processed finished
        public bool starterItemsPending; // C# server only: set by Quests.Load, granted in Welcome
        public bool authenticated;
        public bool isGuest;
        public bool canTalk = true;
        public bool noclip;
        public bool questsLoaded;
        public bool invalidateMovement;
        public bool achievementsLoaded;
        public bool displayedManaWarning;
        public bool bypassAntiCheat;
        public bool pickingUpPet; // Used to doubly ensure the player is not spamming the pickup button.
        public bool requestedPing;
        public double overrideMovementSpeed = -1;

        // Player info
        public string username = "";
        public string password = "";
        public string email = "";
        public string userAgent = "";
        public string guild = "";
        public Modules.Ranks rank = Modules.Ranks.None;

        // Stores the last attack style for each type of weapon.
        public JsObject<double?> lastStyles = new();

        // Pet information
        public Pet pet;

        // Warps
        public double lastWarp;

        // Moderation variables
        public double ban; // epoch timestamp
        public double mute;
        public double jail;

        // Player miscellaneous data
        public double mapVersion = -1;
        public double cheatScore;
        public double movementStart;
        public double pingTime;
        public double lastCraft;
        public double lastGlobalChat;
        double lastNotify;
        double lastEdible;
        string currentSong;

        // Minigame variables
        public Area minigameArea;
        public double coursingScore; // Probably will have a dictionary for this data when we have more minigames.
        public string coursingTarget = ""; // The player we are chasing.

        // Region data
        public List<double> regionsLoaded = new();
        public List<double> lightsLoaded = new();

        // NPC talking
        public string npcTalk = "";
        public double talkIndex;

        // Anti-cheat container
        public bool canAccessContainer;
        public double activeCraftingInterface = -1; // The skill ID
        public string activeLootBag = ""; // The instance of the loot bag currently open

        // Minigame status of the player.
        public Opcodes.Minigame? minigame;
        public Team? team;

        // Currently open store of the player.
        public string storeOpen = "";

        Area cameraArea;
        public Area overlayArea;

        public JsTimeout readyTimeout;

        public Action<Character> killCallback;
        public Action<NPC> npcTalkCallback;
        public Action<JsObject> doorCallback;
        public Action<int> regionCallback;
        public Action<List<int>> recentRegionsCallback;
        Action cheatScoreCallback;

        public IGameDatabase database;
        public Connection connection;

        public Player(World world, IGameDatabase database, Connection connection)
            : base(connection.instance, world, "", -1, -1)
        {
            // Parameter properties, then the field initializers that construct objects.
            this.database = database;
            this.connection = connection;
            bank = new Bank(Constants.BANK_SIZE);
            inventory = new Inventory(Constants.INVENTORY_SIZE);

            this.connection.OnClose(HandleClose);

            map = this.world.map;
            regions = this.world.map.regions;
            entities = this.world.entities;

            incoming = new Incoming(this);
            abilities = new Abilities(this);
            quests = new Quests(this);
            achievements = new Achievements(this);
            skills = new Skills(this);
            equipment = new Equipments(this);
            mana = new Mana(Formulas.GetMaxMana(level));
            statistics = new Statistics(this);
            friends = new Friends(this);
            trade = new Trade(this);
            handler = new PlayerHandler(this);

            // Send the connected packet, begin the handshake process.
            Send(new ConnectedPacket());
        }

        /// <summary>
        /// async load(data): the code before the first await runs at once; quests and achievements resolve through
        /// promise jobs (two jobs each: the loader's promise, then the async helper's promise), then the intro.
        /// </summary>
        public void Load(JsObject data)
        {
            // The player's ban timestamp is in the future, so they are still banned.
            if ((data.Num("ban") ?? double.NaN) > Node.DateNow())
            {
                connection.Reject("banned");
                return;
            }

            // Store coords for when we're done loading.
            x = data.Num("x") ?? double.NaN;
            y = data.Num("y") ?? double.NaN;
            name = data.Str("username");
            guild = data.Str("guild");
            rank = JsData.Truthy(data["rank"]) ? (Modules.Ranks)(int)(double)data["rank"] : Modules.Ranks.None;
            ban = data.Num("ban") ?? double.NaN;
            jail = data.Num("jail") ?? double.NaN;
            mute = data.Num("mute") ?? double.NaN;
            orientation = data.Num("orientation") ?? double.NaN;
            mapVersion = data.Num("mapVersion") ?? double.NaN;
            userAgent = data.Str("userAgent");
            regionsLoaded = JsData.Truthy(data["regionsLoaded"])
                ? data.Arr("regionsLoaded").ConvertAll(region => region is double d ? d : double.NaN)
                : new List<double>();
            lastGlobalChat = JsData.Truthy(data["lastGlobalChat"]) ? (double)data["lastGlobalChat"] : 0;

            var poison = data.Obj("poison") ??
                         throw new JsTypeError("Cannot read properties of undefined (reading 'type')");
            SetPoison(poison.Num("type") ?? double.NaN, Node.DateNow() - (poison.Num("remaining") ?? double.NaN));
            SetLastWarp(data.Num("lastWarp"));

            hitPoints.UpdateHitPoints(data.Num("hitPoints") ?? double.NaN);
            mana.UpdateMana(data.Num("mana") ?? double.NaN);

            friends.Load(data.Arr("friends"));

            LoadSkills();
            LoadEquipment();
            LoadInventory();
            LoadBank();
            LoadStatistics();
            LoadAbilities();

            // Synchronize login with the hub's server list.
            world.client.Send(new PlayerPacket(Opcodes.Player.Login, new JsObject("username", username, "guild", guild)));

            // Quests and achievements have to be loaded prior to introducing the player.
            LoadQuests(() => LoadAchievements(() =>
            {
                Intro();

                // Connect the player to their guild if they are in one.
                if (JsData.Truthy(guild)) world.guilds.Connect(this, guild);

                // Spawn the pet if the player has one.
                if (JsData.Truthy(data["pet"])) SetPet(data.Str("pet"));

                // Apply the status effects from the database.
                status.Load(data.Obj("effects") ??
                            throw new JsTypeError("Cannot convert undefined or null to object"));
            }));
        }

        public void LoadEquipment() => database.LoadEquipment(this, equipment.Load);

        public void LoadInventory() => database.LoadInventory(this, inventory.Load);

        public void LoadBank() => database.LoadBank(this, bank.Load);

        /// <summary>async loadQuests(): quests.load(await loader.loadQuests(this)), then the caller's await.</summary>
        void LoadQuests(Action then) =>
            database.LoadQuests(this, quests =>
            {
                this.quests.Load(quests);
                Node.QueueMicrotask(then);
            });

        void LoadAchievements(Action then) =>
            database.LoadAchievements(this, achievements =>
            {
                this.achievements.Load(achievements);
                Node.QueueMicrotask(then);
            });

        public void LoadSkills() => database.LoadSkills(this, skills.Load);

        public void LoadStatistics() => database.LoadStatistics(this, statistics.Load);

        public void LoadAbilities() => database.LoadAbilities(this, abilities.Load);

        public void HandleClose()
        {
            // Stops the character-based intervals
            Stop();

            // Stops intervals in handler if it has been initialized
            handler?.Clear();

            // If authenticated send information to the hub and Discord.
            if (authenticated)
            {
                world.discord.SendMessage(username, "has logged out!");
                world.client.Send(new PlayerPacket(Opcodes.Player.Logout, new JsObject("username", username, "guild", guild)));
            }

            // Send data to the minigames if present...
            if (InMinigame()) GetMinigame()?.Disconnect(this);

            // Stop all trading.
            trade?.Close();

            // Stop combat.
            combat?.Stop();

            // Stop skilling
            skills?.Stop();

            // Clear the player from areas
            ClearAreas();

            minigameArea?.exitCallback?.Invoke(this);

            // Signal to other attacking entities that we have left.
            world.CleanCombat(this);

            // Synchronize friends list and guilds with the logout status
            world.SyncFriendsList(username, true);
            world.SyncGuildMembers(guild, username, true);

            // Save the player if authenticated and ready.
            if (authenticated && ready) Save();

            // Remove the player from the region.
            entities.RemovePlayer(this);

            // Despawn the pet from the world.
            if (HasPet()) world.entities.RemovePet(pet);
        }

        public void Intro()
        {
            // Reset hitpoints if they are unitialized.
            if (hitPoints.GetHitPoints() < 0) hitPoints.SetHitPoints(hitPoints.GetMaxHitPoints());

            // Reset mana if it is unitialized.
            if (mana.GetMana() < 0) mana.SetMana(mana.GetMaxMana());

            // Update the player's timeout based on their rank.
            if (rank != Modules.Ranks.None) connection.UpdateTimeout(GetTimeoutByRank());

            // JsTimeout the player if the ready packet is not received within 10 seconds.
            readyTimeout = Node.SetTimeout(() =>
            {
                if (!ready || connection.closed) connection.Reject("error");
            }, 7000);

            SetPosition(x, y);

            entities.AddPlayer(this);

            Send(new WelcomePacket(Serialize(false, true, true)));
        }

        public void Respawn()
        {
            // Cannot respawn if the player is not marked as dead.
            if (!dead)
            {
                Log.Warning("Invalid respawn request.");
                return;
            }

            dead = false;

            var spawn = GetSpawn();

            Teleport(spawn.x, spawn.y);

            // Signal to other players that the player is spawning.
            SendToRegions(new SpawnPacket(this), true);

            Send(new RespawnPacket(this));

            hitPoints.Reset();
            mana.Reset();

            Sync();
        }

        public void Welcome()
        {
            var config = Config.Current;

            if (starterItemsPending) GiveStarterItems();

            if (IsNew())
            {
                Save();
                Notify($"misc:WELCOME;name={config.name}");
                return;
            }

            Notify($"misc:WELCOME_BACK;name={config.name}");

            var population = world.GetPopulation();
            var activeEvent = world.events.activeEvent;

            if (population > 1) Notify($"misc:PEOPLE_ONLINE;population={JsString.Of(population)}", "", "", true);

            if (JsData.Truthy(activeEvent)) Notify($"The {activeEvent} event is currently active!", "crimsonred", "", true);

            if (IsJailed()) Notify($"misc:JAILED;duration={GetJailDuration()}", "crimsonred", "", true);
        }

        /// <summary>
        /// C# server only: gives a new character the STARTER_ITEMS when the tutorial is off. It runs once the client is
        /// ready, so the inventory has loaded and the client receives the added items.
        /// </summary>
        void GiveStarterItems()
        {
            starterItemsPending = false;

            var keys = Config.Current.starterItems switch
            {
                List<object> list => list.ConvertAll(JsString.Of),
                string key when key != "" => new List<string> { key },
                _ => new List<string>()
            };

            if (keys.Count == 0) return;

            foreach (var key in keys)
            {
                if (ServerData.Items[key] == null)
                {
                    Log.Warning($"[STARTER_ITEMS] Unknown item {key}.");
                    continue;
                }

                inventory.Add(new Item(key, -1, -1, false, 1));
            }

            // A new character is saved by Welcome, an older one without quest data is saved here.
            if (!IsNew()) Save();
        }

        public override void Heal(double amount = 1) => Heal(amount, "passive");

        public void Heal(double amount, string type)
        {
            switch (type)
            {
                case "passive":
                {
                    // Increment mana by 1% of the max mana.
                    if (!mana.IsFull()) mana.Increment(Math.Floor(mana.GetMaxMana() * 0.01));

                    // Base healing amount by 0.5% of the max hitpoints.
                    var healAmount = hitPoints.GetMaxHitPoints() * 0.005;

                    // Use the eating level to increase the healing amount.
                    healAmount += skills.Get(Modules.Skills.Eating).level / 10;

                    base.Heal(Math.Ceiling(healAmount));
                    break;
                }

                case "hitpoints":
                case "mana":
                {
                    if (IsCheater()) Notify("Healing is disabled for cheaters, sorry.");

                    if (type == "hitpoints") hitPoints.Increment(amount);
                    else if (type == "mana") mana.Increment(amount);

                    SendToRegions(new HealPacket(new JsObject(
                        "instance", instance,
                        "type", type,
                        "amount", amount)));
                    break;
                }
            }

            Sync();
        }

        public void Loiter()
        {
            // Skip if we have not reached the loitering threshold for the region.
            if (!quests.IsTutorialFinished() || !IsLoiteringThreshold()) return;

            var loitering = skills.Get(Modules.Skills.Loitering);

            loitering.AddExperience(loitering.level * 5);
        }

        public override void FindAdjacentTile()
        {
            if (!world.map.IsColliding(x + 1, y)) SetPosition(x + 1, y, true);
            else if (!world.map.IsColliding(x - 1, y)) SetPosition(x - 1, y, true);
            else if (!world.map.IsColliding(x, y + 1)) SetPosition(x, y + 1, true);
            else if (!world.map.IsColliding(x, y - 1)) SetPosition(x, y - 1, true);
        }

        public void UpdateRegion() => regions.SendRegion(this);

        public void UpdateEntities() => regions.SendDisplayInfo(this);

        public void UpdateEntityList() => regions.SendEntities(this);

        public void UpdateEntityPositions() => regions.SendEntityPositions(this);

        public override void Teleport(double x, double y, bool withAnimation = false) =>
            Teleport(x, y, withAnimation, false, false);

        public void Teleport(double x, double y, bool withAnimation, bool before, bool bypass = false)
        {
            if (dead) return;

            if (bypass) bypassAntiCheat = true;

            if (before) SendTeleportPacket(x, y, withAnimation);

            SetPosition(x, y, false);

            world.CleanCombat(this);

            if (before) return;

            SendTeleportPacket(x, y, withAnimation);

            bypassAntiCheat = false;
        }

        void SendTeleportPacket(double x, double y, bool withAnimation = false) =>
            SendToRegions(new TeleportPacket(new JsObject(
                "instance", instance,
                "x", x,
                "y", y,
                "withAnimation", withAnimation)));

        public void IncrementCheatScore(string reason = "", double amount = 1)
        {
            if (bypassAntiCheat) return;

            if (combat.started) return;

            if (JsData.Truthy(reason)) Log.Debug($"[{username}] {reason}");

            cheatScore += amount;

            cheatScoreCallback?.Invoke();
        }

        public void IncrementCoursingScore(double score)
        {
            coursingScore += score;

            // If the score is negative, we set it to 0.
            if (coursingScore < 0) coursingScore = 0;
        }

        bool VerifyCollision(double x, double y)
        {
            var isColliding = map.IsColliding(x, y, this) && !noclip;

            if (isColliding)
            {
                if ((oldX == -1 && oldY == -1) || (oldX == this.x && oldY == this.y))
                {
                    SendToSpawn();
                    return true;
                }

                // Increment the cheat score.
                IncrementCheatScore($"Noclip detected at {JsString.Of(x)}, {JsString.Of(y)}.");

                // Send player to the last valid position.
                Teleport(oldX, oldY);
            }

            return isColliding;
        }

        /// <summary>
        /// Port fix (ANALYSIS.md §9 #2; the harness patches the original the same way): a movement step or stop may
        /// only be on the player's tile or the next one, as the client moves one tile at a time without diagonals. A
        /// forged position raises the cheat score and puts the player back where the server has them.
        /// </summary>
        bool VerifySkip(double x, double y)
        {
            if (bypassAntiCheat || Math.Abs(x - this.x) + Math.Abs(y - this.y) <= 1) return false;

            IncrementCheatScore($"Movement skipped from {JsString.Of(this.x)}, {JsString.Of(this.y)} to {JsString.Of(x)}, {JsString.Of(y)}.");
            Teleport(this.x, this.y);

            return true;
        }

        bool VerifyMovement(double x, double y, double latency)
        {
            var now = Node.DateNow();
            var stepDiff = now - lastStep - latency; // subtract latency
            var regionDiff = now - lastRegionChange;

            // High latency may cause packets to be sent in a delayed manner, causing two to be sent/received at once.
            if (latency > 35 && stepDiff < 35) return false;

            // Firstly ensure that the last step was behaving normally, within a 5% margin of error.
            var movementSpeed = GetMovementSpeed();

            if (stepDiff > movementSpeed - JsValue.ToInt32(movementSpeed * 0.05)) return false;

            // A region change may trigger a movement anomaly, so we ignore movement for 1.5 seconds of a region change.
            if (regionDiff < 1500) return false;

            // Check if the player is currently going into a door.
            if (map.IsDoor(x, y)) return false;

            return true;
        }

        public void HandleTargetAttack(string instance, double? x = null, double? y = null)
        {
            // Prevent targetting yourself.
            if (instance == this.instance) return;

            var target = entities.Get(instance);

            // Ignore if the target is not a character that can actually be attacked.
            if (target is not Character character || target.dead) return;

            // Ensure that the player can actually attack the target.
            if (!CanAttack(character)) return;

            if (target.IsMob() && !character.combat.started && JsData.Truthy(x) && JsData.Truthy(y))
                character.SetPosition(x.Value, y.Value);

            // Clear the cheat score
            cheatScore = 0;

            // Start combat with the target.
            combat.Attack(character);
        }

        public void HandleContainerSelect(Modules.ContainerType type, Modules.ContainerType fromContainer, double fromIndex,
            Modules.ContainerType toContainer, double? toIndex = null)
        {
            switch (type)
            {
                case Modules.ContainerType.Inventory:
                {
                    var item = inventory.GetItem(inventory.Get(fromIndex));

                    if (item == null) return;

                    // Have the plugin handle the action if it exists.
                    if (item.interactable && (item.plugin?.OnUse(this) ?? false)) return;

                    // Checks if the player can eat and uses the item's plugin to handle the action.
                    if (item.edible && CanEat() && (item.plugin?.OnUse(this) ?? false))
                    {
                        inventory.Remove(fromIndex, 1);

                        lastEdible = Node.DateNow();

                        if (item.IsSmallBowl()) inventory.Add(new Item("bowlsmall", -1, -1, false, 1));
                        else if (item.IsMediumBowl()) inventory.Add(new Item("bowlmedium", -1, -1, false, 1));
                    }

                    // The item is equippable and the player has the requirements to equip it.
                    if (item.IsEquippable() && item.CanEquip(this)) equipment.Equip(item, fromIndex);

                    break;
                }

                case Modules.ContainerType.Bank:
                {
                    if (!canAccessContainer)
                    {
                        Notify("misc:CANNOT_DO_THAT");
                        return;
                    }

                    Container from = fromContainer == Modules.ContainerType.Bank ? bank : inventory;
                    Container to = toContainer == Modules.ContainerType.Bank ? bank : inventory;

                    from.Move(fromIndex, to, toIndex);

                    break;
                }
            }
        }

        public void HandleContainerRemove(Modules.ContainerType type, double index, double count)
        {
            if (count < 1 || double.IsNaN(count))
            {
                Notify("misc:INVALID_AMOUNT");
                return;
            }

            Container container = type == Modules.ContainerType.Inventory ? inventory : bank;

            if (type == Modules.ContainerType.Inventory && map.IsDoor(x, y))
            {
                Notify("misc:CANNOT_DROP_ITEM_DOOR");
                return;
            }

            container.Remove(index, count, true);
        }

        public void HandleContainerSwap(Modules.ContainerType type, double fromIndex, double toIndex)
        {
            if (double.IsNaN(fromIndex) || double.IsNaN(toIndex) || fromIndex < 0 || toIndex < 0)
            {
                Log.Warning($"[{username}] Invalid container swap [{JsString.Of(fromIndex)}, {JsString.Of(toIndex)}}}]");
                return;
            }

            // Ignore same index swaps.
            if (fromIndex == toIndex)
            {
                Log.Warning($"[{username}] Same index swap.");
                return;
            }

            Container container = type == Modules.ContainerType.Inventory ? inventory : bank;

            container.Swap(fromIndex, container, toIndex);
        }

        public void HandleObjectInteraction(string instance)
        {
            cheatScore = 0;

            var entity = entities.Get(instance);

            // Ensure that the entity is close enough to interact with.
            if (entity != null && entity.GetDistance(this) > 2) return;

            // If the entity is a tree we use the lumberjacking skill to handle the interaction.
            if (entity != null && entity.IsTree()) skills.GetLumberjacking().Cut(this, (Resource)entity);

            // If the entity is a rock we use the mining skill to handle the interaction.
            if (entity != null && entity.IsRock()) skills.GetMining().Mine(this, (Resource)entity);

            // If the entity is a fishing spot we use the fishing skill to handle the interaction.
            if (entity != null && entity.IsFishSpot()) skills.GetFishing().Catch(this, (Resource)entity);

            // If the entity is a forageable plant we use the foraging skill to handle the interaction.
            if (entity != null && entity.IsForaging()) skills.GetForaging().Harvest(this, (Resource)entity);

            // Attempt to find a sign first.
            var sign = world.globals.GetSigns().Get(instance);

            if (sign != null)
            {
                sign.Talk(this);
                return;
            }

            // If no sign was found, we attempt to find a tree.
            var coords = instance.Split('-');
            var coordX = JsString.ParseInt(coords[0]);
            var coordY = coords.Length > 1 ? JsString.ParseInt(coords[1]) : double.NaN;
            var diffX = Math.Abs(x - coordX);
            var diffY = Math.Abs(y - coordY);

            // Ensure that the player is close enough to the object.
            if (diffX > 2 || diffY > 2) return;

            var index = map.CoordToIndex(coordX, coordY);
            var cursor = map.GetCursor(index);

            if (!JsData.Truthy(cursor)) return;

            // Handle interactable objects based on the cursor.
            switch (cursor)
            {
                case "smithing":
                    world.crafting.Open(this, Modules.Skills.Smithing);
                    return;

                case "smelting":
                    world.crafting.Open(this, Modules.Skills.Smelting);
                    return;

                case "cooking":
                    world.crafting.Open(this, Modules.Skills.Cooking);
                    return;

                case "crafting":
                    if (!CanUseCrafting())
                    {
                        Notify("misc:NO_KNOWLEDGE_USE");
                        return;
                    }

                    world.crafting.Open(this, Modules.Skills.Crafting);
                    return;

                case "alchemy":
                    if (!CanUseAlchemy())
                    {
                        Notify("misc:NO_KNOWLEDGE_USE");
                        return;
                    }

                    world.crafting.Open(this, Modules.Skills.Alchemy);
                    return;
            }
        }

        public void HandleUserAgent(string userAgent, double regionsLoaded = 0)
        {
            // Client's number of regions loaded, user agent, and map version must match.
            if (this.regionsLoaded.Count == regionsLoaded && this.userAgent == userAgent && mapVersion == map.version)
                return;

            // Update user agent and map version, and reset loaded regions information.
            this.userAgent = userAgent;
            mapVersion = map.version;
            this.regionsLoaded = new List<double>();

            Log.Debug($"Reset user agent and regions loaded for {username}.");
        }

        public void HandleExperience(double damage)
        {
            // Ignore invalid damage values.
            if (damage < 1) return;

            var experience = damage * world.GetExperiencePerHit();
            var weapon = equipment.GetWeapon();

            if (!HasManaForAttack()) experience = Math.Floor(experience / 2);

            skills.Get(Modules.Skills.Health).AddExperience(Math.Ceiling(experience / 4), false);

            // Archery directs experience to the archery skill.
            if (IsArcher())
            {
                skills.Get(Modules.Skills.Archery).AddExperience(Math.Ceiling(experience * 0.75), false);
                return;
            }

            // Magic directs experience to the magic skill.
            if (IsMagic())
            {
                skills.Get(Modules.Skills.Magic).AddExperience(Math.Ceiling(experience * 0.75), false);
                return;
            }

            switch (weapon.attackStyle)
            {
                // Stab gives the remaining accuracy experience.
                case Modules.AttackStyle.Stab:
                    skills.Get(Modules.Skills.Accuracy).AddExperience(Math.Ceiling(experience * 0.75), false);
                    break;

                // Slash gives the remaining strength experience.
                case Modules.AttackStyle.Slash:
                    skills.Get(Modules.Skills.Strength).AddExperience(Math.Ceiling(experience * 0.75), false);
                    break;

                // Defense gives the remaining defence experience.
                case Modules.AttackStyle.Defensive:
                    skills.Get(Modules.Skills.Defense).AddExperience(Math.Ceiling(experience * 0.75), false);
                    break;

                // Crush gives accuracy + strength experience.
                case Modules.AttackStyle.Crush:
                    skills.Get(Modules.Skills.Accuracy).AddExperience(Math.Ceiling(experience * 0.375), false);
                    skills.Get(Modules.Skills.Strength).AddExperience(Math.Ceiling(experience * 0.375), false);
                    break;

                // Shared experience evenly distributes the experience between all skills.
                case Modules.AttackStyle.Shared:
                    skills.Get(Modules.Skills.Accuracy).AddExperience(Math.Ceiling(experience * 0.25), false);
                    skills.Get(Modules.Skills.Strength).AddExperience(Math.Ceiling(experience * 0.25), false);
                    skills.Get(Modules.Skills.Defense).AddExperience(Math.Ceiling(experience * 0.25), false);
                    break;

                // Axe hacking attack style gives strength + defence experience.
                case Modules.AttackStyle.Hack:
                    skills.Get(Modules.Skills.Strength).AddExperience(Math.Ceiling(experience * 0.375), false);
                    skills.Get(Modules.Skills.Defense).AddExperience(Math.Ceiling(experience * 0.375), false);
                    break;

                // Axe chop gives accuracy + defence experience.
                case Modules.AttackStyle.Chop:
                    skills.Get(Modules.Skills.Accuracy).AddExperience(Math.Floor(experience * 0.375), false);
                    skills.Get(Modules.Skills.Defense).AddExperience(Math.Floor(experience * 0.375), false);
                    break;

                // Default (unarmed)
                default:
                    skills.Get(Modules.Skills.Strength).AddExperience(Math.Ceiling(experience * 0.75), false);
                    break;
            }
        }

        public void HandleMovementRequest(double x, double y, string target)
        {
            // Immediately clear the target to prevent combat from sticking to previous target.
            if (target != this.target?.instance) this.target = null;

            // Stop the movement if the player is stunned.
            if (IsStunned() || teleporting)
            {
                StopMovement();
                return;
            }

            // If the player clicked anywhere outside the bank then the bank is no longer opened.
            canAccessContainer = false;
            activeLootBag = "";
            activeCraftingInterface = -1;

            if (map.IsDoor(x, y) || InCombat()) return;

            var diffX = Math.Abs(this.x - x);
            var diffY = Math.Abs(this.y - y);

            // No-clip detection if the difference is greater than 2 tiles.
            if (diffX > 2 || diffY > 2)
            {
                Notify($"No-clip detected at {JsString.Of(this.x)}({JsString.Of(x)}), {JsString.Of(this.y)}({JsString.Of(y)}). Please relog.");

                cheatScore++;

                Log.Bug($"{username} has no-clipped from {JsString.Of(this.x)}({JsString.Of(x)}), {JsString.Of(this.y)}({JsString.Of(y)}).");

                Teleport(oldX, oldY, false);

                invalidateMovement = true;
            }
        }

        public void HandleMovementStarted(double x, double y, object speed, string target)
        {
            // Stop the movement if the client is reporting a different position.
            var diffX = Math.Abs(this.x - x);
            var diffY = Math.Abs(this.y - y);

            // Refuse any movement if the starting point mismatches our player's position by more than 2 tiles.
            if (diffX > 2 || diffY > 2) return;

            movementStart = Node.DateNow();

            // Invalid movement speed reported by the client.
            if (!(speed is double s && s == GetMovementSpeed()))
                IncrementCheatScore($"{username} Received incorrect movement speed.");

            // Stop combat and skills every time thre is movement.
            skills.Stop();

            if (!JsData.Truthy(target)) combat.Stop();

            // Mark the player as moving.
            moving = true;
        }

        public void HandleMovementStep(double x, double y, double nextX, double nextY, double? timestamp = null)
        {
            var time = timestamp ?? Node.DateNow();

            // Increment cheat score if the player is moving while stunned.
            if (IsStunned())
            {
                IncrementCheatScore($"[{username}] Movement while stunned.");
                StopMovement();
            }

            // Add 1 to offset errors in calculations/rounding.
            double latency = JsValue.ToInt32(Node.PerformanceNow() - time);

            // Attempt to resync the player if the latency is too high.
            if (latency < 0)
            {
                Send(new NetworkPacket(Opcodes.Network.Sync, new JsObject("timestamp", Node.PerformanceNow())));
                Log.Error($"[{username}] Negative latency of {JsString.Of(latency)}ms detected.");
            }

            if (VerifySkip(x, y)) return;

            // Ensure the movement is valid, negative latency is impossible lmfao.
            if (VerifyMovement(x, y, latency))
                IncrementCheatScore(
                    $"Movement mismatch: {JsString.Of(Node.DateNow() - lastStep - latency)}ms/tile (latency: {JsString.Of(latency)}ms)");

            SetPosition(x, y);

            ResetTalk();

            lastStep = Node.DateNow() - latency;
        }

        public void HandleMovementStop(double x, double y, string target, double orientation)
        {
            // Ignore movements that don't follow the packet order.
            if (!moving)
            {
                IncrementCheatScore("Did not receive movement started packet.");
                return;
            }

            if (VerifySkip(x, y))
            {
                moving = false;
                return;
            }

            var entity = entities.Get(target);

            // Port fix (ANALYSIS.md §9 #1): items and loot bags are only reached by stopping on their tile.
            if (entity is LootBag or Item && (entity.x != x || entity.y != y)) entity = null;

            // Update orientation
            SetOrientation(orientation);

            // Player has stopped on top of a loot bag.
            if (entity is LootBag lootBag)
            {
                // Prevent access to the loot bag by other players.
                if (!lootBag.IsOwner(username))
                {
                    Notify($"This lootbag belongs to {Utils.FormatName(lootBag.owner)}.");
                    return;
                }

                lootBag.Open(this);
            }

            // Player has stopped on top of an item.
            if (entity is Item item)
            {
                // Prevent cheaters from picking up item
                if (IsCheater()) return;

                // Prevent picking up dropped items that belong to other players.
                if (!item.IsOwner(username))
                {
                    Notify($"misc:CANNOT_PICK_UP_ITEM;username={Utils.FormatName(item.owner)}");
                    return;
                }

                // If the item's owner is existent and the player is the owner we add it to the statistics.
                if (item.owner == username) statistics.AddDrop(item.key, item.count);

                inventory.Add(item);
            }

            // Update the player's position.
            SetPosition(x, y);

            // Handle doors when the player stops on one.
            if (map.IsDoor(x, y))
            {
                var door = map.GetDoor(x, y);

                doorCallback?.Invoke(door);
            }

            // Movement has come to an end.
            moving = false;
            lastMovement = Node.DateNow();
        }

        public void UpdatePVP(bool pvp)
        {
            // Skip if pvp state is the same or it's permanent
            if (this.pvp == pvp) return;

            if (this.pvp && !pvp) Notify("misc:NOT_IN_PVP_ZONE");
            else Notify("misc:IN_PVP_ZONE");

            this.pvp = pvp;

            Send(new PVPPacket(new JsObject("state", this.pvp)));
        }

        public void UpdateOverlay(Area overlay)
        {
            // Don't needlessly update if the overlay is the same
            if (overlayArea == overlay) return;

            // Store the active overlay.
            var tempOverlay = overlayArea;

            // Store for comparison.
            overlayArea = overlay;

            // No overlay object or invalid object, remove the overlay.
            if (overlay == null)
            {
                tempOverlay?.RemovePlayer(this);
                Send(new OverlayPacket(Opcodes.Overlay.Remove));
                return;
            }

            // New overlay is being loaded, remove lights.
            lightsLoaded = new List<double>();

            var colour = overlay.rgb.Count > 1
                ? $"rgba({JsString.Of(overlay.rgb[0])}, {JsString.Of(overlay.rgb[1])}, {JsString.Of(overlay.rgb.Count > 2 ? overlay.rgb[2] : (object)Undefined.Value)}, {JsString.Of(overlay.darkness)})"
                : $"rgba(0, 0, 0, {JsString.Of(overlay.darkness)})";

            Send(new OverlayPacket(Opcodes.Overlay.Set, new JsObject(
                "image", JsData.Truthy(overlay.fog) ? overlay.fog : "blank",
                "colour", colour)));

            if (overlay.IsStatusArea()) overlay.AddPlayer(this);
        }

        public void UpdateCamera(Area camera)
        {
            if (cameraArea == camera) return;

            cameraArea = camera;

            if (camera != null)
                switch (camera.type)
                {
                    case "lockX":
                        Send(new CameraPacket(Opcodes.Camera.LockX));
                        break;

                    case "lockY":
                        Send(new CameraPacket(Opcodes.Camera.LockY));
                        break;

                    case "player":
                        Send(new CameraPacket(Opcodes.Camera.Player));
                        break;
                }
            else Send(new CameraPacket(Opcodes.Camera.FreeFlow));
        }

        public void UpdateMusic(Area info = null)
        {
            var song = info?.song;

            if (song == currentSong) return;

            currentSong = song;

            Send(new MusicPacket(song));
        }

        public void UpdateMinigame(Area info = null)
        {
            if (info == minigameArea) return;

            var entering = info != null && minigameArea == null;

            if (entering) info?.enterCallback?.Invoke(this);
            else minigameArea?.exitCallback?.Invoke(this);

            minigameArea = info;
        }

        public double GetMovementSpeed()
        {
            var speed = overrideMovementSpeed == -1 // Whether to use the movement speed override.
                ? (double)Modules.Defaults.MOVEMENT_SPEED
                : overrideMovementSpeed; // Start with default.
            var boots = equipment.GetBoots();

            // Check the boots for movement modifiers
            if (boots.HasMovementModifier()) speed = Math.Floor(speed * boots.movementModifier);

            // Apply a 10% speed boost if the player running effect is present.
            if (status.Has(Modules.Effects.Running)) speed = Math.Floor(speed * 0.9);

            // Apply a 20% speed boost if the player is using the hot sauce.
            if (status.Has(Modules.Effects.HotSauce)) speed = Math.Floor(speed * 0.8);

            // Apply freezing movement speed effect, make the player move 25% slower.
            if (status.Has(Modules.Effects.Freezing) && !status.Has(Modules.Effects.SnowPotion))
                speed = Math.Floor(speed * 1.25);

            // Halve speed if the player is cheating.
            if (IsCheater()) speed = Math.Floor(speed * 2);

            // Update the movement speed if there is a change from default.
            if (movementSpeed != speed) SetMovementSpeed(speed);

            return speed;
        }

        public void SetMovementSpeed(double movementSpeed)
        {
            this.movementSpeed = movementSpeed;

            // Sync to other players in the region.
            SendToRegions(new MovementPacket(Opcodes.Movement.Speed, new JsObject(
                "instance", instance,
                "movementSpeed", movementSpeed)));
        }

        public void SetRunning(bool running, bool hotSauce = false)
        {
            Log.Debug($"{username} is running: {(running ? "true" : "false")}");

            if (running) status.Add(Modules.Effects.Running);
            else status.Remove(Modules.Effects.Running);

            if (hotSauce) status.Add(Modules.Effects.HotSauce);
            else status.Remove(Modules.Effects.HotSauce);

            SendToRegions(new MovementPacket(Opcodes.Movement.Speed, new JsObject(
                "instance", instance,
                "movementSpeed", GetMovementSpeed())));
        }

        public void SetDualistsMark(bool dualistsMark)
        {
            if (dualistsMark) status.Add(Modules.Effects.DualistsMark);
            else status.Remove(Modules.Effects.DualistsMark);

            combat.UpdateLoop();
        }

        public void SetSnowPotion()
        {
            // Remove freezing effect if it has been caused by a timeout (attack).
            if (status.HasTimeout(Modules.Effects.Freezing)) status.Remove(Modules.Effects.Freezing);

            status.AddWithTimeout(Modules.Effects.SnowPotion, Constants.SNOW_POTION_DURATION,
                () => Notify("misc:FREEZE_IMMUNITY_WORN_OFF"));

            Notify($"misc:FREEZE_IMMUNITY;duration={JsString.Of(Constants.SNOW_POTION_DURATION / 1000.0)}");
        }

        public void SetFirePotion()
        {
            // Remove burning effect if present
            if (status.Has(Modules.Effects.Burning)) status.Remove(Modules.Effects.Burning);

            status.AddWithTimeout(Modules.Effects.FirePotion, Constants.FIRE_POTION_DURATION,
                () => Notify("misc:FIRE_IMMUNITY_WORN_OFF"));

            var duration = JsString.Of(Constants.FIRE_POTION_DURATION / 1000.0);

            Notify($"misc:FIRE_IMMUNITY;duration={duration}");
        }

        public void SetRank(Modules.Ranks rank = Modules.Ranks.None)
        {
            this.rank = rank;

            Send(new RankPacket(rank));
        }

        /// <summary>setPosition(x, y) on a player: forced = false, skip = false.</summary>
        public override void SetPosition(double x, double y) => SetPosition(x, y, false, false);

        /// <summary>A third argument on a player is `forced`.</summary>
        public override void SetPosition(double x, double y, bool forced) => SetPosition(x, y, forced, false);

        public void SetPosition(double x, double y, bool forced, bool skip)
        {
            if (dead || VerifyCollision(x, y) || invalidateMovement) return;

            // Sets the player's new position.
            base.SetPosition(x, y, false);

            if (skip) return;

            // Relay a packet to the nearby regions without including the player.
            SendToRegions(new MovementPacket(Opcodes.Movement.Move, new JsObject(
                "instance", instance,
                "x", x,
                "y", y,
                "target", target?.instance,
                "forced", forced)), true);
        }

        public override void SetRegion(int region)
        {
            base.SetRegion(region);

            if (region != -1) regionCallback?.Invoke(region);
        }

        public override void SetRecentRegions(List<int> regions)
        {
            base.SetRecentRegions(regions);

            if (regions.Count > 0) recentRegionsCallback?.Invoke(regions);
        }

        /// <summary>setLastWarp(lastWarp = Date.now()): NaN becomes 0.</summary>
        public void SetLastWarp(double? lastWarp = null)
        {
            var value = lastWarp ?? Node.DateNow();
            this.lastWarp = double.IsNaN(value) ? 0 : value;
        }

        public void SetPet(string key)
        {
            if (HasPet())
            {
                Notify("misc:ALREADY_HAVE_PET");
                return;
            }

            // Create a new pet instance based on the key.
            pet = entities.SpawnPet(this, key);

            // Begin the pet following the player.
            pet.Follow(this);
        }

        public bool RemovePet()
        {
            if (!HasPet()) return false;

            // Ensure the player has enough space in their inventory.
            if (!inventory.HasSpace())
            {
                Notify("misc:NO_SPACE_PET");
                return false;
            }

            // Create a pet item and add it to the player's inventory.
            inventory.Add(new Item($"{pet.key}pet", -1, -1, false, 1));

            // Remove the pet from the world
            entities.Remove(pet);

            // Remove the pet from the player.
            pet = null;

            return true;
        }

        public override JsObject GetDisplayInfo(Player player) => GetDisplayInfo();

        public JsObject GetDisplayInfo() => new(
            "instance", instance,
            "colour", team == Team.Red ? "red" : "blue");

        public override bool HasDisplayInfo(Player player) => InMinigame();

        public bool HasManaForAttack() => mana.GetMana() >= equipment.GetWeapon().manaCost;

        public override bool HasArrows()
        {
            if (!quests.IsTutorialFinished()) return true;

            return equipment.GetArrows().count > 0;
        }

        public override bool HasBloodsucking() => equipment.GetWeapon().IsBloodsucking();

        public bool HasPet() => pet != null;

        public Position GetSpawn()
        {
            if (IsJailed()) return Utils.GetPositionFromString(Constants.JAIL_SPAWN_POINT);

            if (!quests.IsTutorialFinished()) return Utils.GetPositionFromString(Constants.TUTORIAL_SPAWN_POINT);

            if (InMinigame()) return GetMinigame().GetSpawnPoint(team);

            return Utils.GetPositionFromString(Constants.SPAWN_POINT);
        }

        public double GetTotalExperience()
        {
            double total = 0;

            skills.ForEachSkill(skill => total += skill.experience);

            return total;
        }

        public Minigame GetMinigame() => world.minigames.Get(minigame);

        public double GetTimeoutByRank() => rank switch
        {
            Modules.Ranks.TierOne => 15 * 60_000, // 15 minutes
            Modules.Ranks.TierTwo => 20 * 60_000, // 20 minutes
            Modules.Ranks.TierThree => 25 * 60_000, // 25 minutes
            Modules.Ranks.TierFour => 30 * 60_000, // 30 minutes
            Modules.Ranks.TierFive => 35 * 60_000, // 35 minutes
            Modules.Ranks.HollowAdmin or Modules.Ranks.Moderator or Modules.Ranks.TierSix => 40 * 60_000, // 40 minutes
            Modules.Ranks.Admin or Modules.Ranks.TierSeven => 45 * 60_000, // 45 minutes
            _ => 10 * 60_000 // 10 minutes
        };

        public double GetGlobalChatCooldown() => rank switch
        {
            Modules.Ranks.TierOne => 55 * 60_000,
            Modules.Ranks.TierTwo => 50 * 60_000,
            Modules.Ranks.TierThree => 45 * 60_000,
            Modules.Ranks.TierFour => 40 * 60_000,
            Modules.Ranks.TierFive => 35 * 60_000,
            Modules.Ranks.TierSix => 30 * 60_000,
            Modules.Ranks.TierSeven => 15 * 60_000,
            Modules.Ranks.Moderator or Modules.Ranks.HollowAdmin or Modules.Ranks.Admin => 5000,
            _ => 60 * 60_000
        };

        public double GetGlobalChatDuration()
        {
            var difference = GetGlobalChatCooldown() - (Node.DateNow() - lastGlobalChat);

            return Math.Ceiling(difference / 60_000);
        }

        public void LoadRegion(double region) => regionsLoaded.Add(region);

        public bool HasLoadedRegion(double region) => regionsLoaded.Contains(region);

        public bool HasLoadedLight(double light) => lightsLoaded.Contains(light);

        public void Ping()
        {
            pingTime = Node.DateNow();
            requestedPing = true;

            // Bypass the queue and send the packet immediately.
            connection.Send(new List<object> { new NetworkPacket(Opcodes.Network.Ping).Serialize() });
        }

        public void ClearAreas()
        {
            if (!InFreezingArea()) return;

            overlayArea.RemovePlayer(this);
        }

        public void ClearMinigame()
        {
            minigame = null;
            coursingScore = 0;
            coursingTarget = "";
        }

        public void ResetTalk(string instance = null)
        {
            talkIndex = 0;
            npcTalk = JsData.Truthy(instance) ? instance : "";
        }

        public bool IsMuted() => mute - Node.DateNow() > 0;

        public bool IsJailed() => jail - Node.DateNow() > 0;

        public bool InMinigame() => minigame != null;

        public bool InTeamWar() => minigame == Opcodes.Minigame.TeamWar;

        public bool IsCheater() => rank == Modules.Ranks.Cheater;

        public bool IsArtist() => rank == Modules.Ranks.Artist;

        public bool IsMod() => rank == Modules.Ranks.Moderator;

        public bool IsAdmin() => rank == Modules.Ranks.Admin || Config.Current.skipDatabase;

        public bool IsHollowAdmin() => rank == Modules.Ranks.HollowAdmin;

        public bool IsNew() => Node.DateNow() - statistics.creationTime < 60_000;

        public override bool IsArcher() => equipment.GetWeapon().IsArcher();

        public bool IsLoiteringThreshold() => Node.DateNow() - lastRegionChange >= Constants.LOITERING_THRESHOLD;

        public override bool IsPoisonous()
        {
            // If the player is an archer, we use the arrows to determine the poisonous status.
            if (IsArcher()) return equipment.GetArrows().poisonous;

            return equipment.GetWeapon().poisonous;
        }

        public override bool IsMagic() => equipment.GetWeapon().IsMagic();

        public bool InFreezingArea() => overlayArea?.IsStatusArea() ?? false;

        bool CanEat() => Node.DateNow() - lastEdible > Constants.EDIBLE_COOLDOWN;

        public bool CanCraft() => Node.DateNow() - lastCraft > Constants.CRAFT_COOLDOWN;

        public bool CanGlobalChat() => Node.DateNow() - lastGlobalChat > GetGlobalChatCooldown();

        public bool CanUseCrafting() => quests.Get(Constants.CRAFTING_QUEST_KEY).IsStarted();

        public bool CanUseAlchemy() => quests.Get(Constants.ALCHEMY_QUEST_KEY).IsStarted();

        public void Send(Packet packet) => world.Push(Modules.PacketType.Player, new PacketData { packet = packet, player = this });

        public void SendToRecentRegions(Packet packet) =>
            world.Push(Modules.PacketType.RegionList, new PacketData { list = recentRegions, packet = packet });

        public void SendToSpawn()
        {
            var spawnPoint = GetSpawn();

            Teleport(spawnPoint.x, spawnPoint.y, true);
        }

        public void SendPrivateMessage(string playerName, string message)
        {
            if (Config.Current.hubEnabled)
            {
                world.client.Send(new ChatPacket(new JsObject("source", username, "message", message, "target", playerName)));
                return;
            }

            if (!world.IsOnline(playerName))
            {
                Notify($"misc:NOT_ONLINE;username={playerName}", "crimson");
                return;
            }

            SendMessage(playerName, message);
        }

        public void SendMessage(string playerName, string message, string source = "")
        {
            var otherPlayer = world.GetPlayerByName(playerName);
            var oFormattedName = Utils.FormatName(playerName); // Formated username of the player receiving the message.
            var formattedName = Utils.FormatName(JsData.Truthy(source) ? source : username); // Formatted username of the source

            if (JsData.Truthy(source)) Notify(message, "aquamarine", $"[From {formattedName}]", true);
            else
                (otherPlayer ?? throw new JsTypeError("Cannot read properties of undefined (reading 'notify')"))
                    .Notify(message, "aquamarine", $"[From {formattedName}]", true);

            if (!JsData.Truthy(source)) Notify(message, "aquamarine", $"[To {oFormattedName}]", true);
        }

        public void Sync()
        {
            // Update attack range each-time we sync.
            attackRange = equipment.GetWeapon().attackRange;

            GetMovementSpeed();

            // Synchronize health, mana and experience with the player.
            skills.Sync();

            // Sync the player information to the surrounding regions.
            SendToRegions(new SyncPacket(Serialize(true)), true);
        }

        public void Chat(string message, bool global = false, bool withBubble = true, string colour = "")
        {
            if (!canTalk)
            {
                Notify("misc:CANNOT_TALK", "crimson");
                return;
            }

            // Handle global chat
            if (global)
            {
                if (!quests.IsTutorialFinished())
                {
                    Notify("misc:CANNOT_GLOBAL_CHAT_TUTORIAL", "crimson");
                    return;
                }

                // Jailed players cannot global chat.
                if (IsJailed())
                {
                    Notify("misc:CANNOT_GLOBAL_CHAT_JAIL", "crimson");
                    return;
                }

                // Limit the global chat to the cooldown based on ranks.
                if (!CanGlobalChat())
                {
                    Notify($"misc:CANNOT_GLOBAL_CHAT_MINUTES;duration={JsString.Of(GetGlobalChatDuration())}", "crimson");
                    return;
                }

                lastGlobalChat = Node.DateNow();
            }

            Log.Debug($"[{username}] {message}");

            var name = Utils.FormatName(username);

            if (rank != Modules.Ranks.None)
            {
                name = $"[{RankTitle(rank)}] {name}";
                colour = global ? "rgba(191, 161, 63, 1.0)" : RankColour(rank);
            }

            var source = $"{(global ? "[Global]" : "")} {name}";

            // Relay the message to the discord channel if it is enabled.
            world.discord.SendMessage(source, message, null, true);

            // Relay the hub so that it can handle the discord relay.
            world.client.Send(new ChatPacket(new JsObject("source", source, "message", message)));

            if (global)
            {
                world.GlobalMessage(name, message, colour);
                return;
            }

            var packet = new ChatPacket(new JsObject(
                "instance", instance,
                "message", message,
                "withBubble", withBubble,
                "colour", colour));

            Log.Chat($"{username}: {message}");

            SendToRegions(packet);
        }

        static JsObject _rankTitles, _rankColours;

        /// <summary>Modules.RankTitles[rank] (undefined for unknown ranks).</summary>
        public static string RankTitle(Modules.Ranks rank)
        {
            _rankTitles ??= (JsObject)JsData.Convert(JsJson.Parse(ModuleTables.RankTitlesJson));
            return JsString.Of(_rankTitles[((int)rank).ToString()] ?? Undefined.Value);
        }

        /// <summary>Modules.RankColours[rank].</summary>
        public static string RankColour(Modules.Ranks rank)
        {
            _rankColours ??= (JsObject)JsData.Convert(JsJson.Parse(ModuleTables.RankColoursJson));
            return _rankColours.Str(((int)rank).ToString());
        }

        public void Popup(string title, string message, string colour = "#00000", string soundEffect = "")
        {
            if (!JsData.Truthy(title)) return;

            Send(new NotificationPacket(Opcodes.Notification.Popup, new JsObject(
                "title", title,
                "message", message,
                "colour", colour,
                "soundEffect", soundEffect)));
        }

        public void Notify(string message, string colour = "", string source = "", bool bypass = false)
        {
            if (!JsData.Truthy(message)) return;

            // Prevent notify spams
            if (!bypass && Node.DateNow() - lastNotify < 250) return;

            Send(new NotificationPacket(Opcodes.Notification.Text, new JsObject(
                "message", message,
                "colour", colour,
                "source", source)));

            lastNotify = Node.DateNow();
        }

        public void GuildNotify(string message) => Send(new GuildPacket(Opcodes.Guild.Error, new JsObject("message", message)));

        public void Pointer(JsObject info, bool remove = true)
        {
            // Remove all existing pointers first.
            if (remove) Send(new PointerPacket(Opcodes.Pointer.Remove));

            // Invalid pointer data received.
            var type = info["type"];
            if (!(type is double t && Enum.IsDefined(typeof(Opcodes.Pointer), (int)t) && t % 1 == 0) &&
                !(type is string s && Enum.TryParse<Opcodes.Pointer>(s, false, out _)))
                return;

            Send(new Packet(Packets.Pointer, (int?)(type is double d ? (int)d : (int)Enum.Parse<Opcodes.Pointer>((string)type)), info));
        }

        public void Save()
        {
            if (Config.Current.skipDatabase || isGuest || !ready) return;

            database.Save(this);
        }

        public override JsObject Serialize() => Serialize(false);

        public JsObject Serialize(bool withEquipment, bool withExperience = false, bool withMana = false)
        {
            var data = base.Serialize();

            // Sprite key is the armour key.
            data.Set("name", Utils.FormatName(username));
            data.Set("rank", (double)rank);
            data.Set("level", skills.GetCombatLevel());
            data.Set("hitPoints", hitPoints.GetHitPoints());
            data.Set("maxHitPoints", hitPoints.GetMaxHitPoints());
            data.Set("attackRange", attackRange);
            data.Set("movementSpeed", GetMovementSpeed());

            if (InTeamWar()) data.Set("displayInfo", GetDisplayInfo());

            // Include equipment only when necessary.
            if (withEquipment) data.Set("equipments", equipment.Serialize(true)["equipments"]);

            if (withExperience) data.Set("experience", GetTotalExperience());

            if (withMana)
            {
                data.Set("mana", mana.GetMana());
                data.Set("maxMana", mana.GetMaxMana());
            }

            return data;
        }

        public override JsObject GetAttackStats() => equipment.totalAttackStats;

        public override JsObject GetDefenseStats() => equipment.totalDefenseStats;

        public override JsObject GetBonuses() => equipment.totalBonuses;

        public override double GetAccuracyBonus()
        {
            if (IsArcher()) return GetBonuses().Num("archery") ?? double.NaN;
            if (IsMagic()) return GetBonuses().Num("magic") ?? double.NaN;

            return GetBonuses().Num("accuracy") ?? double.NaN;
        }

        public override double GetAccuracyLevel()
        {
            // We use magic level for accuracy when the player is using a magic weapon.
            if (IsMagic()) return skills.Get(Modules.Skills.Magic).level;
            if (IsArcher()) return skills.Get(Modules.Skills.Archery).level;

            return skills.Get(Modules.Skills.Accuracy).level;
        }

        public override double GetStrengthLevel() => skills.Get(Modules.Skills.Strength).level;

        public override double GetArcheryLevel() => skills.Get(Modules.Skills.Archery).level;

        public override double GetDefenseLevel() => skills.Get(Modules.Skills.Defense).level;

        double GetMagicLevel() => skills.Get(Modules.Skills.Magic).level;

        public double? GetLastAttackStyle(string weaponType) => lastStyles[weaponType];

        public override double GetDamageBonus()
        {
            // Handle magic bonuses
            if (IsMagic())
            {
                // If the player does not have enough mana for the attack decrease the damage.
                if (!HasManaForAttack()) return -3;

                // Return the total magic bonus.
                return GetBonuses().Num("magic") ?? double.NaN;
            }

            // Return the archery bonus if using ranged weapons.
            if (IsArcher()) return GetBonuses().Num("archery") ?? double.NaN;

            return GetBonuses().Num("strength") ?? double.NaN;
        }

        public override double GetSkillDamageLevel()
        {
            if (IsMagic()) return GetMagicLevel();
            if (IsArcher()) return GetArcheryLevel();

            return GetStrengthLevel();
        }

        public override double GetDamageReduction()
        {
            double reduction = 1;

            // Thick skin increases damage reduction by 20%.
            if (status.Has(Modules.Effects.ThickSkin)) reduction -= 0.2;

            // Attack style defensive boosts defence by 10%.
            if (GetAttackStyle() == Modules.AttackStyle.Defensive) reduction -= 0.1;

            // Shared attack style defensive boosts defence by 4%.
            if (GetAttackStyle() == Modules.AttackStyle.Shared) reduction -= 0.04;

            return reduction;
        }

        public override Modules.AttackStyle GetAttackStyle() => equipment.GetWeapon().attackStyle;

        public override Modules.Hits GetDamageType()
        {
            // Attempt to apply the arrow effects if the player is using ranged weapons.
            if (IsArcher())
            {
                var arrows = equipment.GetArrows();

                if (arrows.freezing && Formulas.GetEffectChance()) return Modules.Hits.Freezing;
                if (arrows.burning && Formulas.GetEffectChance()) return Modules.Hits.Burning;

                // We use the player's bow to determine the damage type if the arrows do not have any effects.
                var weapon = equipment.GetWeapon();

                if (weapon.IsStun() && Formulas.GetEffectChance()) return Modules.Hits.Stun;

                // Apply the area-of-effect damage if the weapon is explosive.
                if (weapon.IsExplosive() && Formulas.GetEffectChance())
                {
                    aoe = 1;
                    return Modules.Hits.Explosive;
                }
            }
            else
            {
                var weapon = equipment.GetWeapon();

                if (weapon.IsCritical() && Formulas.GetEffectChance()) return Modules.Hits.Critical;
            }

            // Default case for damage.
            return Modules.Hits.Normal;
        }

        public override string GetProjectileName()
        {
            // Use `Character` default `projectileName` in tutorial.
            if (!quests.IsTutorialFinished()) return projectileName;

            // Use the projectile name of the arrows if the player is using ranged weapons.
            if (IsArcher()) return equipment.GetArrows().projectileName;

            return equipment.GetWeapon().projectileName;
        }

        public override double GetBloodsuckingLevel()
        {
            var level = equipment.GetWeapon().enchantments
                .Obj(((int)Modules.Enchantment.Bloodsucking).ToString())?.Num("level");
            return level is double l && l != 0 && !double.IsNaN(l) ? l : 1;
        }

        public override double GetAttackRate()
        {
            // Dualists mark status effect boosts attack speed by 200 milliseconds.
            if (status.Has(Modules.Effects.DualistsMark)) return equipment.GetWeapon().attackRate - 200;

            return equipment.GetWeapon().attackRate;
        }

        public string GetJailDuration()
        {
            var duration = jail - Node.DateNow();

            return duration > 60_000
                ? $"{JsString.Of(Math.Ceiling(duration / 60_000))} more minutes"
                : $"{JsString.Of(Math.Floor(duration / 1000))} more seconds";
        }

        public void OnKill(Action<Character> callback) => killCallback = callback;

        public void OnTalkToNPC(Action<NPC> callback) => npcTalkCallback = callback;

        public void OnDoor(Action<JsObject> callback) => doorCallback = callback;

        public void OnCheatScore(Action callback) => cheatScoreCallback = callback;

        public void OnRegion(Action<int> callback) => regionCallback = callback;

        public void OnRecentRegions(Action<List<int>> callback) => recentRegionsCallback = callback;
    }
}
