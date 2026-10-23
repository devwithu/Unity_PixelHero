using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>game/map/region.ts.</summary>
    public sealed class Region
    {
        public List<object> data = new();

        readonly JsObject<Entity> entities = new();
        List<string> players = new(); // A list of instance ids for players.
        List<Entity> joining = new(); // Used for sending spawn positions.
        readonly List<Area> dynamicAreas = new();
        readonly List<Light> lights = new();

        public double x;
        public double y;
        public double width;
        public double height;

        public Region(double x, double y, double width, double height)
        {
            this.x = x;
            this.y = y;
            this.width = width;
            this.height = height;
        }

        public void AddPlayer(Player player) => players.Add(player.instance);

        public void RemovePlayer(Player player) => players = players.Where(instance => instance != player.instance).ToList();

        public void AddEntity(Entity entity) => entities.Set(entity.instance, entity);

        public void RemoveEntity(Entity entity) => entities.Delete(entity.instance);

        public bool HasEntity(Entity entity) => entities.Has(entity.instance);

        public void AddJoining(Entity entity)
        {
            if (!HasEntity(entity)) joining.Add(entity);
        }

        public void AddDynamicArea(Area area) => dynamicAreas.Add(area);

        public bool HasDynamicAreas() => dynamicAreas.Count > 0;

        public void AddLight(Light light) => lights.Add(light);

        public bool IsJoining() => joining.Count > 0;

        public void ClearJoining() => joining = new List<Entity>();

        public bool InRegion(double x, double y) => x >= this.x && y >= this.y && x < this.x + width && y < this.y + height;

        public double HasPlayersInRegion() => players.Count;

        public Area GetDynamicArea(double x, double y)
        {
            foreach (var area in dynamicAreas)
                if (area.Contains(x, y))
                    return area;

            return null;
        }

        public List<string> GetEntities(Player player, Entity reject = null)
        {
            var list = new List<string>();

            foreach (var pair in entities)
            {
                // Ignore if a reject is present.
                if (reject?.instance == pair.Key) continue;

                // Check if the entity is visible to the player.
                if (!pair.Value.IsVisible(player)) continue;

                // Append our instance to the list.
                list.Add(pair.Key);
            }

            return list;
        }

        public void ForEachTile(Action<double, double> callback)
        {
            for (var i = y; i < y + height; i++)
                for (var j = x; j < x + width; j++)
                    if (GetDynamicArea(j, i) == null)
                        callback(j, i);
        }

        public void ForEachDynamicTile(Action<double, double, Area> callback)
        {
            foreach (var area in dynamicAreas) area.ForEachTile((x, y) => callback(x, y, area));
        }

        public void ForEachEntity(Action<Entity> callback)
        {
            foreach (var entity in entities.Values) callback(entity);
        }

        public void ForEachLight(Action<Light> callback)
        {
            foreach (var light in lights) callback(light);
        }

        public void ForEachPlayer(Action<Player> callback)
        {
            foreach (var instance in players)
                if (entities.Has(instance))
                    callback((Player)entities[instance]);
        }

        public void ForEachJoining(Action<Entity> callback)
        {
            foreach (var entity in joining) callback(entity);
        }
    }

    /// <summary>game/map/regions.ts.</summary>
    public sealed class Regions
    {
        readonly World world;
        readonly List<Region> regions = new();

        readonly double divisionSize;
        double sideLength = -1;

        readonly Areas dynamicAreas;

        Action<Entity, int> enterCallback;
        Action<Entity, int> joiningCallback;

        readonly Map map;

        public Regions(Map map)
        {
            this.map = map;
            world = map.world;

            // Division size must be evenly divisble by the map's width and height.
            divisionSize = Constants.MAP_DIVISION_SIZE;

            dynamicAreas = map.GetDynamicAreas();

            BuildRegions();
            LoadDynamicAreas();

            OnEnter(HandleEnter);
            OnJoining(HandleJoining);
        }

        void BuildRegions()
        {
            if (map.width % divisionSize != 0)
            {
                Log.Error("Corrupted map regions. Unable to evenly divide into sections.");
                Log.Error($"Map: {JsString.Of(map.width)}x{JsString.Of(map.height)} - divisions: {JsString.Of(divisionSize)}.");
                return;
            }

            for (double y = 0; y < map.height; y += divisionSize)
                for (double x = 0; x < map.width; x += divisionSize)
                    regions.Add(new Region(x, y, divisionSize, divisionSize));

            // Number of regions per side.
            sideLength = map.width / divisionSize;

            // Begin the region cache loading if the config allows it.
            if (Config.Current.regionCache) LoadRegionCache();
        }

        void LoadDynamicAreas()
        {
            // If someone creates a new map without dynamic areas, skip.
            if (dynamicAreas == null) return;

            // Parse through each areas in the dynamic areas group.
            dynamicAreas.ForEachArea(area =>
            {
                if (!area.IsMappingArea()) return;

                var region = GetRegion(area.x, area.y);

                if (region == -1) Log.Error($"[ID: {JsString.Of(area.id)}] Dynamic area could not be processed.");
                else regions[region].AddDynamicArea(area);
            });
        }

        /// <summary>
        /// The region cache: the C# server always generates it at start (the original reads ./cache/regions.json when
        /// it exists and the map version matches, which gives the same data).
        /// </summary>
        void LoadRegionCache()
        {
            ForEachRegion((region, _) => region.data = GetRegionTileData(region));

            Log.Notice("Region cache has been successfully created.");
        }

        public void Parse() =>
            ForEachRegion((region, index) =>
            {
                if (!region.IsJoining()) return;

                SendJoining(index);

                region.ClearJoining();
            });

        public bool Handle(Entity entity)
        {
            var isNewRegion = false;
            var region = GetRegion(entity.x, entity.y);

            if (entity.region == -1 || entity.region != region)
            {
                isNewRegion = true;

                Joining(entity, region);

                var oldRegions = Remove(entity);
                var newRegions = Enter(entity, region);

                // Update the recently left regions to the entity.
                entity.SetRecentRegions(oldRegions.Where(r => !newRegions.Contains(r)).ToList());
            }

            return isNewRegion;
        }

        void HandleEnter(Entity entity, int region)
        {
            if (!entity.IsPlayer() || !((Player)entity).ready) return;

            Log.Debug($"Entity: {entity.instance} entering region: {region}.");

            SendRegion((Player)entity);
        }

        void HandleJoining(Entity entity, int region)
        {
            if (!entity.IsPlayer()) return;

            Log.Debug($"Entity: {entity.instance} joining region: {region}.");
        }

        void Joining(Entity entity, int region)
        {
            if (entity == null || region == -1) return;

            regions[region].AddJoining(entity);

            joiningCallback?.Invoke(entity, region);
        }

        List<int> Enter(Entity entity, int region)
        {
            var newRegions = new List<int>();

            if (entity == null) return newRegions;

            ForEachSurroundingRegion(region, surroundingRegion =>
            {
                var surrounding = regions[surroundingRegion];

                surrounding.AddEntity(entity);

                newRegions.Add(surroundingRegion);
            });

            entity.SetRegion(region);

            if (entity.IsPlayer())
                (Get(region) ?? throw new JsTypeError("Cannot read properties of undefined (reading 'addPlayer')"))
                    .AddPlayer((Player)entity);

            enterCallback?.Invoke(entity, region);

            return newRegions;
        }

        public List<int> Remove(Entity entity)
        {
            var oldRegions = new List<int>();

            if (entity == null || entity.region == -1) return oldRegions;

            var region = regions[entity.region];

            if (entity.IsPlayer()) region.RemovePlayer((Player)entity);

            oldRegions = GetOldRegions(entity);

            entity.SetRegion(-1);

            return oldRegions;
        }

        public void SendEntities(Player player)
        {
            if (player.region == -1) return;

            var entities = regions[player.region].GetEntities(player, player);

            player.Send(new ListPacket(Opcodes.List.Spawns, new JsObject("entities", entities.Cast<object>().ToList())));
        }

        public void SendEntityPositions(Player player)
        {
            if (player.region == -1) return;

            // Dictionary to store the positions and instances of the entities.
            var positions = new JsObject();

            // Iterate through all the entities in the region.
            Get(player.region).ForEachEntity(entity =>
            {
                // Skip non-moving entities.
                if (entity is not Character character) return;

                // Skip the player and moving entities.
                if (entity.instance == player.instance || character.moving) return;

                // Store the position of the entity.
                positions.Set(entity.instance, new JsObject("x", entity.x, "y", entity.y));
            });

            // Send the positions to the client.
            player.Send(new ListPacket(Opcodes.List.Positions, new JsObject("positions", positions)));
        }

        public void SendJoining(int region)
        {
            regions[region].ForEachJoining(entity =>
                world.Push(Modules.PacketType.Regions, new PacketData
                {
                    region = region,
                    ignore = entity.IsPlayer() ? entity.instance : "",
                    packet = new SpawnPacket(entity)
                }));

            // Synchronizes the display info for each entity and updates the list of entities.
            regions[region].ForEachPlayer(SendDisplayInfo);
        }

        List<int> GetOldRegions(Entity entity)
        {
            var oldRegions = new List<int>();

            ForEachSurroundingRegion(entity.region, surroundingRegion =>
            {
                var region = regions[surroundingRegion];

                if (!region.HasEntity(entity)) return;

                region.RemoveEntity(entity);

                oldRegions.Add(surroundingRegion);
            });

            return oldRegions;
        }

        public void SendDisplayInfo(Player player)
        {
            if (player.region == -1) return;

            var displayInfos = GetDisplayInfo(player);

            // Don't send empty data.
            if (displayInfos.Count == 0) return;

            player.Send(new UpdatePacket(displayInfos));
        }

        public void SendRegion(Player player) => player.Send(new MapPacket(GetRegionData(player)));

        public void SendUpdate(int region) =>
            ForEachSurroundingRegion(region, surroundingRegion => Get(surroundingRegion).ForEachPlayer(SendRegion));

        /// <summary>regions[region]: undefined (null) outside the list.</summary>
        public Region Get(int region) => region >= 0 && region < regions.Count ? regions[region] : null;

        public int GetRegion(double x, double y) => regions.FindIndex(region => region.InRegion(x, y));

        public JsObject GetRegionData(Player player, bool force = false)
        {
            var data = new JsObject();
            var region = GetRegion(player.x, player.y);

            ForEachSurroundingRegion(region, surroundingRegion =>
            {
                var surrounding = regions[surroundingRegion];
                var key = surroundingRegion.ToString();

                // Initialize empty array for the region tile data.
                var tiles = new List<object>();
                data.Set(key, tiles);

                // Parse and send dynamic areas.
                if (surrounding.HasDynamicAreas()) tiles.AddRange(GetRegionTileData(surrounding, true, player));

                // We skip if the region is loaded and we are not forcing static data.
                if (!player.HasLoadedRegion(surroundingRegion) || force)
                {
                    tiles.AddRange(Config.Current.regionCache ? surrounding.data : GetRegionTileData(surrounding));

                    player.LoadRegion(surroundingRegion);
                }

                // Remove data to prevent client from parsing unnecessarily.
                if (tiles.Count == 0) data.Delete(key);
            });

            return data;
        }

        List<object> GetRegionTileData(Region region, bool dynamic = false, Player player = null)
        {
            var tileData = new List<object>();

            if (dynamic)
                region.ForEachDynamicTile((x, y, area) => tileData.Add(BuildDynamicTile(player, area, x, y)));
            else
                region.ForEachTile((x, y) =>
                {
                    var tile = BuildTile(x, y);

                    // Empty static tile data should be ignored. Otherwise this will cause issues when trying to send resource data.
                    // (tile.data as number) < 1: an array compares through its string form.
                    if (JsAny.ToNumber(tile["data"]) < 1) return;

                    tileData.Add(tile);
                });

            return tileData;
        }

        List<object> GetDisplayInfo(Player player)
        {
            var entityData = new List<object>();
            var instances = new List<string>(); // Used for preventing duplicates.

            ForEachSurroundingRegion(player.region, surroundingRegion =>
            {
                var region = Get(surroundingRegion);

                if (region == null)
                {
                    Log.Warning($"[{player.username}] Invalid surrounding region: {surroundingRegion} for player's region: {player.region}.");
                    return;
                }

                region.ForEachEntity(entity =>
                {
                    if (!entity.HasDisplayInfo(player)) return;

                    // Prevents duplicate entity data.
                    if (instances.Contains(entity.instance)) return;

                    entityData.Add(entity.GetDisplayInfo(player));

                    instances.Add(entity.instance);
                });
            });

            return entityData;
        }

        /// <summary>buildTile(x, y, index?, data?): `index ||=` and `data ||=` replace falsy values.</summary>
        JsObject BuildTile(double x, double y, double? index = null, object data = null)
        {
            // Use the specified index if not undefined or calculate it.
            var tileIndex = index is double i && i != 0 && !double.IsNaN(i) ? i : map.CoordToIndex(x, y);

            // Use the provided data parameter or obtain it from the map file if not specified.
            if (!JsData.Truthy(data)) data = map.DataAt(tileIndex);

            if (!JsData.Truthy(data) || (data is List<object> list && list.Count == 0))
                return new JsObject("x", x, "y", y, "data", 0.0);

            var tile = new JsObject("x", x, "y", y, "data", data);

            // We use this function to prevent iterating through the tile array multiple times. Since collisions,
            // objects, and cursors all call the `forEachTile` function, we can just do it in a batch here.
            Map.ForEachTile(data, tileId =>
            {
                // Remove the tile bitmasks to get the actual tile id.
                if (Map.IsFlippedTileId(tileId)) tileId = Map.GetFlippedTileId(tileId);

                if (map.objects.Contains(tileId))
                {
                    tile.Set("o", true);
                    tile.Set("c", true);
                }
                else if (map.collisions.Contains(tileId)) tile.Set("c", true);

                var cursor = map.cursors.Str(JsString.Of(tileId));

                if (JsData.Truthy(cursor)) tile.Set("cur", cursor);
            });

            return tile;
        }

        JsObject BuildDynamicTile(Player player, Area area, double x, double y)
        {
            var original = BuildTile(x, y);

            // Does not proceed any further if the player does not fulfill the requirements
            if (!area.FulfillsRequirement(player)) return original;

            var mappedTile = area.GetMappedTile(x, y);

            // Defaults to building the original tile if mapping cannot be achieved.
            if (mappedTile == null) return original;

            // Gets the index of the mapped tile coordinates.
            var index = map.CoordToIndex(mappedTile.x, mappedTile.y);
            var tile = BuildTile(x, y, index); // Build tile using the mapped tile's index
            var mappedAnimationTile = area.GetMappedAnimationTile(x, y);

            // If we have a mapped animation tile we add it to the tile data. Note that this animation is purely
            // cosmetic and does not affect the tile in any way.
            if (mappedAnimationTile != null)
                tile.Set("animation", map.DataAt(map.CoordToIndex(mappedAnimationTile.x, mappedAnimationTile.y)));

            return tile;
        }

        public void ForEachSurroundingRegion(int region, Action<int> callback)
        {
            foreach (var surrounding in GetSurroundingRegions(region)) callback(surrounding);
        }

        public void ForEachRegion(Action<Region, int> callback)
        {
            for (var index = 0; index < regions.Count; index++) callback(regions[index], index);
        }

        List<int> GetSurroundingRegions(int region)
        {
            var surroundingRegions = new List<int> { region };
            var side = (int)sideLength;

            if (region < 0 || region > regions.Count - 1)
            {
                Log.Warning($"Attempted to grab surrounding regions for invalid region: {region}.");
                return new List<int>();
            }

            // Handle regions near edge of the map and horizontally.
            // left edge piece
            if (IsLeftEdge(region)) surroundingRegions.Add(region + 1);
            // right edge piece.
            else if (IsRightEdge(region)) surroundingRegions.Add(region - 1);
            else surroundingRegions.AddRange(new[] { region - 1, region + 1 });

            // Handle vertical edge cases
            if (IsTopEdge(region) || IsBottomEdge(region))
            {
                var relativeRegion = IsTopEdge(region) ? region + side : region - side;

                surroundingRegions.Add(relativeRegion);

                if (IsLeftEdge(relativeRegion)) surroundingRegions.Add(relativeRegion + 1);
                else if (IsRightEdge(relativeRegion)) surroundingRegions.Add(relativeRegion - 1);
                else surroundingRegions.AddRange(new[] { relativeRegion - 1, relativeRegion + 1 });
            }
            else
            {
                if (IsLeftEdge(region))
                    surroundingRegions.AddRange(new[] { region - side, region - side + 1, region + side, region + side + 1 });
                else if (IsRightEdge(region))
                    surroundingRegions.AddRange(new[] { region - side, region - side - 1, region + side, region + side - 1 });
                else
                    surroundingRegions.AddRange(new[]
                    {
                        region + side, region - side, region + side - 1, region + side + 1, region - side - 1,
                        region - side + 1
                    });
            }

            return surroundingRegions;
        }

        // Checks if the region is on the left border of the map.
        bool IsLeftEdge(int region) => region % (int)sideLength == 0;

        // Checks if region is on right border of the map.
        bool IsRightEdge(int region) => region % (int)sideLength == (int)sideLength - 1;

        // Checks if the region is on the top of the map.
        bool IsTopEdge(int region) => region < sideLength;

        // Checks if the region is on the bottom of the map.
        bool IsBottomEdge(int region) => region > regions.Count - sideLength - 1;

        void OnEnter(Action<Entity, int> callback) => enterCallback = callback;

        void OnJoining(Action<Entity, int> callback) => joiningCallback = callback;
    }
}
