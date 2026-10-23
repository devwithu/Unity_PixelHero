using System.Collections.Generic;
using System.IO;
using PixelHero.Editor;
using PixelHero.Entities;
using PixelHero.Js;
using PixelHero.Maps;
using PixelHero.Sprites;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PixelHero.Tests
{
    /// <summary>
    /// S5c: Unity's entity canvases (EntityCanvasRenderer) for frames of the synthetic draw scene against the images
    /// made by replaying the original renderer's draw calls (Tools/fixtures/gen_entity_canvas_reference.mts).
    /// Unity images and difference images go to Snapshots/.
    /// </summary>
    public class EntityRenderTests
    {
        const double T0 = 1_700_000_000_000;

        [TestCase(50)]
        [TestCase(150)]
        public void CanvasesMatchTheOriginalDraws(int targetFrame)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);
            var map = new ClientMap(MapInfo.Parse(catalog.MapJson.text));
            map.LoadRegions(RegionCache.FromGzip(catalog.RegionCacheGzip.bytes).GetAllRegions());

            var clock = new ManualClock { Now = T0 };
            var world = new World(clock, map, SpriteDefinitions.ParseAll(catalog.SpritesJson.text, map.Info.TileSize),
                key => catalog.GetTexture("sprites/" + key) != null);
            var packets = new EntityPackets(world);
            var camera = new PixelHero.Entities.Camera(map.Width, map.Height, map.Info.TileSize, 960, 640) { ZoomFactor = 3 };
            camera.Update();
            world.Camera = camera;
            world.PerformanceNow = () => clock.Now - T0 + 1000;
            var planner = new EntityDrawPlanner(world, camera);

            var frames = EntityReplayTests.SceneFrames("entity_draw_scene");
            List<EntityDraw> draws = null;
            var next = 0;

            for (var frame = 0; frame <= targetFrame; frame++)
            {
                clock.Now = T0 + frame * 16;
                world.Timers.RunDue();
                while (next < frames.Count && frames[next].t <= frame * 16)
                {
                    foreach (var packet in frames[next].packets) packets.Handle(packet.Id, packet.Args);
                    next++;
                }

                world.Time = clock.Now;
                if (!world.Player.Dead)
                {
                    packets.UpdateEntities();
                    packets.UpdateKeyboard();
                    packets.UpdateAnimations();
                }

                draws = planner.Draw();
            }

            using var renderer = new EntityCanvasRenderer(catalog, 960, 640);
            renderer.Render(draws);

            var failures = new List<string>();
            foreach (var (name, canvas) in new[] { ("entities", renderer.Entities), ("fore", renderer.Fore) })
            {
                var message = Compare(canvas, Path.Combine(TestPaths.Fixtures, $"entity_canvas_{targetFrame}_{name}.png"), $"entity_{targetFrame}_{name}");
                if (message != null) failures.Add($"{name}: {message}");
            }

            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        /// <summary>Compares a premultiplied canvas with a straight-alpha reference PNG; null when they match.</summary>
        static string Compare(RenderTexture canvas, string referencePath, string snapshotName)
        {
            var reference = new Texture2D(2, 2);
            Assert.IsTrue(reference.LoadImage(File.ReadAllBytes(referencePath)), referencePath);

            var previous = RenderTexture.active;
            RenderTexture.active = canvas;
            var actual = new Texture2D(canvas.width, canvas.height, TextureFormat.RGBA32, false, true);
            actual.ReadPixels(new Rect(0, 0, canvas.width, canvas.height), 0, 0);
            actual.Apply();
            RenderTexture.active = previous;

            var expected = reference.GetPixels32();
            var pixels = actual.GetPixels32();
            var straight = new Color32[pixels.Length];
            var diff = new Color32[pixels.Length];
            var mismatches = 0;
            string first = null;

            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                straight[i] = p.a == 0 ? new Color32(0, 0, 0, 0)
                    : new Color32(Unpremultiply(p.r, p.a), Unpremultiply(p.g, p.a), Unpremultiply(p.b, p.a), p.a);

                var e = expected[i];
                var a = straight[i];
                // Colour precision falls with alpha (8-bit premultiplied storage), so the tolerance grows as alpha drops.
                var colourTolerance = a.a == 0 && e.a == 0 ? 255 : 2 + 255 / Mathf.Max(1, Mathf.Min(a.a, e.a));
                var differs = Mathf.Abs(e.a - a.a) > 2 ||
                              Mathf.Abs(e.r - a.r) > colourTolerance || Mathf.Abs(e.g - a.g) > colourTolerance || Mathf.Abs(e.b - a.b) > colourTolerance;

                diff[i] = differs ? new Color32(255, 0, 255, 255) : new Color32((byte)(a.r / 3), (byte)(a.g / 3), (byte)(a.b / 3), 255);
                if (!differs) continue;

                if (first == null)
                {
                    var x = i % canvas.width;
                    var y = canvas.height - 1 - i / canvas.width;
                    first = $"first at canvas pixel ({x},{y}): expected {e}, got {a}";
                }
                mismatches++;
            }

            var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Snapshots"));
            Directory.CreateDirectory(outDir);
            var straightImage = new Texture2D(canvas.width, canvas.height, TextureFormat.RGBA32, false);
            straightImage.SetPixels32(straight);
            File.WriteAllBytes(Path.Combine(outDir, snapshotName + "_unity.png"), straightImage.EncodeToPNG());
            var diffImage = new Texture2D(canvas.width, canvas.height, TextureFormat.RGBA32, false);
            diffImage.SetPixels32(diff);
            File.WriteAllBytes(Path.Combine(outDir, snapshotName + "_diff.png"), diffImage.EncodeToPNG());

            return mismatches == 0 ? null : $"{mismatches} of {pixels.Length} pixels differ; {first}";
        }

        static byte Unpremultiply(byte value, byte alpha) => (byte)Mathf.Min(255, Mathf.RoundToInt(value * 255f / alpha));
    }
}
