using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PixelHero.Editor;
using PixelHero.Js;
using PixelHero.Maps;
using PixelHero.Sprites;
using NUnit.Framework;
using UnityEditor;

namespace PixelHero.Tests
{
    /// <summary>
    /// S4b: TileDrawPlanner against the draws the original Canvas renderer makes for every cell of the whole region
    /// cache (Tools/fixtures/gen_tile_render_cases.mts), statically and one animation frame later.
    /// </summary>
    public class TileRenderTests
    {
        static Dictionary<string, object> cases;

        static Dictionary<string, object> Cases =>
            cases ??= (Dictionary<string, object>)JsJson.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "tile_render_cases.json")));

        static SpriteCatalog Catalog => AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);

        static ClientMap LoadedMap()
        {
            var map = new ClientMap(MapInfo.Parse(Catalog.MapJson.text));
            map.LoadRegions(RegionCache.FromGzip(Catalog.RegionCacheGzip.bytes).GetAllRegions());
            return map;
        }

        static Dictionary<int, int> TilesetWidths(ClientMap map) =>
            map.Info.Tilesets.ToDictionary(t => t.FirstGid, t => Catalog.GetTexture(t.TextureKey).width);

        [Test]
        public void StaticDrawsMatchTheOriginalCanvas()
        {
            var map = LoadedMap();
            var planner = new TileDrawPlanner(map, TilesetWidths(map), () => 0) { AnimateTiles = false };
            var expected = (Dictionary<string, object>)Cases["static"];

            var cells = Describe(map, planner, out var drawCount);

            Assert.AreEqual((double)Cases["cells"], cells.Count, "cells");
            Assert.AreEqual((double)expected["draws"], drawCount, "draws");
            AssertSamples(expected, cells);
            Assert.AreEqual((string)expected["sha256"], Sha256(string.Join("\n", cells)), "all draws");
        }

        [Test]
        public void AnimatedDrawsMatchTheOriginalCanvasOneFrameLater()
        {
            var map = LoadedMap();
            var now = 1_000_000.0;
            var planner = new TileDrawPlanner(map, TilesetWidths(map), () => now) { AnimateTiles = true };
            var expected = (Dictionary<string, object>)Cases["animated"];

            // The first pass meets every animated tile for the first time and draws it (the original drew nothing for
            // it; fixed in both, ANALYSIS.md §9 #12).
            var first = Describe(map, planner, out _);
            var firstExpected = (Dictionary<string, object>)Cases["firstPass"];
            AssertSamples(firstExpected, first);
            Assert.AreEqual((string)firstExpected["sha256"], Sha256(string.Join("\n", first)), "first pass");

            foreach (var tile in planner.AnimatedTiles.Values)
            {
                tile.Animate(now + 1e6); // frame 0: the stored tile again
                tile.Animate(now + 2e6); // frame 1
            }

            var cells = Describe(map, planner, out _);

            Assert.AreEqual((double)expected["animatedTiles"], planner.AnimatedTiles.Count, "animated tiles");
            AssertSamples(expected, cells);
            Assert.AreEqual((string)expected["sha256"], Sha256(string.Join("\n", cells)), "all draws");
        }

        [Test]
        public void EveryFlipCombinationAndArrayQuirkMatchesTheOriginalCanvas()
        {
            var map = new ClientMap(MapInfo.Parse(Catalog.MapJson.text));
            var planner = new TileDrawPlanner(map, TilesetWidths(map), () => 0) { AnimateTiles = false };
            var size = map.Info.TileSize;
            var draws = new List<TileDraw>();

            foreach (Dictionary<string, object> c in (List<object>)Cases["synthetic"])
            {
                var index = (int)(double)c["index"];
                draws.Clear();
                planner.ParseTile(map.ParseTileData(c["data"]), index, draws);

                var actual = string.Join("|", draws.Select(d => string.Join(" ",
                    d.Layer.ToString().ToLowerInvariant(), d.Tileset.FirstGid, d.Sx, d.Sy, size, size,
                    d.Ux, d.Uy, d.Vx, d.Vy, d.OffsetX, d.OffsetY)));
                Assert.AreEqual((string)c["draws"], actual, JsJson.Stringify(c["data"]));
            }
        }

        [Test]
        public void EveryDrawCoversItsCellExactly()
        {
            // Lets the Unity side place each tile at its cell centre and only rotate/flip it.
            var map = LoadedMap();
            var planner = new TileDrawPlanner(map, TilesetWidths(map), () => 0) { AnimateTiles = false };
            var size = map.Info.TileSize;
            var transforms = new HashSet<string>();

            for (var index = 0; index < map.Data.Length; index++)
            {
                if (map.Data[index] is int id && id == 0) continue;
                foreach (var draw in planner.Plan(index))
                {
                    var left = draw.OffsetX + System.Math.Min(0, draw.Ux * size) + System.Math.Min(0, draw.Vx * size);
                    var top = draw.OffsetY + System.Math.Min(0, draw.Uy * size) + System.Math.Min(0, draw.Vy * size);
                    Assert.AreEqual((0, 0), (left, top), $"cell {index}");
                    transforms.Add($"{draw.Ux},{draw.Uy},{draw.Vx},{draw.Vy}");
                }
            }

            Assert.Greater(transforms.Count, 1, "the map has flipped tiles");
        }

        static List<string> Describe(ClientMap map, TileDrawPlanner planner, out int drawCount)
        {
            var cells = new List<string>();
            var draws = new List<TileDraw>();
            var size = map.Info.TileSize;
            drawCount = 0;

            for (var index = 0; index < map.Data.Length; index++)
            {
                if (map.Data[index] is int id && id == 0) continue;

                draws.Clear();
                planner.ParseTile(map.Data[index], index, draws);
                drawCount += draws.Count;

                var text = string.Join("|", draws.Select(d => string.Join(" ",
                    d.Layer.ToString().ToLowerInvariant(), d.Tileset.FirstGid, d.Sx, d.Sy, size, size,
                    d.Ux, d.Uy, d.Vx, d.Vy, d.OffsetX, d.OffsetY)));
                cells.Add(index.ToString(CultureInfo.InvariantCulture) + ":" + text);
            }

            return cells;
        }

        static void AssertSamples(Dictionary<string, object> expected, List<string> cells)
        {
            var byIndex = cells.ToDictionary(c => c.Substring(0, c.IndexOf(':')));
            foreach (string sample in (List<object>)expected["samples"])
            {
                var key = sample.Substring(0, sample.IndexOf(':'));
                Assert.AreEqual(sample, byIndex[key], $"cell {key}");
            }
        }

        static string Sha256(string text)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }
    }
}
