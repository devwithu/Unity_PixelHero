using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Maps
{
    /// <summary>
    /// The original server's static region data (server cache/regions.json, written by server map/regions.ts
    /// loadRegionCache from world.json), stored gzipped by the importer. It is exactly what the server sends for a
    /// region the player has not loaded yet, minus the player-dependent dynamic tiles, so it can stand in for the
    /// server's Map packets: <see cref="GetRegions"/> returns the same shape <see cref="ClientMap.LoadRegions"/> takes.
    /// </summary>
    public sealed class RegionCache
    {
        public readonly long Version;
        readonly Dictionary<string, object> _data;

        RegionCache(long version, Dictionary<string, object> data)
        {
            Version = version;
            _data = data;
        }

        public int RegionCount => _data.Count;

        public static RegionCache FromGzip(byte[] gzip)
        {
            using var input = new MemoryStream(gzip);
            using var stream = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            stream.CopyTo(output);
            return FromJson(Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length));
        }

        public static RegionCache FromJson(string json)
        {
            var cache = (Dictionary<string, object>)JsJson.Parse(json);
            return new RegionCache((long)(double)cache["version"], (Dictionary<string, object>)cache["data"]);
        }

        public List<object> GetRegion(int region) =>
            _data.TryGetValue(region.ToString(CultureInfo.InvariantCulture), out var tiles) ? (List<object>)tiles : null;

        /// <summary>
        /// Region id → tiles for the given regions, leaving out empty ones like server regions.ts getRegionData does.
        /// </summary>
        public Dictionary<string, object> GetRegions(IEnumerable<int> regions)
        {
            var result = new Dictionary<string, object>();
            foreach (var region in regions)
            {
                var tiles = GetRegion(region);
                if (tiles != null && tiles.Count > 0) result[region.ToString(CultureInfo.InvariantCulture)] = tiles;
            }
            return result;
        }

        public Dictionary<string, object> GetAllRegions()
        {
            var result = new Dictionary<string, object>();
            foreach (var pair in _data) result[pair.Key] = pair.Value;
            return result;
        }
    }

    /// <summary>Region arithmetic of server map/regions.ts (MAP_DIVISION_SIZE squares, row-major ids).</summary>
    public static class Regions
    {
        public static int DivisionSize => Constants.MAP_DIVISION_SIZE;

        public static int SideLength(int mapWidth) => mapWidth / DivisionSize;

        /// <summary>regions.ts getRegion (the index of the region square containing the tile).</summary>
        public static int GetRegion(int x, int y, int mapWidth) =>
            x / DivisionSize + y / DivisionSize * SideLength(mapWidth);

        /// <summary>
        /// regions.ts getSurroundingRegions, in its order: the region, then its horizontal and vertical neighbours.
        /// Empty for an invalid region.
        /// </summary>
        public static List<int> GetSurroundingRegions(int region, int mapWidth, int mapHeight)
        {
            var side = SideLength(mapWidth);
            var count = side * (mapHeight / DivisionSize);
            var surrounding = new List<int> { region };

            if (region < 0 || region > count - 1) return new List<int>();

            bool IsLeftEdge(int r) => r % side == 0;
            bool IsRightEdge(int r) => r % side == side - 1;
            bool IsTopEdge(int r) => r < side;
            bool IsBottomEdge(int r) => r > count - side - 1;

            if (IsLeftEdge(region)) surrounding.Add(region + 1);
            else if (IsRightEdge(region)) surrounding.Add(region - 1);
            else surrounding.AddRange(new[] { region - 1, region + 1 });

            if (IsTopEdge(region) || IsBottomEdge(region))
            {
                var relative = IsTopEdge(region) ? region + side : region - side;
                surrounding.Add(relative);

                if (IsLeftEdge(relative)) surrounding.Add(relative + 1);
                else if (IsRightEdge(relative)) surrounding.Add(relative - 1);
                else surrounding.AddRange(new[] { relative - 1, relative + 1 });
            }
            else if (IsLeftEdge(region))
                surrounding.AddRange(new[] { region - side, region - side + 1, region + side, region + side + 1 });
            else if (IsRightEdge(region))
                surrounding.AddRange(new[] { region - side, region - side - 1, region + side, region + side - 1 });
            else
                surrounding.AddRange(new[]
                {
                    region + side, region - side, region + side - 1, region + side + 1, region - side - 1, region - side + 1
                });

            return surrounding;
        }
    }
}
