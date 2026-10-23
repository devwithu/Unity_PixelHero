using System.Collections.Generic;
using PixelHero.Common;
using PixelHero.Editor;
using PixelHero.Entities;
using PixelHero.Game;
using PixelHero.Js;
using PixelHero.Maps;
using PixelHero.Sprites;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PixelHero.Tests
{
    /// <summary>
    /// The #overlay canvas pixels: the dark mask colour everywhere, cut by each lamp's radial gradient with
    /// destination-out (clear at the centre, the full mask at the radius).
    /// </summary>
    public sealed class OverlayCanvasTests
    {
        [Test]
        public void TheDarkMaskIsCutByEachLamp()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);
            var map = new ClientMap(MapInfo.Parse(catalog.MapJson.text));
            var world = new World(new ManualClock { Now = 0 }, map, SpriteDefinitions.ParseAll(catalog.SpritesJson.text, map.Info.TileSize), _ => false);
            var camera = new Entities.Camera(map.Width, map.Height, map.Info.TileSize, 320, 240) { ZoomFactor = 4 };
            camera.Update();
            world.Camera = camera;
            world.Lights.CanvasWidth = 320;
            world.Lights.CanvasHeight = 240;

            world.Lights.HandleOverlay(Opcodes.Overlay.Set, new Dictionary<string, object> { ["image"] = "blank", ["colour"] = "rgba(0, 0, 0, 0.75)" });
            // A lamp at tile (2, 1): centre (2 × 16 + 8, 1 × 16 + 8) × zoom 4 = (160, 96); no flicker; t = floor(50 × 1.4) = 70.
            world.Lights.AddLight(new Dictionary<string, object>
            {
                ["instance"] = "1", ["x"] = 2.0, ["y"] = 1.0, ["colour"] = "white", ["diffuse"] = 0.2, ["distance"] = 50.0,
                ["flickerSpeed"] = -1.0, ["flickerIntensity"] = -1.0
            });

            var stamps = world.Lights.Draw();
            Assert.AreEqual("70 90 26", string.Join(" | ", stamps));

            using var renderer = new OverlayCanvasRenderer(catalog, 320, 240);
            renderer.Render(world.Lights);
            var pixels = Read(renderer.Overlay);

            byte Alpha(int x, int y) => pixels[(239 - y) * 320 + x].a;
            Assert.AreEqual(191, Alpha(5, 230), 1, "the mask colour away from the lamp");
            Assert.AreEqual(0, Alpha(160, 96), 2, "clear at the lamp's centre");
            // Half way out: the gradient's alpha is 0.5, so 0.75 × (1 − 0.5) is left.
            Assert.AreEqual(96, Alpha(160 + 35, 96), 4, "half the mask half way out");
            Assert.AreEqual(96, Alpha(160, 96 + 35), 4, "the same below the centre (not flipped)");
            Assert.AreEqual(191, Alpha(160, 96 + 75), 1, "the full mask past the radius");
        }

        static Color32[] Read(RenderTexture texture)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = texture;
            var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            var pixels = copy.GetPixels32();
            Object.DestroyImmediate(copy);
            return pixels;
        }
    }
}
