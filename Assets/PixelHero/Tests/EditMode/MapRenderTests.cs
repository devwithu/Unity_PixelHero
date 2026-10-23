using System.IO;
using PixelHero.Editor;
using NUnit.Framework;
using UnityEngine;

namespace PixelHero.Tests
{
    /// <summary>
    /// S4b: Unity's render of a map window (MapView on tilemaps, through a camera) against the image made by
    /// replaying the original Canvas renderer's drawImage calls (Tools/fixtures/gen_map_reference.mts).
    /// The Unity image and a difference image are written to Snapshots/ for a look.
    /// </summary>
    public class MapRenderTests
    {
        const int Tolerance = 2; // per channel, for 8-bit alpha blending differences

        [TestCase(117, 550, 32, 24)] // tutorial room
        [TestCase(312, 880, 32, 24)] // Mudwich spawn (Constants.SPAWN_POINT 328,892): trees, high tiles
        [TestCase(405, 905, 32, 24)] // H+V flipped tiles at 421-432,919-921
        [TestCase(1112, 690, 32, 24)] // H flipped tiles at 1126-1132,703-704
        [TestCase(98, 114, 32, 24)] // V+D flipped tiles at 111-112,126-127
        public void WindowMatchesTheOriginalCanvasDraws(int x0, int y0, int cols, int rows)
        {
            var referencePath = Path.Combine(TestPaths.Fixtures, $"map_reference_{x0}_{y0}_{cols}x{rows}.png");
            var reference = new Texture2D(2, 2);
            Assert.IsTrue(reference.LoadImage(File.ReadAllBytes(referencePath)), referencePath);

            var actual = MapSnapshot.Render(x0, y0, cols, rows);
            Assert.AreEqual((reference.width, reference.height), (actual.width, actual.height));

            var expectedPixels = reference.GetPixels32();
            var actualPixels = actual.GetPixels32();
            var diff = new Color32[actualPixels.Length];
            var mismatches = 0;
            var firstMismatch = "";

            for (var i = 0; i < actualPixels.Length; i++)
            {
                var e = expectedPixels[i];
                var a = actualPixels[i];
                var differs = Mathf.Abs(e.r - a.r) > Tolerance || Mathf.Abs(e.g - a.g) > Tolerance || Mathf.Abs(e.b - a.b) > Tolerance;
                diff[i] = differs ? new Color32(255, 0, 255, 255) : new Color32((byte)(a.r / 3), (byte)(a.g / 3), (byte)(a.b / 3), 255);
                if (!differs) continue;

                if (mismatches == 0)
                {
                    // Both textures are bottom-up; report in canvas coordinates (top-down).
                    var x = i % actual.width;
                    var y = actual.height - 1 - i / actual.width;
                    firstMismatch = $"first at canvas pixel ({x},{y}): expected {e}, got {a}";
                }
                mismatches++;
            }

            var outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Snapshots"));
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, $"map_{x0}_{y0}_unity.png"), actual.EncodeToPNG());
            var diffImage = new Texture2D(actual.width, actual.height, TextureFormat.RGBA32, false);
            diffImage.SetPixels32(diff);
            File.WriteAllBytes(Path.Combine(outDir, $"map_{x0}_{y0}_diff.png"), diffImage.EncodeToPNG());

            Assert.AreEqual(0, mismatches, $"{mismatches} of {actualPixels.Length} pixels differ; {firstMismatch}");
        }
    }
}
