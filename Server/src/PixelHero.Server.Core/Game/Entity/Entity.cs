using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>
    /// game/entity/entity.ts.
    /// Port rule for every entity class: TypeScript runs the base constructor, then the subclass's field initializers,
    /// then its constructor body. C# field initializers run before the base constructor, so anything with a side
    /// effect (timers, random numbers, reading other fields) is initialized at the top of the constructor body instead.
    /// </summary>
    public abstract class Entity
    {
        public double type; // EntityType
        public string name = "";
        public double x = -1;
        public double y = -1;
        public double oldX = -1;
        public double oldY = -1;
        public bool dead;
        public int region = -1;
        public string colour = ""; // name colour displayed for the entity
        public double scale; // scale of the entity (default if not specified)
        public bool visible = true; // used to hide an entity from being sent to the client.
        public List<int> recentRegions = new(); // regions the entity just left

        public Action<double, double> movementCallback;

        public string instance;
        public string key;

        protected Entity(string instance, string key, double x, double y)
        {
            this.instance = instance ?? "";
            this.key = key ?? "";

            type = Utils.GetEntityType(this.instance);

            UpdatePosition(x, y);
        }

        public virtual void SetPosition(double x, double y)
        {
            // On initialization just set oldX/Y to current position
            oldX = this.x;
            oldY = this.y;

            this.x = x;
            this.y = y;

            // Make a callback
            movementCallback?.Invoke(x, y);
        }

        public void UpdatePosition(double x, double y)
        {
            oldX = this.x == -1 ? x : this.x;
            oldY = this.y == -1 ? y : this.y;

            this.x = x;
            this.y = y;
        }

        public virtual void SetRegion(int region) => this.region = region;

        public virtual void SetRecentRegions(List<int> regions) => recentRegions = regions;

        public double GetDistance(Entity entity) => Math.Abs(x - entity.x) + Math.Abs(y - entity.y);

        public virtual JsObject GetDisplayInfo(Player player) => new("instance", instance);

        public virtual bool HasDisplayInfo(Player player) => false;

        protected bool IsNear(Entity entity, double distance)
        {
            var dx = Math.Abs(x - entity.x);
            var dy = Math.Abs(y - entity.y);

            return dx <= distance && dy <= distance;
        }

        public bool IsAdjacent(Entity entity) => GetDistance(entity) < 2;

        public bool IsNonDiagonal(Entity entity) => IsAdjacent(entity) && !(entity.x != x && entity.y != y);

        public bool IsVisible(Player player = null)
        {
            // Return visibility status if no player provided or entity is not an NPC.
            if (player == null || !IsNPC()) return visible;

            var quest = player.quests.GetQuestFromNPC((NPC)this, true);

            // No quest found, return the visibility status of the entity.
            if (quest == null) return visible;

            // Check if quest is completed and check if NPC is hidden.
            return quest.IsNPCVisible(key);
        }

        public bool IsCharacter() => this is Character;

        public bool IsMob() => type == (double)Modules.EntityType.Mob;

        public bool IsNPC() => type == (double)Modules.EntityType.NPC;

        public bool IsItem() => type == (double)Modules.EntityType.Item;

        public bool IsLootBag() => type == (double)Modules.EntityType.LootBag;

        public bool IsChest() => type == (double)Modules.EntityType.Chest;

        public bool IsPlayer() => type == (double)Modules.EntityType.Player;

        public bool IsProjectile() => type == (double)Modules.EntityType.Projectile;

        public bool IsPet() => type == (double)Modules.EntityType.Pet;

        public bool IsEffect() => type == (double)Modules.EntityType.Effect;

        public bool IsTree() => type == (double)Modules.EntityType.Tree;

        public bool IsRock() => type == (double)Modules.EntityType.Rock;

        public bool IsFishSpot() => type == (double)Modules.EntityType.FishSpot;

        public bool IsForaging() => type == (double)Modules.EntityType.Foraging;

        public bool IsResource() => IsTree() || IsRock() || IsFishSpot() || IsForaging();

        /// <summary>EntityData: {instance, type, name, key, x, y}.</summary>
        public virtual JsObject Serialize() => new(
            "instance", instance,
            "type", type,
            "name", name,
            "key", key,
            "x", x,
            "y", y);

        public void OnMovement(Action<double, double> callback) => movementCallback = callback;
    }
}
