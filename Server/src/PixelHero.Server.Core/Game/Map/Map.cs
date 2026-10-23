using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>game/map/map.ts over data/map/world.json.</summary>
    public sealed class Map
    {
        // Map versioning and information
        public double version;
        public double width;
        public double height;
        public double tileSize;

        // Map handlers
        public Regions regions;
        public Grids grids;

        // Map data and collisions
        public List<object> data;
        public HashSet<double> collisions;
        readonly JsObject entities;

        public JsObject plateau;
        public HashSet<double> objects;
        public JsObject cursors;
        public JsObject<JsObject> doors = new();
        public List<object> warps;
        public List<object> lights;
        public List<object> signs;

        // Static chest areas, named as singular to prevent confusion with `chests` area.
        public List<object> chest;

        readonly JsObject<Areas> areas = new();

        public World world;

        /// <summary>areas/index.ts: the area groups the server loads, by world.json key.</summary>
        static readonly Dictionary<string, Func<List<object>, World, Areas>> AreasIndex = new()
        {
            ["camera"] = (data, world) => new CameraAreas(data, world),
            ["chests"] = (data, world) => new ChestAreas(data, world),
            ["music"] = (data, world) => new MusicAreas(data, world),
            ["overlay"] = (data, world) => new OverlayAreas(data, world),
            ["pvp"] = (data, world) => new PVPAreas(data, world),
            ["dynamic"] = (data, world) => new DynamicAreas(data, world),
            ["minigame"] = (data, world) => new MinigameAreas(data, world)
        };

        public Map(World world)
        {
            var map = ServerData.Map;
            var mapAreas = map.Obj("areas");

            version = map.Num("version") ?? double.NaN;
            width = map.Num("width") ?? double.NaN;
            height = map.Num("height") ?? double.NaN;
            tileSize = map.Num("tileSize") ?? double.NaN;

            grids = new Grids(width, height);

            data = map.Arr("data");
            // includes() lookups over the id lists.
            collisions = new HashSet<double>((map.Arr("collisions") ?? new List<object>()).Cast<double>());
            entities = map.Obj("entities");
            MapFixes.Apply(entities, width);

            plateau = map.Obj("plateau");
            objects = new HashSet<double>(map.Arr("objects").Cast<double>());
            MapFixes.ApplyObjects(objects);
            cursors = map.Obj("cursors");
            warps = mapAreas.Arr("warps") ?? new List<object>();
            lights = mapAreas.Arr("lights") ?? new List<object>();
            signs = mapAreas.Arr("signs") ?? new List<object>();
            chest = mapAreas.Arr("chest") ?? new List<object>();

            this.world = world;

            LoadAreas();
            LoadDoors();

            regions = new Regions(this);
        }

        void LoadAreas()
        {
            foreach (var pair in ServerData.Map.Obj("areas"))
            {
                if (!AreasIndex.TryGetValue(pair.Key, out var create)) continue;

                areas.Set(pair.Key, create((List<object>)pair.Value, world));
            }
        }

        void LoadDoors()
        {
            var doorsData = ServerData.Map.Obj("areas").Arr("doors") ??
                            throw new JsTypeError("Cannot read properties of undefined (reading 'map')");
            var doorsClone = doorsData.ConvertAll(door => (JsObject)JsData.Clone(door));

            static object Or(object a, object b) => JsData.Truthy(a) ? a : b;

            // Iterate through the doors in the map.
            foreach (JsObject door in doorsData)
            {
                // Skip if the door does not have a destination.
                if (!JsData.Truthy(door["destination"])) continue;

                // Find destination door in the clone list of doors.
                var index = CoordToIndex(door.Num("x") ?? double.NaN, door.Num("y") ?? double.NaN);
                var destination = doorsClone.Find(cloneDoor => door["destination"] is double d && cloneDoor["id"] is double id && d == id);

                if (destination == null) continue;

                // Assign destination door information to the door we are parsing.
                doors.Set(JsString.Of(index), new JsObject(
                    "x", destination["x"],
                    "y", destination["y"],
                    "orientation", Or(destination["orientation"], "d"),
                    "quest", Or(door["quest"], ""),
                    "achievement", Or(door["achievement"], ""),
                    "reqAchievement", Or(door["reqAchievement"], ""),
                    "reqQuest", Or(door["reqQuest"], ""),
                    "reqItem", Or(door["reqItem"], ""),
                    "reqItemCount", Or(door["reqItemCount"], 0.0),
                    "stage", Or(door["stage"], 0.0),
                    "skill", Or(door["skill"], ""),
                    "level", Or(door["level"], 0.0),
                    "npc", Or(door["npc"], "")));
            }
        }

        public double CoordToIndex(double x, double y) => y * width + x;

        public Position IndexToCoord(double index) => new(index % width, JsValue(index / width));

        static double JsValue(double value) => PixelHero.Js.JsValue.ToInt32(value);

        /// <summary>tileId > DIAGONAL_FLAG.</summary>
        public static bool IsFlippedTileId(double tileId) => tileId > (double)Modules.MapFlags.DIAGONAL_FLAG;

        public bool IsDoor(double x, double y) => JsData.Truthy(doors[JsString.Of(CoordToIndex(x, y))]);

        public bool IsOutOfBounds(double x, double y) => x < 0 || x >= width || y < 0 || y >= height;

        public bool IsCollision(object data)
        {
            var collision = false;

            ForEachTile(data, tile =>
            {
                // Remove the tile transformation flags if they exist.
                if (IsFlippedTileId(tile)) tile = GetFlippedTileId(tile);

                if (collisions.Contains(tile)) collision = true;
            });

            return collision;
        }

        /// <summary>The tile data at an index (undefined outside the array).</summary>
        public object DataAt(double index) =>
            index >= 0 && index < data.Count && index % 1 == 0 ? data[(int)index] : null;

        public bool IsCollisionIndex(double index)
        {
            var data = DataAt(index);

            // Empty data means the tile is a collision.
            if (!JsData.Truthy(data)) return true;

            return IsCollision(data);
        }

        public bool IsColliding(double x, double y, Player player = null)
        {
            if (IsOutOfBounds(x, y)) return true;

            // Verify dynamic tile collision if player is provided as a parameter.
            var region = regions.Get(regions.GetRegion(x, y));
            var index = CoordToIndex(x, y);

            // Skip if there are no dynamic areas in the region.
            if (player != null && (region ?? throw new JsTypeError("Cannot read properties of undefined (reading 'hasDynamicAreas')")).HasDynamicAreas())
            {
                var dynamicArea = region.GetDynamicArea(x, y);

                // Skip if no dynamic area is found or it doesn't fulfill requirement.
                if (dynamicArea != null && dynamicArea.FulfillsRequirement(player))
                {
                    var mappedTile = dynamicArea.GetMappedTile(x, y);

                    // Check collision if we can find a mapping tile.
                    if (mappedTile != null) return IsColliding(mappedTile.x, mappedTile.y);
                }
            }

            var hasEntity = false;

            if (grids.HasEntityAt(x, y))
                grids.ForEachEntityAt(x, y, entity =>
                {
                    if (entity.IsResource()) hasEntity = true;
                });

            // Check the collision at a specified index.
            return hasEntity || IsCollisionIndex(index);
        }

        public bool IsObject(object data)
        {
            var isObject = false;

            ForEachTile(data, tileId =>
            {
                if (objects.Contains(tileId)) isObject = true;
            });

            return isObject;
        }

        public Areas GetChestAreas() => areas["chests"];

        public Areas GetDynamicAreas() => areas["dynamic"];

        public Areas GetMinigameAreas() => areas["minigame"];

        public JsObject GetDoor(double x, double y) => doors[JsString.Of(CoordToIndex(x, y))];

        public string GetCursor(double index)
        {
            var cursor = "";

            ForEachTile(DataAt(index), tileId =>
            {
                var key = JsString.Of(tileId);
                if (cursors.Has(key)) cursor = cursors.Str(key);
            });

            return cursor;
        }

        public double GetPlateauLevel(double x, double y)
        {
            var index = CoordToIndex(x, y);

            var key = JsString.Of(index);
            if (!plateau.Has(key)) return 0;

            // Plateau at the coordinate index.
            return plateau.Num(key) ?? double.NaN;
        }

        /// <summary>tileId &amp; ~(DIAGONAL | VERTICAL | HORIZONTAL), in int32 like JavaScript's bitwise operators.</summary>
        public static double GetFlippedTileId(double tileId)
        {
            var mask = unchecked((int)((uint)Modules.MapFlags.DIAGONAL_FLAG | (uint)Modules.MapFlags.VERTICAL_FLAG |
                                       (uint)Modules.MapFlags.HORIZONTAL_FLAG));
            return PixelHero.Js.JsValue.ToInt32(tileId) & ~mask;
        }

        public void ForEachAreas(Action<Areas, string> callback, List<string> list = null)
        {
            list ??= new List<string>();

            foreach (var pair in areas)
            {
                if (list.Count > 0 && !list.Contains(pair.Key)) continue;

                callback(pair.Value, pair.Key);
            }
        }

        /// <summary>forEachTile: an array's elements (by index), or the value itself.</summary>
        public static void ForEachTile(object data, Action<double> callback)
        {
            if (data is List<object> list)
            {
                foreach (var tile in list) callback(tile is double d ? d : double.NaN);
                return;
            }

            callback(data is double value ? value : double.NaN);
        }

        public void ForEachEntity(Action<Position, string> callback)
        {
            foreach (var pair in entities)
            {
                var position = IndexToCoord(JsString.ParseInt(pair.Key));

                callback(position, pair.Value as string);
            }
        }
    }
}
