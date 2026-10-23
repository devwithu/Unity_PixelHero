using System.IO;
using System.Linq;
using PixelHero.Editor;
using PixelHero.Sprites;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PixelHero.Tests
{
    /// <summary>
    /// Sprite definitions (recorded in Tools/fixtures/out/sprite_cases.json) and the import settings of the
    /// textures in Assets/PixelHero/Generated.
    /// </summary>
    public class SpriteImportTests
    {
        static JObject fixture;

        static JObject Fixture =>
            fixture ??= JObject.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "sprite_cases.json")));

        static SpriteCatalog Catalog => AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);

        [Test]
        public void DefinitionsMatchTheOriginalSpriteClass()
        {
            var expected = (JArray)Fixture["sprites"];
            var actual = SpriteDefinitions.ParseAll(Catalog.SpritesJson.text, (int)Fixture["tileSize"]);

            Assert.AreEqual(expected.Count, actual.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                var e = expected[i];
                var a = actual[i];
                var label = $"#{i} {a.Key}";

                Assert.AreEqual((string)e["key"], a.Key, label);
                Assert.AreEqual((string)e["path"], a.Path, label);
                Assert.AreEqual((int)e["width"], a.Width, $"{label} width");
                Assert.AreEqual((int)e["height"], a.Height, $"{label} height");
                Assert.AreEqual((int)e["offsetX"], a.OffsetX, $"{label} offsetX");
                Assert.AreEqual((int)e["offsetY"], a.OffsetY, $"{label} offsetY");
                Assert.AreEqual((int)e["idleSpeed"], a.IdleSpeed, $"{label} idleSpeed");
                Assert.AreEqual((bool)e["preload"], a.Preload, $"{label} preload");
                Assert.AreEqual((bool)e["hasDeathAnimation"], a.HasDeathAnimation, $"{label} death");
                Assert.AreEqual((bool)e["hasHurtSprite"], a.HasHurtSprite, $"{label} hurt");
                Assert.AreEqual((bool)e["hasSilhouette"], a.HasSilhouette, $"{label} silhouette");

                var animations = (JArray)e["animations"];
                Assert.AreEqual(animations.Count, a.Animations.Count, $"{label} animation count");
                for (var k = 0; k < animations.Count; k++)
                {
                    var ea = animations[k];
                    var aa = a.Animations[k];
                    Assert.AreEqual((string)ea["name"], aa.Name, $"{label} animation #{k}");
                    Assert.AreEqual((int)ea["length"], aa.Length, $"{label} {aa.Name} length");
                    Assert.AreEqual((int)ea["row"], aa.Row, $"{label} {aa.Name} row");
                    Assert.AreEqual((int)ea["width"], aa.Width, $"{label} {aa.Name} width");
                    Assert.AreEqual((int)ea["height"], aa.Height, $"{label} {aa.Name} height");
                }
            }
        }

        [Test]
        public void RepeatedKeysKeepTheLastEntry()
        {
            var all = SpriteDefinitions.ParseAll(Catalog.SpritesJson.text, 16);
            var byKey = SpriteDefinitions.Parse(Catalog.SpritesJson.text, 16);

            var repeated = all.GroupBy(s => s.Key).Where(g => g.Count() > 1).ToList();
            Assert.IsNotEmpty(repeated, "sprites.json no longer repeats keys; drop this test");

            foreach (var group in repeated)
                Assert.AreEqual(Describe(group.Last()), Describe(byKey[group.Key]), group.Key);

            Assert.AreEqual(all.Select(s => s.Key).Distinct().Count(), byKey.Count);
        }

        static string Describe(SpriteInfo s) =>
            $"{s.Key} {s.Width}x{s.Height} {s.OffsetX},{s.OffsetY} idle {s.IdleSpeed} preload {s.Preload} " +
            string.Join(" ", s.Animations.Select(a => $"{a.Name}:{a.Length}@{a.Row}"));

        [Test]
        public void TexturesUseExactPixels()
        {
            foreach (var entry in Catalog.Textures)
            {
                var texture = entry.Texture;
                Assert.AreEqual(FilterMode.Point, texture.filterMode, entry.Key);
                Assert.AreEqual(1, texture.mipmapCount, entry.Key);
                // Uncompressed; PNGs without an alpha channel (e.g. projectiles/none, a 1-bit palette) come in as RGB24.
                Assert.That(texture.format, Is.EqualTo(TextureFormat.RGBA32).Or.EqualTo(TextureFormat.RGB24), entry.Key);
                Assert.AreEqual(TextureWrapMode.Clamp, texture.wrapMode, entry.Key);
            }
        }
    }
}
