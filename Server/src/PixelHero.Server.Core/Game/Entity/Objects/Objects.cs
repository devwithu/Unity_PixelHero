using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>game/entity/character/pet/pet.ts.</summary>
    public sealed class Pet : Character
    {
        public Player owner;

        public Pet(Player owner, string key) : this(owner, key, owner.x, owner.y) { }

        public Pet(Player owner, string key, double x, double y)
            : base(Utils.CreateInstance(Modules.EntityType.Pet), owner.world, key, x, y)
        {
            this.owner = owner;
        }

        /// <summary>{...super.serialize(), owner, movementSpeed}.</summary>
        public override JsObject Serialize()
        {
            var data = base.Serialize();

            data.Set("owner", owner.instance);
            data.Set("movementSpeed", owner.movementSpeed);

            return data;
        }
    }

    /// <summary>game/entity/npc/npc.ts.</summary>
    public sealed class NPC : Entity
    {
        readonly JsObject data;
        List<object> text = new();
        public string role;
        public string store = "";

        public NPC(string key, double x, double y) : base(Utils.CreateInstance(Modules.EntityType.NPC), key, x, y)
        {
            data = ServerData.Npcs.Obj(key);

            if (data == null)
            {
                Log.Error($"[NPC] Could not find data for {key}.");
                return;
            }

            // Load default NPC data.
            name = data.Str("name");
            text = JsData.Truthy(data["text"]) ? data.Arr("text") : text;
            role = data.Str("role");
            store = JsData.Truthy(data["store"]) ? data.Str("store") : "";
        }

        public void Talk(Player player) => Talk(player, text);

        public void Talk(Player player, List<object> text)
        {
            if (!(player != null && HasDialogue(text))) return;

            // Reset the talking index if we talk to a new NPC.
            if (player.npcTalk != key) player.ResetTalk(key);

            // Text to display at the current talking index.
            var message = player.talkIndex >= 0 && player.talkIndex < text.Count ? text[(int)player.talkIndex] : null;

            if (player.talkIndex > text.Count - 1) player.talkIndex = 0;
            else player.talkIndex++;

            // Send the network packet of the current dialogue index.
            player.Send(new NPCPacket(Opcodes.NPC.Talk, new JsObject(
                "instance", instance,
                "text", message)));
        }

        public bool HasDialogue(List<object> text) => text.Count > 0;

        string GetNameColour(Player player)
        {
            if (player != null)
            {
                if (player.quests.GetQuestFromNPC(this) != null) return Mob.NameColour(Modules.SpecialEntityTypes.Quest);

                if (player.achievements.GetAchievementFromEntity(this) != null)
                    return Mob.NameColour(Modules.SpecialEntityTypes.Achievement);
            }

            return "";
        }

        string GetExclamation(Player player)
        {
            if (player != null)
            {
                var quest = player.quests.GetQuestFromNPC(this);

                if (quest != null && quest.IsNPCForStage(player, this)) return "blue";

                if (player.achievements.GetAchievementFromEntity(this) != null) return "achievement";
            }

            return "";
        }

        public override JsObject GetDisplayInfo(Player player) => new(
            "instance", instance,
            "colour", GetNameColour(player),
            "exclamation", GetExclamation(player));

        public override bool HasDisplayInfo(Player player)
        {
            if (player != null)
            {
                if (player.quests.GetQuestFromNPC(this) != null) return true;
                if (player.achievements.GetAchievementFromEntity(this) != null) return true;
            }

            return false;
        }
    }

    /// <summary>game/entity/objects/chest.ts.</summary>
    public sealed class Chest : Entity
    {
        public sealed class ItemDrop
        {
            public string key;
            public double count;
        }

        // If the chest should respawn.
        public bool @static;

        readonly double respawnDuration = Constants.CHEST_RESPAWN;

        Action<Player> openCallback;
        Action respawnCallback;

        public string achievement;
        public bool mimic;
        readonly List<string> items;

        public Chest(double x, double y, string achievement = null, bool mimic = false, List<string> items = null)
            : base(Utils.CreateInstance(Modules.EntityType.Chest), "chest", x, y)
        {
            this.achievement = achievement;
            this.mimic = mimic;
            this.items = items ?? new List<string>();
        }

        public void OpenChest(Player player = null) => openCallback?.Invoke(player);

        /// <summary>setTimeout(this.respawnCallback!, ...): Node throws at once when the callback is undefined.</summary>
        public void Respawn()
        {
            var callback = respawnCallback ??
                           throw new JsTypeError("The \"callback\" argument must be of type function. Received undefined");

            Node.SetTimeout(callback, respawnDuration);
        }

        public ItemDrop GetItem()
        {
            if (items.Count == 0) return null;

            // Picks a random item from the list of items.
            var random = Utils.RandomInt(0, items.Count - 1);
            var item = items[(int)random];
            double count = 1, probability = 100;

            // If the item has specified count and probability, we use that to determine drop rate.
            if (item.Contains(':'))
            {
                var itemData = new Queue<string>(item.Split(':'));

                item = itemData.Count > 0 ? itemData.Dequeue() : null; // name
                count = itemData.Count > 0 ? JsString.ParseInt(itemData.Dequeue()) : double.NaN; // count
                probability = itemData.Count > 0 ? JsString.ParseInt(itemData.Dequeue()) : double.NaN; // probability
            }

            if (string.IsNullOrEmpty(item)) return null;

            // Roll against the probability factor.
            if (Utils.RandomInt(0, 100) > probability) return null;

            return new ItemDrop { key = item, count = count };
        }

        public void OnOpen(Action<Player> callback) => openCallback = callback;

        public void OnRespawn(Action callback) => respawnCallback = callback;
    }

    /// <summary>game/entity/objects/effect.ts.</summary>
    public sealed class Effect : Entity
    {
        readonly JsObject data;
        readonly double duration = 4000;
        Action despawnCallback;

        public Effect(string key, double x, double y) : base(Utils.CreateInstance(Modules.EntityType.Effect), key, x, y)
        {
            data = ServerData.EffectEntities.Obj(key);

            if (data == null)
            {
                Log.Error($"[NPC] Could not find data for {key}.");
                return;
            }

            duration = data.Has("duration") && data["duration"] != null ? data.Num("duration") ?? double.NaN : duration;

            Node.SetTimeout(() => despawnCallback?.Invoke(), duration);
        }

        public void OnDespawn(Action callback) => despawnCallback = callback;
    }

    /// <summary>game/entity/objects/projectile.ts.</summary>
    public sealed class Projectile : Entity
    {
        Action impactCallback;

        public Character owner;
        public Character target;
        public Hit hit;

        public Projectile(Character owner, Character target, Hit hit)
            : base(Utils.CreateInstance(Modules.EntityType.Projectile), owner.GetProjectileName(), owner.x, owner.y)
        {
            this.owner = owner;
            this.target = target;
            this.hit = hit;

            // Apply the terror hit type if the projectile is a terror projectile.
            if (key == "terror") this.hit.type = Modules.Hits.Terror;

            // Apply the terorr projectile if the hit type is terror.
            if (this.hit.type == Modules.Hits.Terror) key = "terror";

            Node.SetTimeout(HandleImpact, this.owner.GetDistance(this.target) * 90);
        }

        void HandleImpact()
        {
            target?.Hit(hit.GetDamage(), owner, hit.aoe);

            // Updates the status effect based on the hit type.
            target?.AddStatusEffect(hit);

            // Despawn callback.
            impactCallback?.Invoke();
        }

        public override JsObject Serialize()
        {
            var data = base.Serialize();

            data.Set("ownerInstance", owner.instance);
            data.Set("targetInstance", target.instance);
            data.Set("hit", hit.Serialize());

            return data;
        }

        public void OnImpact(Action callback) => impactCallback = callback;
    }

    /// <summary>game/entity/objects/resource/resource.ts.</summary>
    public abstract class Resource : Entity
    {
        // Amount of time it takes for the resource to respawn.
        protected double respawnTime = Constants.RESOURCE_RESPAWN;

        // Empty initialization for the resource info, subclasses will override this.
        public JsObject data = new("levelRequirement", 1.0, "experience", 0.0, "difficulty", 0.0, "item", "");

        // The state of the resource
        public Modules.ResourceState state = Modules.ResourceState.Default;

        // Callbacks for when the resource state changes.
        JsTimeout respawnTimeout;
        Action stateCallback;

        protected Resource(string instance, string key, double x, double y) : base(instance, key, x, y) { }

        public void Deplete()
        {
            // Cannot cut a tree that's already cut.
            if (respawnTimeout != null) return;

            SetState(Modules.ResourceState.Depleted);

            // Reset the tree once the timeout expires.
            respawnTimeout = Node.SetTimeout(() =>
            {
                SetState(Modules.ResourceState.Default);
                respawnTimeout = null;
            }, respawnTime);
        }

        public double GetRespawnTime() => respawnTime;

        public bool IsDepleted() => state == Modules.ResourceState.Depleted;

        void SetState(Modules.ResourceState state)
        {
            this.state = state;

            stateCallback?.Invoke();
        }

        public void SetRespawnTime(double? time = null) => respawnTime = time ?? GetRespawnTime();

        /// <summary>setData(data): data.name ?? this.name. An undefined entry throws like the original.</summary>
        protected void SetData(JsObject data)
        {
            this.data = data ?? throw new JsTypeError("Cannot read properties of undefined (reading 'name')");
            name = data.Has("name") && data["name"] != null ? data["name"] as string : name;
        }

        public override JsObject Serialize()
        {
            var data = base.Serialize();

            data.Set("state", (double)state);

            return data;
        }

        public void OnStateChange(Action callback) => stateCallback = callback;
    }

    public sealed class Tree : Resource
    {
        public Tree(string key, double x, double y) : base(Utils.CreateInstance(Modules.EntityType.Tree), key, x, y) =>
            SetData(ServerData.Trees.Obj(key));
    }

    public sealed class Rock : Resource
    {
        public Rock(string key, double x, double y) : base(Utils.CreateInstance(Modules.EntityType.Rock), key, x, y) =>
            SetData(ServerData.Rocks.Obj(key));
    }

    public sealed class FishSpot : Resource
    {
        public FishSpot(string key, double x, double y) : base(Utils.CreateInstance(Modules.EntityType.FishSpot), key, x, y) =>
            SetData(ServerData.Fishing.Obj(key));
    }

    public sealed class Foraging : Resource
    {
        public Foraging(string key, double x, double y) : base(Utils.CreateInstance(Modules.EntityType.Foraging), key, x, y) =>
            SetData(ServerData.Foraging.Obj(key));
    }

    /// <summary>game/entity/objects/lootbag.ts.</summary>
    public sealed class LootBag : Entity
    {
        readonly JsObject<Item> container = new();
        Action emptyCallback;

        JsTimeout blinkTimeout;
        JsTimeout destroyTimeout;

        readonly World world;
        public string owner;

        public LootBag(World world, double x, double y, string owner, List<Item> items)
            : base(Utils.CreateInstance(Modules.EntityType.LootBag), "lootbag", x, y)
        {
            this.world = world;
            this.owner = owner;

            // Iterate through the items and add them to the loot bag.
            for (var i = 0; i < items.Count; i++) container.Set(i.ToString(), items[i]);

            // Begin the destroying timer.
            Timer();
        }

        void Destroy()
        {
            Close();

            // Clear the timeouts.
            Node.ClearTimeout(blinkTimeout);
            Node.ClearTimeout(destroyTimeout);

            blinkTimeout = null;
            destroyTimeout = null;

            emptyCallback?.Invoke();
        }

        void Timer()
        {
            blinkTimeout = Node.SetTimeout(() =>
            {
                blinkTimeout = null;

                owner = "";

                destroyTimeout = Node.SetTimeout(() =>
                {
                    destroyTimeout = null;

                    Destroy();
                }, (double)Modules.ItemDefaults.DESPAWN_DURATION);
            }, (double)Modules.ItemDefaults.BLINK_DELAY);
        }

        public void Open(Player player)
        {
            // Prevent cheating and packet manipulation.
            if (GetDistance(player) > 1)
            {
                Log.Warning($"open(): Player {player.username} tried to open a loot bag that was too far away.");
                return;
            }

            player.activeLootBag = instance;

            player.Send(new LootBagPacket(Opcodes.LootBag.Open, new JsObject("items", GetItems())));
        }

        public void Take(Player player, double index, double count = 1)
        {
            if (instance != player.activeLootBag)
            {
                Log.Warning($"Player {player.username} tried to take an item from a loot bag without opening it.");
                return;
            }

            // Double check that the player has access to the loot bag.
            if (!IsOwner(player.username))
            {
                player.Notify("item:CANNOT_ACCESS_LOOTBAG");
                return;
            }

            // Verify that the player is close enough to the lootbag.
            if (GetDistance(player) > 1)
            {
                Log.Warning($"take(): Player {player.username} tried to take an item from a loot bag that was too far away.");
                return;
            }

            var key = JsString.Of(index);
            var item = container[key];

            // This is to prevent taking items at the same time/that don't exist.
            if (item == null) return;

            if (JsData.Truthy(owner) && owner != player.username)
            {
                player.Notify("item:CANNOT_ACCESS_LOOTBAG");
                return;
            }

            // Ensure the player has enough space in their inventory.
            if (!player.inventory.HasSpace())
            {
                player.Notify("misc:NO_SPACE");
                return;
            }

            // Removes the item from the loot bag.
            container.Delete(key);

            // Add the item to the player's inventory.
            player.inventory.Add(item);

            // Destroy the loot bag if it is empty.
            if (IsEmpty()) Destroy();
            else SendTakePacket(index);
        }

        public void Close() =>
            world.network.SendToSurroundingRegions(region, new LootBagPacket(Opcodes.LootBag.Close, new JsObject()));

        public void SendTakePacket(double index) =>
            world.network.SendToSurroundingRegions(region, new LootBagPacket(Opcodes.LootBag.Take, new JsObject("index", index)));

        public bool IsOwner(string username)
        {
            if (!JsData.Truthy(owner)) return true;

            return owner == username;
        }

        bool IsEmpty() => container.Count == 0;

        List<object> GetItems()
        {
            var items = new List<object>();

            foreach (var pair in container)
            {
                var item = pair.Value;

                if (item == null) continue;

                items.Add(new JsObject(
                    "index", JsString.ParseInt(pair.Key),
                    "key", item.key,
                    "count", item.count,
                    "enchantments", item.enchantments));
            }

            return items;
        }

        public void OnEmpty(Action callback) => emptyCallback = callback;
    }
}
