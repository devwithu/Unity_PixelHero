using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PixelHero.Editor;
using PixelHero.Js;
using PixelHero.Maps;
using PixelHero.Network;
using PixelHero.Sprites;
using NUnit.Framework;
using UnityEditor;

namespace PixelHero.Tests
{
    /// <summary>
    /// S4a: ClientMap against the original client Map class run in Node (Tools/fixtures/gen_client_map_cases.mts),
    /// the imported region cache against the server's, and region arithmetic against server regions.ts.
    /// </summary>
    public class MapDataTests
    {
        static Dictionary<string, object> mapCases;
        static Dictionary<string, object> regionCases;

        static Dictionary<string, object> MapCases =>
            mapCases ??= (Dictionary<string, object>)JsJson.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "client_map_cases.json")));

        static Dictionary<string, object> RegionCases =>
            regionCases ??= (Dictionary<string, object>)JsJson.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "region_cases.json")));

        static SpriteCatalog Catalog => AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);

        static ClientMap NewMap() => new ClientMap(MapInfo.Parse(Catalog.MapJson.text));

        static RegionCache cache;

        static RegionCache LoadCache() => cache ??= RegionCache.FromGzip(Catalog.RegionCacheGzip.bytes);

        [Test]
        public void RecordedMapPacketsLeaveTheSameStateAsTheOriginal()
        {
            var map = NewMap();
            var packets = 0;

            foreach (var file in Directory.GetFiles(TestPaths.Recordings, "*.jsonl").OrderBy(f => Path.GetFileName(f), System.StringComparer.Ordinal))
            foreach (var line in File.ReadLines(file).Where(l => l.Trim().Length > 0))
            {
                var entry = (Dictionary<string, object>)JsJson.Parse(line);
                if (!entry.TryGetValue("dir", out var dir) || (string)dir != "s2c") continue;

                foreach (var packet in PacketFrames.Parse((string)entry["data"]).Where(p => p.Id == Common.Packets.Map))
                {
                    map.LoadRegions(MapPayload.Decode((string)packet.Arg(0)));
                    packets++;
                }
            }

            var expected = (Dictionary<string, object>)MapCases["recording"];
            Assert.AreEqual((double)expected["packets"], packets);
            AssertSameState(expected, map);
        }

        [Test]
        public void TheWholeRegionCacheLeavesTheSameStateAsTheOriginal()
        {
            var map = NewMap();
            map.LoadRegions(LoadCache().GetAllRegions());
            AssertSameState((Dictionary<string, object>)MapCases["cache"], map);
        }

        [Test]
        public void RegionCacheMatchesTheServersRegionByRegion()
        {
            var cache = LoadCache();
            var expected = (List<object>)MapCases["regions"];

            Assert.AreEqual((double)MapCases["cacheVersion"], cache.Version);
            Assert.AreEqual(GameVersion.MapVersion, cache.Version);
            Assert.AreEqual(expected.Count, cache.RegionCount);

            foreach (Dictionary<string, object> region in expected)
            {
                var id = (int)(double)region["id"];
                var tiles = cache.GetRegion(id);
                Assert.AreEqual((double)region["tiles"], tiles.Count, $"region {id}");
                Assert.AreEqual((string)region["sha256"], Sha256(JsJson.Stringify(tiles)), $"region {id}");
            }
        }

        [Test]
        public void LookupsMatchTheOriginal()
        {
            var map = NewMap();
            var lookups = (Dictionary<string, object>)MapCases["lookups"];
            var ids = Enumerable.Range(0, 20000).ToList();

            CollectionAssert.AreEqual(Ints(lookups["high"]), ids.Where(map.IsHighTile).ToList(), "high");
            CollectionAssert.AreEqual(Ints(lookups["animated"]), ids.Where(map.IsAnimatedTile).ToList(), "animated");
            CollectionAssert.AreEqual(Ints(lookups["tilesets"]), ids.Select(id => map.GetTilesetFromId(id)?.FirstGid ?? -1).ToList(), "tilesets");

            foreach (Dictionary<string, object> check in (List<object>)lookups["flippedChecks"])
            {
                var id = (double)check["id"];
                Assert.AreEqual((bool)check["flipped"], ClientMap.IsFlippedTileId(id), $"flipped {id}");
                Assert.AreEqual(JsJson.Stringify(check["parsed"]), JsJson.Stringify(ClientMap.ToJsValue(map.ParseTileData(id))), $"parse {id}");
                Assert.AreEqual(JsJson.Stringify(check["parsedArray"]),
                    JsJson.Stringify(ClientMap.ToJsValue(map.ParseTileData(new List<object> { id, 5.0 }))), $"parse [{id},5]");
            }
        }

        [Test]
        public void CollisionComparisonFollowsJavaScript()
        {
            var map = NewMap();
            map.Grid[0] = 0;

            foreach (Dictionary<string, object> probe in (List<object>)((Dictionary<string, object>)MapCases["lookups"])["collidingQuirks"])
            {
                map.Data[0] = ToCell(map, probe["data"]);
                Assert.AreEqual((bool)probe["colliding"], map.IsColliding(0, 0), JsJson.Stringify(probe["data"]));
            }
        }

        [Test]
        public void RegionArithmeticMatchesTheServer()
        {
            var width = (int)(double)RegionCases["width"];
            var height = (int)(double)RegionCases["height"];
            var surrounding = (List<object>)RegionCases["surrounding"];

            for (var region = 0; region < surrounding.Count; region++)
                CollectionAssert.AreEqual(Ints(surrounding[region]), Regions.GetSurroundingRegions(region, width, height), $"region {region}");

            CollectionAssert.IsEmpty(Regions.GetSurroundingRegions(-1, width, height));
            CollectionAssert.IsEmpty(Regions.GetSurroundingRegions(surrounding.Count, width, height));

            foreach (List<object> tile in (List<object>)RegionCases["tiles"])
                Assert.AreEqual((double)tile[2], Regions.GetRegion((int)(double)tile[0], (int)(double)tile[1], width), $"tile {tile[0]},{tile[1]}");
        }

        static void AssertSameState(Dictionary<string, object> expected, ClientMap map)
        {
            var loaded = map.Data.Count(cell => !(cell is int id && id == 0));
            Assert.AreEqual((double)expected["loadedCells"], loaded, "loaded cells");

            var data = JsJson.Stringify(map.Data.Select(ClientMap.ToJsValue).ToList());
            Assert.AreEqual((string)expected["dataSha256"], Sha256(data), "data");

            var grid = new StringBuilder();
            var colliding = new StringBuilder();
            for (var y = 0; y < map.Height; y++)
            {
                if (y > 0) grid.Append('\n');
                for (var x = 0; x < map.Width; x++)
                {
                    grid.Append(map.Grid[map.CoordToIndex(x, y)]);
                    colliding.Append(map.IsColliding(x, y) ? '1' : '0');
                }
            }
            Assert.AreEqual((string)expected["gridSha256"], Sha256(grid.ToString()), "grid");
            Assert.AreEqual((string)expected["collidingSha256"], Sha256(colliding.ToString()), "isColliding");

            Assert.AreEqual(JsJson.Stringify(expected["objects"]), JsJson.Stringify(map.Objects), "objects");
            Assert.AreEqual(JsJson.Stringify(expected["cursorTiles"]),
                JsJson.Stringify(map.CursorTiles.ToDictionary(p => p.Key.ToString(), p => (object)p.Value)), "cursorTiles");
            Assert.AreEqual(JsJson.Stringify(expected["dynamicAnimatedTiles"]),
                JsJson.Stringify(map.DynamicAnimatedTiles.ToDictionary(p => p.Key.ToString(), p => ClientMap.ToJsValue(p.Value))), "dynamicAnimatedTiles");

            foreach (List<object> sample in (List<object>)expected["samples"])
            {
                var index = (int)(double)sample[0];
                Assert.AreEqual(JsJson.Stringify(sample[1]), JsJson.Stringify(ClientMap.ToJsValue(map.Data[index])), $"cell {index}");
            }
        }

        /// <summary>A probe value from the fixture as a ClientMap cell.</summary>
        static object ToCell(ClientMap map, object value)
        {
            switch (value)
            {
                case double number: return (int)number;
                case List<object> list: return list.Select(v => ToCell(map, v)).ToList();
                case Dictionary<string, object> t:
                    return new TransformedTile { TileId = (int)(double)t["tileId"], H = (bool)t["h"], V = (bool)t["v"], D = (bool)t["d"] };
                default: return value;
            }
        }

        static List<int> Ints(object list) => ((List<object>)list).Select(v => (int)(double)v).ToList();

        static string Sha256(string text)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }
    }
}
