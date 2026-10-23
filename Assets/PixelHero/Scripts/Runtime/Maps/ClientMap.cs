using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Maps
{
    /// <summary>A tile id with Tiled flip flags taken off (map.ts getFlippedTile).</summary>
    public sealed class TransformedTile
    {
        public int TileId;
        public bool H;
        public bool V;
        public bool D;
    }

    /// <summary>
    /// The client's map state, ported from client/src/map/map.ts (data, collisions, objects, cursors, dynamic
    /// animated tiles; rendering lives elsewhere). Coordinates are the original's: top-left origin, y down.
    ///
    /// A cell of <see cref="Data"/> is what the original stores: 0 (nothing loaded), an int tile id, a
    /// <see cref="TransformedTile"/>, or a List&lt;object&gt; of those (bottom to top).
    /// </summary>
    public sealed class ClientMap
    {
        const double DiagonalFlag = (double)(uint)Modules.MapFlags.DIAGONAL_FLAG;

        public readonly MapInfo Info;
        public readonly int Width;
        public readonly int Height;
        public readonly object[] Data;

        /// <summary>grid[y][x] of map.ts as one array: 1 = colliding (the starting value everywhere), 0 = free.</summary>
        public readonly byte[] Grid;

        /// <summary>Indexes of object tiles, in the order they were added (map.ts keeps an array).</summary>
        public readonly List<int> Objects = new List<int>();

        /// <summary>Index → cursor name ("" once removed, like the original).</summary>
        public readonly Dictionary<int, string> CursorTiles = new Dictionary<int, string>();

        /// <summary>Index → the tile data underneath a one-time (door-like) animation.</summary>
        public readonly Dictionary<int, object> DynamicAnimatedTiles = new Dictionary<int, object>();

        /// <summary>map.ts reads game.isLowPowerMode() for every loaded tile.</summary>
        public Func<bool> IsLowPowerMode = () => false;

        public int RegionsLoaded;

        /// <summary>A region was parsed (map.ts stores it with storage.setRegionData).</summary>
        public event Action<int, List<object>> RegionLoaded;

        /// <summary>A cell changed (the WebGL renderer's setTile hook; the Unity renderer redraws it).</summary>
        public event Action<int> TileChanged;

        public ClientMap(MapInfo info)
        {
            Info = info;
            Width = info.Width;
            Height = info.Height;
            Data = new object[Width * Height];
            Grid = new byte[Width * Height];

            // loadGrid: every cell starts empty and colliding.
            for (var i = 0; i < Data.Length; i++)
            {
                Data[i] = 0;
                Grid[i] = 1;
            }
        }

        /// <summary>
        /// map.ts loadRegions: <c>for (let region in regionData)</c>, so integer region ids come in ascending
        /// order whatever the order in the JSON text.
        /// </summary>
        public void LoadRegions(Dictionary<string, object> regionData)
        {
            foreach (var key in JsPropertyOrder(regionData.Keys))
                LoadRegion((List<object>)regionData[key], (int)double.Parse(key, CultureInfo.InvariantCulture));
        }

        public void LoadRegion(List<object> data, int region)
        {
            foreach (var tile in data) LoadRegionTileData((Dictionary<string, object>)tile);
            RegionLoaded?.Invoke(region, data);
        }

        void LoadRegionTileData(Dictionary<string, object> tile)
        {
            var x = (int)(double)tile["x"];
            var y = (int)(double)tile["y"];
            var index = CoordToIndex(x, y);
            var objectIndex = Objects.IndexOf(index);
            var useAnimationData = !DynamicAnimatedTiles.ContainsKey(index) && !IsLowPowerMode();
            var tileData = ParseTileData(tile["data"]);
            var animationData = Truthy(tile, "animation") ? ParseTileData(tile["animation"]) : null;

            Data[index] = useAnimationData ? animationData ?? tileData : tileData;

            if (animationData != null) DynamicAnimatedTiles[index] = tileData;

            var colliding = Truthy(tile, "c");
            if (colliding && !IsColliding(x, y)) Grid[index] = 1;
            if (!colliding && IsColliding(x, y)) Grid[index] = 0;

            if (Truthy(tile, "cur")) CursorTiles[index] = (string)tile["cur"];
            if (!Truthy(tile, "cur") && CursorTiles.ContainsKey(index)) CursorTiles[index] = "";

            var isObject = Truthy(tile, "o");
            if (isObject && objectIndex < 0) Objects.Add(index);
            if (!isObject && objectIndex > -1) Objects.RemoveAt(objectIndex);

            TileChanged?.Invoke(index);
        }

        /// <summary>map.ts parseTileData: a number stays a number unless flipped; arrays are parsed per element.</summary>
        public object ParseTileData(object data)
        {
            if (data is List<object> list)
                return list.Select(id => ParseTileId((double)id)).ToList();

            return ParseTileId((double)data);
        }

        static object ParseTileId(double tileId) =>
            IsFlippedTileId(tileId) ? (object)GetFlippedTile(tileId) : (int)tileId;

        /// <summary>map.ts isFlippedTileId: strictly greater than the diagonal flag.</summary>
        public static bool IsFlippedTileId(double tileId) => tileId > DiagonalFlag;

        /// <summary>map.ts getFlippedTile (bit operations on ToInt32 like JavaScript).</summary>
        public static TransformedTile GetFlippedTile(double tileId)
        {
            var bits = JsValue.ToInt32(tileId);
            const int horizontal = unchecked((int)Modules.MapFlags.HORIZONTAL_FLAG);
            const int vertical = (int)Modules.MapFlags.VERTICAL_FLAG;
            const int diagonal = (int)Modules.MapFlags.DIAGONAL_FLAG;

            return new TransformedTile
            {
                TileId = bits & ~(diagonal | vertical | horizontal),
                H = (bits & horizontal) != 0,
                V = (bits & vertical) != 0,
                D = (bits & diagonal) != 0
            };
        }

        public int CoordToIndex(int x, int y) => y * Width + x;

        public (int x, int y) IndexToCoord(int index) => (index % Width, index / Width);

        public bool IsOutOfBounds(int x, int y) => x < 0 || x >= Width || y < 0 || y >= Height;

        /// <summary>map.ts isColliding, including the JavaScript `data &lt; 1` comparison on any cell value.</summary>
        public bool IsColliding(int x, int y, bool all = false)
        {
            if (IsOutOfBounds(x, y)) return true;

            var index = CoordToIndex(x, y);
            if (LessThanOne(Data[index])) return true;

            return Grid[index] == 1 || (all && Grid[index] == 2);
        }

        public bool IsObject(int x, int y) => Objects.Contains(CoordToIndex(x, y));

        public bool IsHighTile(int tileId) => Info.High.Contains(tileId);

        public bool IsAnimatedTile(int tileId) => Info.Animations.ContainsKey(tileId);

        public bool HasCachedData => RegionsLoaded > 0;

        /// <summary>map.ts getTilesetFromId (null when the id is in no tileset).</summary>
        public TilesetRange GetTilesetFromId(int tileId)
        {
            foreach (var tileset in Info.Tilesets)
                if (tileId >= tileset.FirstGid && tileId <= tileset.LastGid)
                    return tileset;
            return null;
        }

        public string GetTileCursor(int x, int y) =>
            CursorTiles.TryGetValue(CoordToIndex(x, y), out var cursor) ? cursor : null;

        /// <summary>map.ts forEachTile: one call per tile of a cell, bottom to top.</summary>
        public static IEnumerable<object> TilesOf(object cell) =>
            cell is List<object> list ? list : (IEnumerable<object>)new[] { cell };

        /// <summary>JavaScript <c>value &lt; 1</c> for the values a cell can hold.</summary>
        static bool LessThanOne(object cell)
        {
            switch (cell)
            {
                case int id:
                    return id < 1;
                case TransformedTile _:
                    return false; // ToNumber("[object Object]") is NaN
                case List<object> list:
                    // ToPrimitive joins the elements with ",": "" → 0, one number → itself, otherwise NaN.
                    if (list.Count == 0) return true;
                    if (list.Count == 1 && list[0] is int single) return single < 1;
                    return false;
                default:
                    return false;
            }
        }

        static bool Truthy(Dictionary<string, object> tile, string key)
        {
            if (!tile.TryGetValue(key, out var value)) return false;
            switch (value)
            {
                case null: return false;
                case bool b: return b;
                case double d: return d != 0 && !double.IsNaN(d);
                case string s: return s.Length > 0;
                default: return true;
            }
        }

        /// <summary>JavaScript own-property order: array-index keys ascending, then the rest in insertion order.</summary>
        static IEnumerable<string> JsPropertyOrder(IEnumerable<string> keys)
        {
            var list = keys.ToList();
            var indexes = list.Where(IsArrayIndex).OrderBy(k => uint.Parse(k, CultureInfo.InvariantCulture));
            return indexes.Concat(list.Where(k => !IsArrayIndex(k))).ToList();
        }

        static bool IsArrayIndex(string key) =>
            uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
            n != uint.MaxValue && n.ToString(CultureInfo.InvariantCulture) == key;

        /// <summary>A cell as JSON.stringify writes it in the original (for comparisons and storage).</summary>
        public static object ToJsValue(object cell)
        {
            switch (cell)
            {
                case TransformedTile t:
                    return new Dictionary<string, object> { ["tileId"] = t.TileId, ["h"] = t.H, ["v"] = t.V, ["d"] = t.D };
                case List<object> list:
                    return list.Select(ToJsValue).ToList();
                default:
                    return cell;
            }
        }
    }
}
