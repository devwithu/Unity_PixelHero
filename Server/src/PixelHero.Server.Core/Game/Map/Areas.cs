using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>game/map/areas/area.ts.</summary>
    public sealed class Area
    {
        public List<Position> polygon;
        readonly List<string> entities = new(); // Stores instances of mobs in the area.
        public Chest chest;
        public List<string> items = new();
        public bool hasRespawned = true;
        public bool ignore; // If the area is omitted from player walk callbacks.

        public double boundaryX;
        public double boundaryY;

        // Overlay properties
        public double darkness;
        public List<double> rgb = new();
        public string type = "none";
        public string fog = "";
        public string reason = ""; // Message displayed when player takes damage in the area.

        // Properties it can hold
        public string quest = "";
        public string achievement = "";
        public string cameraType = "";
        public string song = "";

        // Chest coordinates
        public double cx;
        public double cy;

        // Dynamic areas
        public Area mappedArea;
        public Area mappedAnimation;
        public double mapping;
        public double animation;

        // Minigame
        public string minigame = "";
        public string mObjectType = "";

        // Players
        public JsObject<Player> players = new();

        public double maxEntities;
        public double spawnDelay;
        public double lastSpawn = double.NaN; // undefined until a chest spawns

        public Action<Player> enterCallback;
        public Action<Player> exitCallback;
        Action spawnCallback;
        Action<Character> emptyCallback;

        public double id;
        public double x;
        public double y;
        public double width;
        public double height;

        public Area(double id, double x, double y, double width, double height)
        {
            this.id = id;
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;

            boundaryX = x + width;
            boundaryY = y + height;
        }

        public void AddEntity(Mob mob)
        {
            if (entities.Contains(mob.instance)) return;

            entities.Add(mob.instance);

            // Grab a spawn delay from an mob to create an offset for the chest.
            if (spawnDelay == 0 || double.IsNaN(spawnDelay)) spawnDelay = mob.respawnDelay;

            spawnCallback?.Invoke();
        }

        public void AddPlayer(Player player)
        {
            if (players.Has(player.instance)) return;

            players.Set(player.instance, player);

            // Adds a freezing effect to the player if the area is cold.
            if (IsStatusArea()) player.status.Add(Modules.Effects.Freezing);
        }

        public void RemoveEntity(Mob mob, Character attacker = null)
        {
            var index = entities.IndexOf(mob.instance);

            if (index > -1) entities.RemoveAt(index);

            if (entities.Count == 0) emptyCallback?.Invoke(attacker);
        }

        public void RemovePlayer(Player player)
        {
            players.Delete(player.instance);

            if (IsStatusArea()) player.status.Remove(Modules.Effects.Freezing);
        }

        /// <summary>Quest or achievement finished (undefined counts as false).</summary>
        public bool FulfillsRequirement(Player player)
        {
            if (JsData.Truthy(quest)) return player.quests.Get(quest)?.IsFinished() ?? false;

            if (JsData.Truthy(achievement)) return player.achievements.Get(achievement)?.IsFinished() ?? false;

            return false;
        }

        public Position GetMappedTile(double x, double y)
        {
            if (mappedArea == null) return null;

            // The x and y relative to the area rather than globally.
            var relativeX = Math.Abs(this.x - x);
            var relativeY = Math.Abs(this.y - y);

            return new Position(mappedArea.x + relativeX, mappedArea.y + relativeY);
        }

        public Position GetMappedAnimationTile(double x, double y)
        {
            if (mappedAnimation == null) return null;

            // The x and y relative to the area rather than globally.
            var relativeX = Math.Abs(this.x - x);
            var relativeY = Math.Abs(this.y - y);

            return new Position(mappedAnimation.x + relativeX, mappedAnimation.y + relativeY);
        }

        public bool IsMappingArea() => mappedArea != null;

        public bool Contains(double x, double y)
        {
            if (ignore) return false; // Skip ignorable areas.

            return polygon != null ? InPolygon(x, y) : InRectangularArea(x, y);
        }

        public bool IsStatusArea() => type == "freezing";

        bool InRectangularArea(double x, double y) => x >= this.x && y >= this.y && x < boundaryX && y < boundaryY;

        bool InPolygon(double x, double y)
        {
            var inside = false;

            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                double xi = polygon[i].x, yi = polygon[i].y, xj = polygon[j].x, yj = polygon[j].y;
                var intersect = yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi;

                if (intersect) inside = !inside;
            }

            return inside;
        }

        public void ForEachTile(Action<double, double> callback)
        {
            for (var i = y; i < boundaryY; i++)
                for (var j = x; j < boundaryX; j++)
                    callback(j, i);
        }

        public void ForEachPlayer(Action<Player> callback)
        {
            foreach (var pair in players) callback(pair.Value);
        }

        public void OnEnter(Action<Player> callback) => enterCallback = callback;

        public void OnExit(Action<Player> callback) => exitCallback = callback;

        public void OnEmpty(Action<Character> callback) => emptyCallback = callback;

        public void OnSpawn(Action callback) => spawnCallback = callback;
    }

    /// <summary>game/map/areas/areas.ts.</summary>
    public abstract class Areas
    {
        public List<Area> areas = new();

        public List<object> data;
        public World world;

        protected Areas(List<object> data, World world)
        {
            this.data = data;
            this.world = world;
        }

        public void Load(List<object> mapAreas, Action<Area, JsObject> callback = null)
        {
            foreach (var entry in mapAreas)
            {
                var a = (JsObject)entry;
                var area = new Area(a.Num("id") ?? double.NaN, a.Num("x") ?? double.NaN, a.Num("y") ?? double.NaN,
                    a.Num("width") ?? double.NaN, a.Num("height") ?? double.NaN);

                // Add polygon if present.
                if (JsData.Truthy(a["polygon"]))
                    area.polygon = a.Arr("polygon").ConvertAll(point =>
                        new Position(((JsObject)point).Num("x") ?? double.NaN, ((JsObject)point).Num("y") ?? double.NaN));

                if (JsData.Truthy(a["ignore"])) area.ignore = true;

                // Add to our list of areas.
                areas.Add(area);

                // Callback the last element in our list alongside the processed area info
                callback?.Invoke(areas[^1], a);
            }
        }

        public void Message(string type) => Log.Info($"Loaded {areas.Count} {type} areas.");

        public Area Get(double id) => areas.Find(area => area.id == id);

        public Area InArea(double x, double y) => areas.Find(area => area.Contains(x, y));

        public void ForEachArea(Action<Area> callback)
        {
            foreach (var area in areas) callback(area);
        }
    }

    public sealed class CameraAreas : Areas
    {
        public CameraAreas(List<object> data, World world) : base(data, world)
        {
            Load(this.data, (area, rawData) => area.cameraType = rawData.Str("type"));
            Message("camera");
        }
    }

    /// <summary>game/map/areas/impl/chest.ts.</summary>
    public sealed class ChestAreas : Areas
    {
        public ChestAreas(List<object> data, World world) : base(data, world)
        {
            Load(this.data, (chestArea, rawData) =>
            {
                chestArea.maxEntities = JsData.Truthy(rawData["entities"]) ? (double)rawData["entities"] : 0;
                chestArea.items = (rawData.Str("items") ?? throw new JsTypeError("Cannot read properties of undefined (reading 'split')"))
                    .Split(',').ToList();
                chestArea.cx = rawData.Num("spawnX") ?? double.NaN;
                chestArea.cy = rawData.Num("spawnY") ?? double.NaN;

                // Achievement we reward to player when they loot the chest.
                if (JsData.Truthy(rawData["achievement"])) chestArea.achievement = rawData.Str("achievement");

                // Once the area is cleared of mobs callback.
                chestArea.OnEmpty(attacker =>
                {
                    SpawnChest(chestArea);

                    // Award the player with the achievement.
                    if (attacker != null && JsData.Truthy(chestArea.achievement))
                        (attacker as Player)?.achievements?.Get(chestArea.achievement)?.Finish();
                });

                chestArea.OnSpawn(() => RemoveChest(chestArea));
            });

            Message("chest");
        }

        void SpawnChest(Area chestArea)
        {
            if (Utils.TimePassed(chestArea.lastSpawn, chestArea.spawnDelay)) return;

            chestArea.chest = world.entities.SpawnChest(chestArea.items, chestArea.cx, chestArea.cy, false);

            chestArea.lastSpawn = Node.DateNow();
        }

        void RemoveChest(Area chestArea)
        {
            if (chestArea.chest == null) return;

            world.entities.RemoveChest(chestArea.chest);
            chestArea.chest = null;
        }
    }

    /// <summary>game/map/areas/impl/dynamic.ts.</summary>
    public sealed class DynamicAreas : Areas
    {
        public DynamicAreas(List<object> data, World world) : base(data, world)
        {
            Load(this.data, (area, rawData) =>
            {
                area.mapping = rawData.Num("mapping") ?? double.NaN;
                area.animation = rawData.Num("animation") ?? double.NaN;
                area.quest = rawData.Str("quest");
                area.achievement = rawData.Str("achievement");
            });

            Link();

            Log.Info($"Loaded {JsString.Of(Math.Ceiling(areas.Count / 2.0))} dynamic areas.");
        }

        void Link()
        {
            foreach (var area in areas)
            {
                if (area.mapping == 0 || double.IsNaN(area.mapping)) continue;

                area.mappedArea = Get(area.mapping);
                area.mappedAnimation = Get(area.animation);
            }
        }
    }

    public sealed class MinigameAreas : Areas
    {
        public MinigameAreas(List<object> data, World world) : base(data, world)
        {
            Load(this.data, (area, rawData) =>
            {
                // Used to determine what minigame the object belongs to.
                area.minigame = rawData.Str("minigame");

                // Object type used within the minigame to organize the area.
                area.mObjectType = rawData.Str("mObjectType");
            });

            Message("minigame");
        }
    }

    public sealed class MusicAreas : Areas
    {
        public MusicAreas(List<object> data, World world) : base(data, world)
        {
            Load(this.data, (musicArea, rawData) => musicArea.song = rawData.Str("song"));
            Message("music");
        }
    }

    public sealed class OverlayAreas : Areas
    {
        public OverlayAreas(List<object> data, World world) : base(data, world)
        {
            Load(this.data, (overlayArea, rawData) =>
            {
                overlayArea.darkness = rawData.Num("darkness") ?? double.NaN;
                overlayArea.type = JsData.Truthy(rawData["type"]) ? rawData.Str("type") : "none";

                if (JsData.Truthy(rawData["fog"])) overlayArea.fog = rawData.Str("fog");
                if (JsData.Truthy(rawData["rgb"]))
                    overlayArea.rgb = rawData.Str("rgb").Split(',').Select(JsAny.StringToNumber).ToList();
            });

            Message("overlay");
        }
    }

    public sealed class PVPAreas : Areas
    {
        public PVPAreas(List<object> data, World world) : base(data, world)
        {
            Load(this.data);
            Message("PVP");
        }
    }
}
