using PixelHero.Entities;
using PixelHero.UI;
using NUnit.Framework;
using UnityEngine;

namespace PixelHero.Tests
{
    /// <summary>The colour strings the original's text canvas and styles use.</summary>
    public class CssColourTests
    {
        [TestCase("white", 255, 255, 255, 255)]
        [TestCase("#000000", 0, 0, 0, 255)]
        [TestCase("#db963c", 219, 150, 60, 255)]
        [TestCase("#fff", 255, 255, 255, 255)]
        [TestCase("#37373780", 55, 55, 55, 128)]
        [TestCase("rgba(252,218,92, 1)", 252, 218, 92, 255)]
        [TestCase("rgba(253, 0, 0, 1)", 253, 0, 0, 255)]
        [TestCase("rgb(255, 64, 64)", 255, 64, 64, 255)]
        [TestCase("rgba(0, 0, 0, 0.84)", 0, 0, 0, 214)]
        public void ParsesCanvasColours(string text, int r, int g, int b, int a)
        {
            Assert.IsTrue(CssColour.TryParse(text, out var colour), text);
            Assert.AreEqual(new Color32((byte)r, (byte)g, (byte)b, (byte)a), colour, text);
        }

        [TestCase("rbga(0, 0, 0, 1)")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("#12345")]
        public void RejectsWhatTheCanvasIgnores(string text)
        {
            Assert.IsFalse(CssColour.TryParse(text, out _), text);
            Assert.IsFalse(TextContext.IsValidColour(text), text);
        }
    }
}
