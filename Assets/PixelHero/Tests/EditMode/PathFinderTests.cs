using System.Collections.Generic;
using System.IO;
using System.Linq;
using PixelHero.Editor;
using PixelHero.Js;
using PixelHero.Maps;
using PixelHero.Sprites;
using NUnit.Framework;
using UnityEditor;

namespace PixelHero.Tests
{
    /// <summary>AStar/PathFinder against 3,000 paths from the original client A* on the real grid (gen_astar_cases.mts).</summary>
    public class PathFinderTests
    {
        [Test]
        public void PathsMatchTheOriginalAStar()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);
            var map = new ClientMap(MapInfo.Parse(catalog.MapJson.text));
            map.LoadRegions(RegionCache.FromGzip(catalog.RegionCacheGzip.bytes).GetAllRegions());

            var cases = (List<object>)JsJson.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "astar_cases.json")));
            var finder = new PathFinder();
            var failures = new List<string>();

            foreach (List<object> c in cases)
            {
                int sx = (int)(double)c[0], sy = (int)(double)c[1], ex = (int)(double)c[2], ey = (int)(double)c[3];
                var actual = string.Join(" ", finder.Find(map, sx, sy, ex, ey).Select(p => $"{p[0]},{p[1]}"));
                if (actual != (string)c[4] && failures.Count < 5)
                    failures.Add($"{sx},{sy} → {ex},{ey}: expected [{c[4]}] got [{actual}]");
            }

            Assert.AreEqual(3000, cases.Count);
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }
    }
}
