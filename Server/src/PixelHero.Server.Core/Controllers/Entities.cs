using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Controllers
{
    /// <summary>controllers/entities.ts.</summary>
    public sealed class Entities
    {
        readonly Map map;
        readonly Regions regions;
        readonly Grids grids;

        // Stores all the entities in the world.
        readonly JsObject<Entity> entities = new();

        public readonly JsObject<Player> players = new();
        readonly JsObject<Item> items = new();
        readonly JsObject<Mob> mobs = new();
        readonly JsObject<Chest> chests = new();
        readonly JsObject<NPC> npcs = new();
        readonly JsObject<Pet> pets = new();
        readonly JsObject<LootBag> lootBags = new();
        readonly JsObject<Effect> effects = new();
        readonly JsObject<Tree> trees = new();
        readonly JsObject<Rock> rocks = new();
        readonly JsObject<FishSpot> fishSpots = new();
        readonly JsObject<Foraging> foragings = new();

        readonly World world;

        public Entities(World world)
        {
            this.world = world;
            map = world.map;
            regions = world.map.regions;
            grids = world.map.grids;

            Load();
        }

        void Load()
        {
            map.ForEachEntity((position, key) =>
            {
                switch (GetEntityType(key))
                {
                    case Modules.EntityType.Item:
                        SpawnItem(key, position.x, position.y);
                        return;

                    case Modules.EntityType.NPC:
                        SpawnNPC(key, position.x, position.y);
                        return;

                    case Modules.EntityType.Mob:
                        SpawnMob(key, position.x, position.y);
                        return;

                    case Modules.EntityType.Tree:
                        SpawnTree(key, position.x, position.y);
                        return;

                    case Modules.EntityType.Rock:
                        SpawnRock(key, position.x, position.y);
                        return;

                    case Modules.EntityType.FishSpot:
                        SpawnFishSpots(key, position.x, position.y);
                        return;

                    case Modules.EntityType.Foraging:
                        SpawnForaging(key, position.x, position.y);
                        return;
                }
            });

            Log.Info($"Spawned {items.Count} items!");
            Log.Info($"Spawned {npcs.Count} NPCs!");
            Log.Info($"Spawned {mobs.Count} mobs!");
            Log.Info($"Spawned {trees.Count} trees!");
            Log.Info($"Spawned {rocks.Count} rocks!");
            Log.Info($"Spawned {fishSpots.Count} fish spots!");
            Log.Info($"Spawned {foragings.Count} foraging spots!");

            // Spawns the static chests throughout the world.
            foreach (JsObject info in map.chest)
                SpawnChest(
                    JsData.Truthy(info["items"]) ? info.Str("items").Split(',').ToList() : new List<string>(),
                    info.Num("x") ?? double.NaN,
                    info.Num("y") ?? double.NaN,
                    true,
                    info.Str("achievement"),
                    JsData.Truthy(info["mimic"]));

            Log.Info($"Spawned {chests.Count} static chests!");

            // Initialize the roaming interval for mobs
            Node.SetInterval(() =>
                ForEachMob(mob =>
                {
                    // Roaming only when players are in the region when there are more than 30 players online.
                    if (!((regions.Get(mob.region)?.HasPlayersInRegion() ?? 0) != 0) && world.GetPopulation() > 30) return;

                    mob.roamingCallback?.Invoke();
                }), (double)Modules.MobDefaults.ROAM_FREQUENCY);
        }

        public void SpawnItem(string key, double x, double y, bool dropped = false, double count = 1, JsObject enchantments = null,
            string owner = "") =>
            AddItem(new Item(key, x, y, dropped, count, enchantments ?? new JsObject(), owner));

        public void SpawnLootBag(double x, double y, string owner, List<Item> items)
        {
            // Do not spawn a loot bag if it is empty.
            if (items.Count == 0) return;

            AddLootBag(new LootBag(world, x, y, owner, items));
        }

        public Mob SpawnMob(string key, double x, double y, bool plugin = false)
        {
            var mob = new Mob(world, key, x, y, plugin);

            AddMob(mob);

            return mob;
        }

        void OnResourceState(Resource resource) =>
            resource.OnStateChange(() =>
                world.Push(Modules.PacketType.Regions, new PacketData
                {
                    region = resource.region,
                    packet = new ResourcePacket(new JsObject("instance", resource.instance, "state", (double)resource.state))
                }));

        public Tree SpawnTree(string key, double x, double y)
        {
            var tree = new Tree(key, x, y);

            OnResourceState(tree);

            Add(tree);

            trees.Set(tree.instance, tree);

            return tree;
        }

        public Rock SpawnRock(string key, double x, double y)
        {
            var rock = new Rock(key, x, y);

            OnResourceState(rock);

            Add(rock);

            rocks.Set(rock.instance, rock);

            return rock;
        }

        public FishSpot SpawnFishSpots(string key, double x, double y)
        {
            var fishSpots = new FishSpot(key, x, y);

            OnResourceState(fishSpots);

            Add(fishSpots);

            this.fishSpots.Set(fishSpots.instance, fishSpots);

            return fishSpots;
        }

        public Foraging SpawnForaging(string key, double x, double y)
        {
            var foraging = new Foraging(key, x, y);

            OnResourceState(foraging);

            Add(foraging);

            foragings.Set(foraging.instance, foraging);

            return foraging;
        }

        public Chest SpawnChest(List<string> items, double x, double y, bool isStatic = false, string achievement = null,
            bool mimic = false)
        {
            var chest = new Chest(x, y, achievement, mimic, items);

            // Static chests respawn after a certain amount of time.
            if (isStatic)
            {
                chest.@static = isStatic;
                chest.OnRespawn(() => Add(chest));
            }

            chest.OnOpen(player =>
            {
                Remove(chest);

                // We use the player's world instance to spawn mimic mob.
                if (player != null && mimic)
                {
                    var mimicMob = SpawnMob("mimic", chest.x, chest.y);

                    // Mimic's death respawns the chest. We also ensure mimic doesn't respawn.
                    if (mimicMob != null)
                    {
                        mimicMob.respawnable = false;
                        mimicMob.chest = chest;
                    }
                }

                // Spawn chest items if there are any.
                var item = chest.GetItem();

                if (item == null) return;

                SpawnItem(item.key, chest.x, chest.y, true, item.count);

                // Reward the player with an achievement if they have one.
                if (player != null && JsData.Truthy(chest.achievement)) player.achievements.Get(chest.achievement)?.Finish();
            });

            AddChest(chest);

            return chest;
        }

        void SpawnNPC(string key, double x, double y) => AddNPC(new NPC(key, x, y));

        public Projectile SpawnProjectile(Character owner, Character target, Hit hit)
        {
            var projectile = new Projectile(owner, target, hit);

            // Remove on impact
            projectile.OnImpact(() => Remove(projectile));

            Add(projectile);

            return projectile;
        }

        public Pet SpawnPet(Player owner, string key)
        {
            var pet = new Pet(owner, key);

            AddPet(pet);

            return pet;
        }

        public void SpawnEffect(string key, double x, double y) => AddEffect(new Effect(key, x, y));

        void Add(Entity entity)
        {
            if (entities.Has(entity.instance)) Log.Warning($"Entity {entity.instance} already exists.");

            entities.Set(entity.instance, entity);

            // Do not have the projectiles be parsed as part of the region.
            if (entity.IsProjectile()) return;

            regions.Handle(entity);

            grids.AddToEntityGrid(entity);
        }

        public void AddPlayer(Player player)
        {
            Add(player);

            players.Set(player.instance, player);
        }

        public void AddItem(Item item)
        {
            // Callback for removing the item from the world.
            item.OnDespawn(() => RemoveItem(item));

            // Dropped items have a callback for their despawn.
            if (item.dropped)
            {
                item.Despawn();

                // Blinking timeout before the item despawns.
                item.OnBlink(() => world.Push(Modules.PacketType.Broadcast, new PacketData { packet = new BlinkPacket(item.instance) }));
            }
            else item.OnRespawn(() => AddItem(item));

            Add(item);

            items.Set(item.instance, item);
        }

        void AddLootBag(LootBag lootBag)
        {
            lootBag.OnEmpty(() => RemoveLootBag(lootBag));

            Add(lootBag);

            lootBags.Set(lootBag.instance, lootBag);
        }

        public void AddMob(Mob mob)
        {
            Add(mob);

            mobs.Set(mob.instance, mob);

            mob.AddToChestArea(map.GetChestAreas());
        }

        void AddChest(Chest chest)
        {
            Add(chest);

            chests.Set(chest.instance, chest);
        }

        void AddNPC(NPC npc)
        {
            Add(npc);

            npcs.Set(npc.instance, npc);
        }

        void AddPet(Pet pet)
        {
            Add(pet);

            pets.Set(pet.instance, pet);
        }

        void AddEffect(Effect effect)
        {
            effect.OnDespawn(() => RemoveEffect(effect));

            Add(effect);

            effects.Set(effect.instance, effect);
        }

        public void Remove(Entity entity)
        {
            // Signal to nearby regions that the entity has been removed.
            world.Push(Modules.PacketType.Regions, new PacketData
            {
                region = entity.region,
                packet = new DespawnPacket(new JsObject("instance", entity.instance))
            });

            // Remove the entity from the entity grid
            grids.RemoveFromEntityGrid(entity);

            // Remove the entity from the region it is in.
            regions.Remove(entity);

            entities.Delete(entity.instance);

            // Clean combat when removing a player.
            if (entity.IsPlayer()) world.CleanCombat((Character)entity);
        }

        public void RemovePlayer(Player player)
        {
            Remove(player);

            players.Delete(player.instance);

            world.network.DeletePacketQueue(player);
        }

        public void RemoveChest(Chest chest)
        {
            Remove(chest);

            if (chest.mimic || !chest.@static) chests.Delete(chest.instance);
            else chest.Respawn();
        }

        public void RemoveItem(Item item)
        {
            Remove(item);

            // Dropped items are removed permanently, static ones respawn.
            if (item.dropped) items.Delete(item.instance);
            else item.Respawn();
        }

        public void RemoveLootBag(LootBag lootBag)
        {
            Remove(lootBag);

            lootBags.Delete(lootBag.instance);
        }

        public void RemoveMob(Mob mob)
        {
            Remove(mob);

            mobs.Delete(mob.instance);
        }

        public void RemovePet(Pet pet)
        {
            Remove(pet);

            pets.Delete(pet.instance);
        }

        public void RemoveEffect(Effect effect)
        {
            Remove(effect);

            effects.Delete(effect.instance);
        }

        public Entity Get(string instance) => instance == null ? null : entities[instance];

        public Player GetPlayer(string username) =>
            players.Values.FirstOrDefault(player =>
                JsString.ToLowerCase(player.username) == JsString.ToLowerCase(username ??
                    throw new JsTypeError("Cannot read properties of undefined (reading 'toLowerCase')")));

        public List<string> GetPlayerUsernames() => players.Values.ConvertAll(player => player.username);

        public List<Player> GetPlayersByIp(string ip) => players.Values.Where(player => player.connection.address == ip).ToList();

        public NPC GetNPCByKey(string key) => npcs.Values.FirstOrDefault(npc => npc.key == key);

        /// <summary>`key in data` for each data file (an unknown key gives -1).</summary>
        static Modules.EntityType GetEntityType(string key)
        {
            if (ServerData.Items.Has(key)) return Modules.EntityType.Item;
            if (ServerData.Npcs.Has(key)) return Modules.EntityType.NPC;
            if (ServerData.Mobs.Has(key)) return Modules.EntityType.Mob;
            if (ServerData.Trees.Has(key)) return Modules.EntityType.Tree;
            if (ServerData.Rocks.Has(key)) return Modules.EntityType.Rock;
            if (ServerData.Fishing.Has(key)) return Modules.EntityType.FishSpot;
            if (ServerData.Foraging.Has(key)) return Modules.EntityType.Foraging;

            return (Modules.EntityType)(-1);
        }

        public void ForEachEntity(Action<Entity> callback)
        {
            foreach (var entity in entities.Values) callback(entity);
        }

        public void ForEachCharacter(Action<Character> callback) =>
            ForEachEntity(entity =>
            {
                if (entity is Character character) callback(character);
            });

        public void ForEachMob(Action<Mob> callback)
        {
            foreach (var mob in mobs.Values) callback(mob);
        }

        public void ForEachPlayer(Action<Player> callback)
        {
            foreach (var player in players.Values) callback(player);
        }

        /// <summary>The entities in creation order (for tests that compare the world with the original's).</summary>
        public List<Entity> All => entities.Values;
    }
}
